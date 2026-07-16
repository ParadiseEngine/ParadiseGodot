using Paradise.Rendering.WebGPU;
using Paradise.Ui.Noesis;
using ParadiseGame.Ui;
using ParadiseUi;

namespace ParadiseRuntime;

/// <summary>NoesisGUI behind <see cref="IUiSystem"/>: the shared renderer-independent half
/// (<see cref="NoesisViewCore"/> — sim-thread view lifecycle + the UpdateRenderTree sync seam)
/// plus this host's WebGPU render half (<see cref="NoesisRenderDevice"/> compositing into the
/// engine's OverlayPass — no readback, no extra latency). The render device initializes on the
/// first recorded frame after the sim thread has published the view (Noesis: Renderer.Init on
/// the render thread, View on the UI thread).</summary>
internal sealed class NoesisUi : IUiSystem
{
    private readonly NoesisViewCore _core;
    private WebGpuRenderer? _renderer;
    private NoesisRenderDevice? _device;

    public IUiInput Input => _core.Input;

    public NoesisUi(string xamlPath, uint pixelWidth, uint pixelHeight) =>
        _core = new NoesisViewCore(xamlPath, pixelWidth, pixelHeight);

    /// <summary>Render-thread half: remember the engine renderer. The host composes
    /// <see cref="WebGpuRenderer.OverlayPass"/> and calls <see cref="RecordOverlay"/> from it.</summary>
    public void Attach(WebGpuRenderer renderer) => _renderer = renderer;

    /// <summary>Record the UI passes into the frame (render thread).</summary>
    public void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer)
    {
        var view = _core.View;
        if (view is null) return; // sim thread has not created the UI yet — skip this frame

        if (_device is null)
        {
            // Deliberately outside the core's sync lock: Noesis's threading contract runs
            // Renderer.Init on the render thread while the View lives on the UI thread — Init
            // touches only render-side state, so it may overlap a concurrent sim-thread
            // View.Update. Only UpdateRenderTree synchronizes the two trees.
            var renderer = _renderer!;
            var format = renderer.ColorFormat == Paradise.Rendering.TextureFormat.Bgra8Unorm
                ? WebGpuSharp.TextureFormat.BGRA8Unorm
                : WebGpuSharp.TextureFormat.RGBA8Unorm;
            _device = new NoesisRenderDevice(renderer.NativeDevice, format);
            view.Renderer.Init(_device);
            _device.PrewarmPipelines();
        }

        if (!_core.TryUpdateRenderTree()) return;
        _device.BeginFrame(encoder, backbuffer, _core.Width, _core.Height);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        _device.EndFrame();
    }
}
