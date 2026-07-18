using Noesis;
using Paradise.Sample.Game.Ui;
using IoPath = System.IO.Path;

namespace Paradise.Sample.Ui;

/// <summary>The renderer-independent half of NoesisGUI in the two-half UI architecture,
/// shared by every host (the SDL/WebGPU runtime, the Godot play-mode bridge):
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread — the simulation
///   drains pointer events into the view and advances view time each fixed tick, so hover,
///   focus, animations and bindings step in lockstep with game state.
/// - The host's render half (a WebGPU overlay pass, or an offscreen render + readback) reads
///   <see cref="View"/> once published, initializes its own <c>RenderDevice</c> against it,
///   and calls <see cref="TryUpdateRenderTree"/> once per frame before recording the passes.
///
/// The two halves meet at exactly one point, per Noesis's threading model: view updates
/// (sim) and <c>UpdateRenderTree</c> (render) are mutually excluded by the internal sync
/// lock — which is why the render half goes through <see cref="TryUpdateRenderTree"/> instead
/// of touching the renderer directly; <c>Renderer.Init/RenderOffscreen/Render</c> touch only
/// render-side state and deliberately stay outside the lock.
///
/// Noesis pins each View to the Dispatcher of its CREATION thread, so all GUI construction
/// (native init, providers rooted at the XAML's directory, optional
/// <c>Theme/NoesisTheme.DarkBlue.xaml</c>, view creation) happens LAZILY on the sim thread at
/// the first tick; render halves wait (skipping frames) until <see cref="View"/> is
/// published.</summary>
// Lifetime: process-scoped by design — no Dispose/GUI.Shutdown. Hosts create at most one
// NoesisViewCore per process and native/GPU teardown happens at exit; add disposal if this
// ever hosts multiple sessions (tests, editor).
public sealed class NoesisViewCore
{
    private readonly string _root;
    private readonly string _xamlFile;
    private readonly object _sync = new();
    private volatile View? _view; // published by the sim thread once created there
    private volatile uint _width;
    private volatile uint _height;

    public IUiInput Input { get; }

    /// <summary>The Noesis view, once the sim thread has created it; null until then (render
    /// halves skip frames). Volatile read.</summary>
    public View? View => _view;

    /// <summary>Current view size in UI pixels — tracks sim-side Resize events. Volatile.</summary>
    public uint Width => _width;
    public uint Height => _height;

    public NoesisViewCore(string xamlPath, uint pixelWidth, uint pixelHeight)
    {
        _root = IoPath.GetDirectoryName(IoPath.GetFullPath(xamlPath)) ?? ".";
        _xamlFile = IoPath.GetFileName(xamlPath);
        _width = pixelWidth;
        _height = pixelHeight;
        Input = new UiInputHalf(this);
    }

    /// <summary>The single sync point between the halves: pick up the last sim-thread view
    /// update into the render tree. False while the view does not exist yet. Call from the
    /// render/main thread once per frame, before recording the UI passes.</summary>
    public bool TryUpdateRenderTree()
    {
        var view = _view;
        if (view is null) return false;
        lock (_sync)
        {
            view.Renderer.UpdateRenderTree();
        }
        return true;
    }

    /// <summary>Sim-thread (lazy) construction: the View's Dispatcher binds here, making the
    /// sim thread the UI thread for its whole lifetime.</summary>
    private View CreateViewOnSimThread()
    {
        Log.SetLogCallback(static (level, channel, message) =>
        {
            if (level >= Noesis.LogLevel.Warning) Console.WriteLine($"[noesis {level}] {message}");
        });
        var licenseName = Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        var licenseKey = Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(licenseName) && !string.IsNullOrWhiteSpace(licenseKey))
        {
            GUI.SetLicense(licenseName, licenseKey);
        }
        GUI.Init();
        GUI.SetXamlProvider(new FolderXamlProvider(_root));
        GUI.SetTextureProvider(new FolderTextureProvider(_root));
        GUI.SetFontProvider(new FolderFontProvider(_root));
        GUI.SetFontDefaultProperties(14.0f, FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
        if (File.Exists(IoPath.Combine(_root, "Theme", "NoesisTheme.DarkBlue.xaml")))
        {
            GUI.SetFontFallbacks(["Theme/Fonts/#PT Root UI", "Arial"]);
            GUI.LoadApplicationResources("Theme/NoesisTheme.DarkBlue.xaml");
        }

        var rootElement = (FrameworkElement)GUI.LoadXaml(_xamlFile);
        var view = GUI.CreateView(rootElement);
        view.SetFlags(RenderFlags.PPAA);
        view.SetSize((int)_width, (int)_height);
        Console.WriteLine($"[NoesisUi] '{_xamlFile}' loaded from {_root} ({_width}x{_height}) on the sim thread.");
        return view;
    }

    private sealed class UiInputHalf(NoesisViewCore owner) : IUiInput
    {
        private View SimView => owner._view ??= owner.CreateViewOnSimThread();

        public bool Handle(in UiEvent uiEvent)
        {
            lock (owner._sync)
            {
                var view = SimView;
                switch (uiEvent.Kind)
                {
                    case UiEventKind.PointerMove:
                        return view.MouseMove((int)uiEvent.X, (int)uiEvent.Y);
                    case UiEventKind.PointerDown:
                        return view.MouseButtonDown((int)uiEvent.X, (int)uiEvent.Y, ToNoesis(uiEvent.Button));
                    case UiEventKind.PointerUp:
                        return view.MouseButtonUp((int)uiEvent.X, (int)uiEvent.Y, ToNoesis(uiEvent.Button));
                    case UiEventKind.Resize:
                        owner._width = (uint)uiEvent.X;
                        owner._height = (uint)uiEvent.Y;
                        view.SetSize((int)uiEvent.X, (int)uiEvent.Y);
                        return false;
                    default:
                        return false;
                }
            }
        }

        public void Tick(double simTimeSeconds)
        {
            lock (owner._sync)
            {
                SimView.Update(simTimeSeconds);
            }
        }

        private static MouseButton ToNoesis(UiPointerButton button) => button switch
        {
            UiPointerButton.Right => MouseButton.Right,
            UiPointerButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left,
        };
    }

    // ---- file-system resource providers rooted at the XAML's directory ----

    private static string Combine(string root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var normalized = segment.Replace('\\', IoPath.DirectorySeparatorChar)
                .Replace('/', IoPath.DirectorySeparatorChar)
                .TrimStart(IoPath.DirectorySeparatorChar);
            if (normalized.Length > 0) path = IoPath.Combine(path, normalized);
        }
        return path;
    }

    private sealed class FolderXamlProvider(string root) : XamlProvider
    {
        public override Stream? LoadXaml(Uri uri)
        {
            var path = Combine(root, uri.GetPath());
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }

    private sealed class FolderTextureProvider(string root) : FileTextureProvider
    {
        public override Stream? OpenStream(Uri uri)
        {
            var path = Combine(root, uri.GetPath());
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }

    private sealed class FolderFontProvider(string root) : FontProvider
    {
        public override Stream? OpenFont(Uri folder, string filename)
        {
            var path = Combine(root, folder.GetPath(), filename);
            return File.Exists(path) ? File.OpenRead(path) : null;
        }

        public override void ScanFolder(Uri folder)
        {
            var path = Combine(root, folder.GetPath());
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path))
            {
                var ext = IoPath.GetExtension(file);
                if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterFont(folder, IoPath.GetFileName(file));
                }
            }
        }
    }
}
