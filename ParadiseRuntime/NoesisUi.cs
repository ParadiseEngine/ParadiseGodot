using Noesis;
using Paradise.Rendering.WebGPU;
using Paradise.Ui.Noesis;
using ParadiseGame.Ui;
using IoPath = System.IO.Path;

namespace ParadiseRuntime;

/// <summary>NoesisGUI split across the two UI halves:
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread — the simulation
///   drains pointer events into the view and advances view time each fixed tick, so hover,
///   focus, animations and bindings step in lockstep with game state.
/// - The renderer half runs on the RENDER thread — <see cref="Attach"/> plugs the
///   managed WebGPU RenderDevice into the engine's <see cref="WebGpuRenderer.OverlayPass"/>
///   seam; each frame it syncs the render tree and composites the UI over the scene inside
///   the same command encoder (no readback, no extra latency).
///
/// The two halves meet at exactly one point, per Noesis's threading model: view updates
/// (sim) and <c>UpdateRenderTree</c> (render) are mutually excluded by <see cref="_sync"/>;
/// everything else runs lock-free on its own thread.
///
/// Noesis pins each View to the Dispatcher of its CREATION thread, so all GUI construction
/// (native init, providers rooted at the XAML's directory, optional
/// <c>Theme/NoesisTheme.DarkBlue.xaml</c>, view creation) happens LAZILY on the sim thread at
/// the first tick; the render half waits (skipping frames) until the view is published, then
/// initializes the render device on the render thread on its first recorded frame.</summary>
// Lifetime: process-scoped by design — no Dispose/GUI.Shutdown. The runtime creates at most
// one NoesisUi per process and native/GPU teardown happens at exit; add disposal if this ever
// hosts multiple sessions (tests, editor).
internal sealed class NoesisUi : IUiSystem
{
    private readonly string _root;
    private readonly string _xamlFile;
    private readonly object _sync = new();
    private volatile View? _view; // published by the sim thread once created there
    private WebGpuRenderer? _renderer;
    private NoesisRenderDevice? _device;
    private volatile uint _width;
    private volatile uint _height;

    public IUiInput Input { get; }

    public NoesisUi(string xamlPath, uint pixelWidth, uint pixelHeight)
    {
        _root = IoPath.GetDirectoryName(IoPath.GetFullPath(xamlPath)) ?? ".";
        _xamlFile = IoPath.GetFileName(xamlPath);
        _width = pixelWidth;
        _height = pixelHeight;
        Input = new UiInputHalf(this);
    }

    /// <summary>Render-thread half: remember the engine renderer. The host composes
    /// <see cref="WebGpuRenderer.OverlayPass"/> and calls <see cref="RecordOverlay"/> from it.
    /// The render device itself initializes on the first recorded frame after the sim thread
    /// has published the view (Noesis: Renderer.Init on the render thread, View on the UI
    /// thread).</summary>
    public void Attach(WebGpuRenderer renderer) => _renderer = renderer;

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

    /// <summary>Record the UI passes into the frame (render thread).</summary>
    public void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer)
    {
        var view = _view;
        if (view is null) return; // sim thread has not created the UI yet — skip this frame

        if (_device is null)
        {
            // Deliberately outside _sync: Noesis's threading contract runs Renderer.Init on
            // the render thread while the View lives on the UI thread — Init touches only
            // render-side state, so it may overlap a concurrent sim-thread View.Update. Only
            // UpdateRenderTree (below) synchronizes the two trees and needs the lock.
            var renderer = _renderer!;
            var format = renderer.ColorFormat == Paradise.Rendering.TextureFormat.Bgra8Unorm
                ? WebGpuSharp.TextureFormat.BGRA8Unorm
                : WebGpuSharp.TextureFormat.RGBA8Unorm;
            _device = new NoesisRenderDevice(renderer.NativeDevice, format);
            view.Renderer.Init(_device);
            _device.PrewarmPipelines();
        }

        // The single sync point with the sim half: pick up the last view update.
        lock (_sync)
        {
            view.Renderer.UpdateRenderTree();
        }
        _device.BeginFrame(encoder, backbuffer, _width, _height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        _device.EndFrame();
    }

    private sealed class UiInputHalf(NoesisUi owner) : IUiInput
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
