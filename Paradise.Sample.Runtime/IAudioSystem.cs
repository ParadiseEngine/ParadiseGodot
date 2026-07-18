using Paradise.Sample.Game.Audio;

namespace Paradise.Sample.Runtime;

/// <summary>A complete audio system in the same two-half shape as <see cref="IUiSystem"/>:
///
/// - <see cref="Sink"/> is the SIM-thread half (event posts, parameters, per-tick time),
///   consumed by <c>SimulationRunner</c> via the engine-neutral
///   <see cref="Paradise.Sample.Game.Audio.IAudioSink"/>.
/// - <see cref="Pump"/> is the RENDER-thread half: called once per render frame to advance
///   the audio engine (e.g. Wwise's <c>RenderAudio</c>, which consumes the commands the sink
///   enqueued from the sim thread).
///
/// Unlike UI there is no per-frame GPU pass, so the render half is a plain pump rather than
/// an overlay recorder — the seam is the frame loop instead of the command encoder.</summary>
public interface IAudioSystem : IDisposable
{
    /// <summary>The sim-thread half; hand it to the simulation (<c>SimulationRunner.Audio</c>).</summary>
    IAudioSink Sink { get; }

    /// <summary>Render-thread, once per frame: advance the audio engine.</summary>
    void Pump();
}
