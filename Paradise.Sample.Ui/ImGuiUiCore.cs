using System.Numerics;
using ImGuiNET;
using Paradise.Ui.ImGui;
using Paradise.Sample.Game.Ui;

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
        public bool Handle(in UiEvent uiEvent)
        {
            var io = ImGui.GetIO();
            switch (uiEvent.Kind)
            {
                case UiEventKind.PointerMove:
                    io.AddMousePosEvent(uiEvent.X, uiEvent.Y);
                    return io.WantCaptureMouse;
                case UiEventKind.PointerDown:
                    io.AddMouseButtonEvent(ToImGui(uiEvent.Button), true);
                    return io.WantCaptureMouse;
                case UiEventKind.PointerUp:
                    io.AddMouseButtonEvent(ToImGui(uiEvent.Button), false);
                    return io.WantCaptureMouse;
                case UiEventKind.Resize:
                    io.DisplaySize = new Vector2(uiEvent.X, uiEvent.Y);
                    return false;
                case UiEventKind.Scroll:
                    io.AddMouseWheelEvent(uiEvent.X, uiEvent.Y);
                    return io.WantCaptureMouse;
                case UiEventKind.KeyDown when ToImGui(uiEvent.Key) is { } downKey:
                    io.AddKeyEvent(downKey, true);
                    return io.WantCaptureKeyboard;
                case UiEventKind.KeyUp when ToImGui(uiEvent.Key) is { } upKey:
                    io.AddKeyEvent(upKey, false);
                    return io.WantCaptureKeyboard;
                case UiEventKind.Text:
                    io.AddInputCharacter(uiEvent.Character);
                    return io.WantCaptureKeyboard;
                default:
                    return false;
            }
        }

        private static ImGuiKey? ToImGui(UiKey key) => key switch
        {
            UiKey.Enter => ImGuiKey.Enter,
            UiKey.Escape => ImGuiKey.Escape,
            UiKey.Backspace => ImGuiKey.Backspace,
            UiKey.Delete => ImGuiKey.Delete,
            UiKey.Tab => ImGuiKey.Tab,
            UiKey.Left => ImGuiKey.LeftArrow,
            UiKey.Right => ImGuiKey.RightArrow,
            UiKey.Up => ImGuiKey.UpArrow,
            UiKey.Down => ImGuiKey.DownArrow,
            UiKey.Home => ImGuiKey.Home,
            UiKey.End => ImGuiKey.End,
            UiKey.Ctrl => ImGuiKey.ModCtrl,
            UiKey.Shift => ImGuiKey.ModShift,
            UiKey.A => ImGuiKey.A,
            UiKey.C => ImGuiKey.C,
            UiKey.D => ImGuiKey.D,
            UiKey.S => ImGuiKey.S,
            UiKey.V => ImGuiKey.V,
            UiKey.W => ImGuiKey.W,
            UiKey.X => ImGuiKey.X,
            UiKey.Y => ImGuiKey.Y,
            UiKey.Z => ImGuiKey.Z,
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

        private static int ToImGui(UiPointerButton button) => button switch
        {
            UiPointerButton.Right => 1,
            UiPointerButton.Middle => 2,
            _ => 0,
        };
    }
}
