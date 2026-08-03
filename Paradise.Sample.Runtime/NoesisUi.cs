using Paradise.Rendering.WebGPU;
using Paradise.Ui;
using Paradise.Ui.Noesis.Host;

namespace Paradise.Sample.Runtime;

/// <summary>NoesisGUI behind <see cref="IUiSystem"/>: the shared renderer-independent half
/// (<see cref="NoesisViewCore"/> — sim-thread view lifecycle + the UpdateRenderTree sync seam)
/// plus this host's WebGPU render half (<see cref="NoesisOverlayRenderer"/> compositing into
/// the engine's OverlayPass — no readback, no extra latency). Both halves come from the engine's
/// Paradise.Ui.Noesis.Host package; all this host adds is the <see cref="IUiSystem"/> shape and
/// the engine-renderer wiring (native device + swapchain color format).</summary>
internal sealed class NoesisUi : IUiSystem
{
    private readonly NoesisViewCore _core;
    private NoesisOverlayRenderer? _overlay;

    public IUiInput Input => _core.Input;

    public NoesisUi(string xamlPath, uint pixelWidth, uint pixelHeight,
        object? dataContext = null, Action? simTick = null) =>
        _core = new NoesisViewCore(xamlPath, pixelWidth, pixelHeight, dataContext, simTick);

    /// <summary>Render-thread half: bind the overlay renderer to the engine renderer's device
    /// and swapchain color format. The host composes <see cref="WebGpuRenderer.OverlayPass"/>
    /// and calls <see cref="RecordOverlay"/> from it.</summary>
    public void Attach(WebGpuRenderer renderer) => _overlay = new NoesisOverlayRenderer(
        _core,
        renderer.NativeDevice,
        renderer.ColorFormat == Paradise.Rendering.TextureFormat.Bgra8Unorm
            ? WebGpuSharp.TextureFormat.BGRA8Unorm
            : WebGpuSharp.TextureFormat.RGBA8Unorm);

    /// <summary>Record the UI passes into the frame (render thread). No-op until
    /// <see cref="Attach"/> has run and the sim thread has published the view.</summary>
    public void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer) =>
        _overlay?.RecordOverlay(encoder, backbuffer);
}
