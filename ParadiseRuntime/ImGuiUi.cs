using System.Numerics;
using ImGuiNET;
using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using ParadiseGame.Ui;

namespace ParadiseRuntime;

/// <summary>Dear ImGui behind the two-half UI interface:
///
/// - <see cref="Input"/> (<see cref="IUiInput"/>) runs on the SIM thread and owns the ENTIRE
///   ImGui frame: events feed <c>io</c>, and each fixed tick runs NewFrame → registered draw
///   delegates → Render → snapshot. Immediate mode + sim-thread execution means panels read
///   and mutate live sim state directly — no marshaling.
/// - The render half never touches ImGui at all: it draws the latest self-contained
///   <see cref="ImGuiDrawSnapshot"/> through <see cref="ImGuiWebGpuRenderer"/> (triple-buffered
///   handoff, so neither thread ever waits on the other beyond a pointer swap).
///
/// The classic static font atlas is pinned deliberately (pixels copied at construction,
/// uploaded once on the render thread) — ImGui 1.92's dynamic-texture protocol is the known
/// cross-thread hazard and debug UI does not need runtime font changes. Context creation
/// happens on the main thread before the sim starts; ImGui's current context lives in
/// cimgui's process-global GImGui (ImGui.NET does not compile the thread-local variant), so
/// there is no thread affinity — only a no-concurrent-access rule, and after startup only the
/// sim thread calls into it.
/// Process-scoped lifetime (one global ImGui context), like NoesisUi.</summary>
internal sealed class ImGuiUi : IUiSystem
{
    private readonly object _snapshotLock = new();
    private readonly Stack<ImGuiDrawSnapshot> _free = new();
    private ImGuiDrawSnapshot? _latest;
    private ImGuiDrawSnapshot? _rendering;

    private readonly List<Action> _draw = new();
    private readonly byte[] _fontPixels;
    private readonly uint _fontWidth;
    private readonly uint _fontHeight;
    private WebGpuRenderer? _renderer;
    private ImGuiWebGpuRenderer? _drawRenderer;
    private double _lastTickTime;
    private bool _hasTicked;

    public IUiInput Input { get; }

    public unsafe ImGuiUi(uint pixelWidth, uint pixelHeight)
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplaySize = new Vector2(pixelWidth, pixelHeight);
        io.Fonts.AddFontDefault();
        io.Fonts.Build();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
        _fontPixels = new ReadOnlySpan<byte>(pixels, width * height * 4).ToArray();
        _fontWidth = (uint)width;
        _fontHeight = (uint)height;
        io.Fonts.SetTexID(ImGuiWebGpuRenderer.FontTextureId);

        Input = new UiInputHalf(this);
        Console.WriteLine($"[ImGuiUi] context ready ({pixelWidth}x{pixelHeight}).");
    }

    /// <summary>Register a per-tick draw delegate — runs ON THE SIM THREAD between NewFrame
    /// and Render, so it may read and mutate sim-owned state freely. Register before the sim
    /// starts.</summary>
    public void AddDraw(Action draw) => _draw.Add(draw);

    /// <summary>Render-thread half: remember the engine renderer. The host composes
    /// <see cref="WebGpuRenderer.OverlayPass"/> and calls <see cref="RecordOverlay"/> from it.</summary>
    public void Attach(WebGpuRenderer renderer) => _renderer = renderer;

    /// <summary>Record the latest UI snapshot into the frame (render thread). Draws the
    /// previous snapshot again when the sim has not produced a new one yet.</summary>
    public void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer)
    {
        if (_drawRenderer is null)
        {
            var renderer = _renderer!;
            var format = renderer.ColorFormat == Paradise.Rendering.TextureFormat.Bgra8Unorm
                ? WebGpuSharp.TextureFormat.BGRA8Unorm
                : WebGpuSharp.TextureFormat.RGBA8Unorm;
            _drawRenderer = new ImGuiWebGpuRenderer(renderer.NativeDevice, format);
            _drawRenderer.SetFontAtlas(_fontPixels, _fontWidth, _fontHeight);
        }

        lock (_snapshotLock)
        {
            if (_latest is not null)
            {
                if (_rendering is not null) _free.Push(_rendering);
                _rendering = _latest;
                _latest = null;
            }
        }
        if (_rendering is { } snapshot)
        {
            _drawRenderer.Render(
                encoder, backbuffer,
                (uint)snapshot.DisplaySize.X, (uint)snapshot.DisplaySize.Y, snapshot);
        }
    }

    private sealed class UiInputHalf(ImGuiUi owner) : IUiInput
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
                default:
                    return false;
            }
        }

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

/// <summary>Fan-out for running several UI systems on one sim input stream (e.g. ImGui debug
/// panels over Noesis game UI). Pointer-downs/ups stop at the first consumer in registration
/// order (earlier = higher priority); moves and resizes broadcast to all.</summary>
internal sealed class CompositeUiInput(params IUiInput[] inputs) : IUiInput
{
    public bool Handle(in UiEvent uiEvent)
    {
        if (uiEvent.Kind is UiEventKind.PointerDown or UiEventKind.PointerUp)
        {
            foreach (var input in inputs)
            {
                if (input.Handle(in uiEvent)) return true;
            }
            return false;
        }
        var consumed = false;
        foreach (var input in inputs)
        {
            consumed |= input.Handle(in uiEvent);
        }
        return consumed;
    }

    public void Tick(double simTimeSeconds)
    {
        foreach (var input in inputs)
        {
            input.Tick(simTimeSeconds);
        }
    }
}
