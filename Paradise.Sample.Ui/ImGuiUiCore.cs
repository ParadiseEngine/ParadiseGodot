using System.Numerics;
using ImGuiNET;
using Paradise.Ui.ImGui;
using Paradise.Ui;
using Paradise.Windowing;

namespace Paradise.Sample.Ui;

/// <summary>The renderer-independent half of Dear ImGui in the two-half UI architecture,
/// shared by every host (the SDL/WebGPU runtime, the Godot play-mode bridge):
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread and owns the ENTIRE
///   ImGui frame: events feed <c>io</c>, and each fixed tick runs NewFrame → registered draw
///   delegates → Render → snapshot. Immediate mode + sim-thread execution means panels read
///   and mutate live sim state directly — no marshaling.
/// - The host's render half never touches ImGui at all: it acquires the latest self-contained
///   <see cref="ImGuiDrawSnapshot"/> via <see cref="AcquireSnapshotForRender"/> (triple-buffered
///   handoff, so neither thread ever waits on the other beyond a pointer swap) and draws it
///   with whatever renderer it owns (WebGPU overlay pass, Godot canvas items, …). The font
///   atlas pixels are exposed once via <see cref="FontPixels"/> for the host to upload.
///
/// The classic static font atlas is pinned deliberately (pixels copied at construction,
/// uploaded once by the host) — ImGui 1.92's dynamic-texture protocol is the known
/// cross-thread hazard and debug UI does not need runtime font changes. Context creation
/// happens on the main thread before the sim starts; ImGui's current context lives in
/// cimgui's process-global GImGui (ImGui.NET does not compile the thread-local variant), so
/// there is no thread affinity — only a no-concurrent-access rule, and after startup only the
/// sim thread calls into it. Process-scoped lifetime (one global ImGui context).</summary>
public sealed class ImGuiUiCore
{
    /// <summary>The opaque <c>ImTextureID</c> the font atlas registers under — hosts map it to
    /// their uploaded atlas texture. Matches <c>ImGuiWebGpuRenderer.FontTextureId</c>.</summary>
    public static readonly nint FontTextureId = ImGuiWebGpuRenderer.FontTextureId;

    private readonly object _snapshotLock = new();
    private readonly Stack<ImGuiDrawSnapshot> _free = new();
    private ImGuiDrawSnapshot? _latest;
    private ImGuiDrawSnapshot? _rendering;

    private readonly List<Action> _draw = new();
    private readonly byte[] _fontPixels;
    private double _lastTickTime;
    private bool _hasTicked;

    public IUiInput Input { get; }

    /// <summary>RGBA8 pixels of the static font atlas, for the host's one-time upload.</summary>
    public ReadOnlySpan<byte> FontPixels => _fontPixels;
    public uint FontWidth { get; }
    public uint FontHeight { get; }

    /// <param name="cjkFont">Optional CJK-capable font (see <see cref="UiFonts"/>). Null =
    /// the classic ASCII default font, unchanged behavior for existing hosts. When the
    /// requested font cannot be resolved/loaded the core degrades to the default font.</param>
    public unsafe ImGuiUiCore(uint pixelWidth, uint pixelHeight, UiFontConfig? cjkFont = null)
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(pixelWidth, pixelHeight);
        if (cjkFont is null || !UiFonts.TryAddCjkFont(io, cjkFont))
        {
            io.Fonts.AddFontDefault();
        }
        io.Fonts.Build();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
        _fontPixels = new ReadOnlySpan<byte>(pixels, width * height * 4).ToArray();
        FontWidth = (uint)width;
        FontHeight = (uint)height;
        io.Fonts.SetTexID(FontTextureId);

        Input = new UiInputHalf(this);
        Console.WriteLine($"[ImGuiUi] context ready ({pixelWidth}x{pixelHeight}).");
    }

    /// <summary>Register a per-tick draw delegate — runs ON THE SIM THREAD between NewFrame
    /// and Render, so it may read and mutate sim-owned state freely. Register before the sim
    /// starts.</summary>
    public void AddDraw(Action draw) => _draw.Add(draw);

    /// <summary>Render/main-thread half: swap in the newest snapshot (the previous rendering
    /// snapshot returns to the free pool) and return it. Null before the first sim tick.
    /// <paramref name="isNew"/> is false when this is the same snapshot as the previous call —
    /// hosts with retained scenes (Godot canvas items) skip the rebuild then; immediate hosts
    /// just draw it again.</summary>
    public ImGuiDrawSnapshot? AcquireSnapshotForRender(out bool isNew)
    {
        lock (_snapshotLock)
        {
            isNew = _latest is not null;
            if (_latest is not null)
            {
                if (_rendering is not null) _free.Push(_rendering);
                _rendering = _latest;
                _latest = null;
            }
            return _rendering;
        }
    }

    private sealed class UiInputHalf(ImGuiUiCore owner) : IUiInput
    {
        public bool Handle(in WindowEvent input)
        {
            var io = ImGui.GetIO();
            switch (input.Kind)
            {
                case WindowEventKind.PointerMove:
                    io.AddMousePosEvent(input.X, input.Y);
                    return io.WantCaptureMouse;
                // Pointer down and up used to be separate kinds. WindowEvent folds every
                // transition into Button + Pressed and says WHICH DEVICE in Source, so the two
                // cases become one and the direction rides in the bool.
                case WindowEventKind.Button when input.Source == EventSource.Mouse:
                    io.AddMouseButtonEvent(ToImGui((PointerButton)input.Code), input.Pressed);
                    return io.WantCaptureMouse;
                case WindowEventKind.Button when input.Source == EventSource.Keyboard
                        && ToImGui((KeyboardKey)input.Code) is { } key:
                    io.AddKeyEvent(key, input.Pressed);
                    return io.WantCaptureKeyboard;
                case WindowEventKind.Resize:
                    io.DisplaySize = new Vector2(input.X, input.Y);
                    return false;
                case WindowEventKind.Scroll:
                    io.AddMouseWheelEvent(input.X, input.Y);
                    return io.WantCaptureMouse;
                case WindowEventKind.Text:
                    io.AddInputCharacter(input.Character);
                    return io.WantCaptureKeyboard;
                default:
                    // Gamepad, axis and touch reach ImGui through none of this — a button case
                    // that did not match Source above lands here rather than being mistaken for
                    // a mouse click.
                    return false;
            }
        }

        private static ImGuiKey? ToImGui(KeyboardKey key) => key switch
        {
            KeyboardKey.Enter => ImGuiKey.Enter,
            KeyboardKey.Escape => ImGuiKey.Escape,
            KeyboardKey.Backspace => ImGuiKey.Backspace,
            KeyboardKey.Delete => ImGuiKey.Delete,
            KeyboardKey.Tab => ImGuiKey.Tab,
            KeyboardKey.Left => ImGuiKey.LeftArrow,
            KeyboardKey.Right => ImGuiKey.RightArrow,
            KeyboardKey.Up => ImGuiKey.UpArrow,
            KeyboardKey.Down => ImGuiKey.DownArrow,
            KeyboardKey.Home => ImGuiKey.Home,
            KeyboardKey.End => ImGuiKey.End,
            KeyboardKey.LeftControl => ImGuiKey.ModCtrl,
            KeyboardKey.LeftShift => ImGuiKey.ModShift,
            KeyboardKey.A => ImGuiKey.A,
            KeyboardKey.C => ImGuiKey.C,
            KeyboardKey.D => ImGuiKey.D,
            KeyboardKey.S => ImGuiKey.S,
            KeyboardKey.V => ImGuiKey.V,
            KeyboardKey.W => ImGuiKey.W,
            KeyboardKey.X => ImGuiKey.X,
            KeyboardKey.Y => ImGuiKey.Y,
            KeyboardKey.Z => ImGuiKey.Z,
            _ => null,
        };

        public void Tick(double simTimeSeconds)
        {
            var io = ImGui.GetIO();
            // Seed on the first tick: the sim clock is not guaranteed to start at zero, and an
            // unclamped first DeltaTime would step animations by the whole absolute time.
            var delta = owner._hasTicked ? simTimeSeconds - owner._lastTickTime : 0.0;
            owner._lastTickTime = simTimeSeconds;
            owner._hasTicked = true;
            io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f;

            ImGui.NewFrame();
            foreach (var draw in owner._draw)
            {
                draw();
            }
            ImGui.Render();

            ImGuiDrawSnapshot snapshot;
            lock (owner._snapshotLock)
            {
                snapshot = owner._free.Count > 0 ? owner._free.Pop() : new ImGuiDrawSnapshot();
            }
            snapshot.Capture(ImGui.GetDrawData());
            lock (owner._snapshotLock)
            {
                if (owner._latest is not null) owner._free.Push(owner._latest);
                owner._latest = snapshot;
            }
        }

        private static int ToImGui(PointerButton button) => button switch
        {
            PointerButton.Right => 1,
            PointerButton.Middle => 2,
            _ => 0,
        };
    }
}
