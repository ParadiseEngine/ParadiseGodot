using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using Paradise.Sample.Game.Ui;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Runtime;

/// <summary>Dear ImGui behind <see cref="IUiSystem"/>: the shared renderer-independent half
/// (<see cref="ImGuiUiCore"/> — sim-thread frame + triple-buffered snapshots) plus this host's
/// WebGPU render half (<see cref="ImGuiWebGpuRenderer"/> into the engine's OverlayPass).</summary>
internal sealed class ImGuiUi : IUiSystem
{
    private readonly ImGuiUiCore _core;
    private WebGpuRenderer? _renderer;
    private ImGuiWebGpuRenderer? _drawRenderer;

    public IUiInput Input => _core.Input;

    public ImGuiUi(uint pixelWidth, uint pixelHeight, UiFontConfig? cjkFont = null) =>
        _core = new ImGuiUiCore(pixelWidth, pixelHeight, cjkFont);

    /// <summary>Register a per-tick draw delegate — runs ON THE SIM THREAD between NewFrame
    /// and Render, so it may read and mutate sim-owned state freely. Register before the sim
    /// starts.</summary>
    public void AddDraw(Action draw) => _core.AddDraw(draw);

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
            _drawRenderer.SetFontAtlas(_core.FontPixels, _core.FontWidth, _core.FontHeight);
        }

        if (_core.AcquireSnapshotForRender(out _) is { } snapshot)
        {
            _drawRenderer.Render(
                encoder, backbuffer,
                (uint)snapshot.DisplaySize.X, (uint)snapshot.DisplaySize.Y, snapshot);
        }
    }
}
