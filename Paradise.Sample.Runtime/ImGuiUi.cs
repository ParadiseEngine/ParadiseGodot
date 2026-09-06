using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using Paradise.Ui;

namespace Paradise.Sample.Runtime;

/// <summary>Dear ImGui behind <see cref="IUiSystem"/>: the engine's renderer-independent half
/// (<see cref="ImGuiUiCore"/> — sim-thread frame + triple-buffered snapshots) plus this host's
/// WebGPU render half (<see cref="ImGuiWebGpuRenderer"/> into the engine's OverlayPass).</summary>
internal sealed class ImGuiUi : IUiSystem
{
    private readonly ImGuiUiCore _core;
    private readonly List<ImGuiTextureOp> _textureOps = new();
    private WebGpuRenderer? _renderer;
    private ImGuiWebGpuRenderer? _drawRenderer;

    public IUiInput Input => _core.Input;

    public ImGuiUi(uint pixelWidth, uint pixelHeight) => _core = new ImGuiUiCore(pixelWidth, pixelHeight);

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
        }

        // Texture ops land before the draw: the snapshot from this acquire may name a texture
        // these ops are what create. ApplyTextureOps clears the list only once every op landed.
        var snapshot = _core.AcquireSnapshotForRender(_textureOps, out _);
        _drawRenderer.ApplyTextureOps(_textureOps);
        if (snapshot is { } frame)
        {
            _drawRenderer.Render(
                encoder, backbuffer,
                (uint)frame.DisplaySize.X, (uint)frame.DisplaySize.Y, frame);
        }
    }
}
