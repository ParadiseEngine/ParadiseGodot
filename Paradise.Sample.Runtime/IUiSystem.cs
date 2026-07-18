using Paradise.Rendering.WebGPU;
using Paradise.Sample.Game.Ui;

namespace Paradise.Sample.Runtime;

/// <summary>A complete UI system in the two-half architecture — the contract every
/// implementation (NoesisUi, ImGuiUi, …) satisfies, making them interchangeable at the
/// composition site:
///
/// - <see cref="Input"/> is the SIM-thread half (event handling + fixed-tick time), consumed
///   by <c>SimulationRunner</c> via the engine-neutral <see cref="IUiInput"/>.
/// - <see cref="Attach"/> / <see cref="RecordOverlay"/> are the RENDER-thread half: attach
///   once after the engine renderer exists, then record the UI's passes into each frame via
///   the renderer's <c>OverlayPass</c> seam (LoadOp.Load — composite over the scene).
///
/// This interface lives in the runtime (not the engine) because it joins the engine-neutral
/// input contract from Paradise.Sample.Game with engine WebGPU types — the two ends the runtime
/// exists to connect. Content authoring is intentionally NOT part of the contract (XAML vs
/// immediate-mode delegates); only the lifecycle around it is unified.</summary>
internal interface IUiSystem
{
    /// <summary>The sim-thread half; hand it (or a <see cref="CompositeUiInput"/> of several)
    /// to the simulation.</summary>
    IUiInput Input { get; }

    /// <summary>Render-thread, once: remember the engine renderer (device access, formats).</summary>
    void Attach(WebGpuRenderer renderer);

    /// <summary>Render-thread, per frame: record this UI's passes into the frame encoder,
    /// compositing over <paramref name="backbuffer"/>.</summary>
    void RecordOverlay(WebGpuSharp.CommandEncoder encoder, WebGpuSharp.TextureView backbuffer);
}
