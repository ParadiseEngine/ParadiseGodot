using System.Numerics;

namespace Paradise.Sample.Pool.Audio;

/// <summary>The SIMULATION-thread half of an audio system — the mirror of
/// <see cref="Ui.IUiInput"/> with the data flowing the other way: UI events flow platform →
/// sim, audio commands flow sim → device. Game logic calls these on the sim thread (event
/// posts, parameters, switches) and the runner advances <see cref="Tick"/> once per fixed
/// tick; the system's other half (the engine pump, e.g. Wwise's RenderAudio) runs on the
/// render thread. The sim thread is the PRIMARY caller, but hosts may also post from the
/// render thread (e.g. a move-confirmation on the no-UI click path) — implementations must
/// therefore serialize internally or be free-threaded.</summary>
public interface IAudioSink
{
    /// <summary>Post a named audio event (fire-and-forget). <paramref name="sourceId"/>
    /// selects the emitting game object; 0 = the default 2D object.</summary>
    void PostEvent(string eventName, ulong sourceId = 0);

    /// <summary>Set a real-time parameter (game → mixer control curve).</summary>
    void SetParameter(string parameterName, float value, ulong sourceId = 0);

    /// <summary>Set a switch state on a source (e.g. footstep surface material).</summary>
    void SetSwitch(string switchGroup, string switchState, ulong sourceId = 0);

    /// <summary>Place an emitting source in world space (engine coordinates: right-handed,
    /// +Y up, -Z forward — implementations convert to their backend's convention). Zero
    /// orientation vectors mean "use the default facing".</summary>
    void SetSourcePosition(ulong sourceId, Vector3 position, Vector3 forward = default, Vector3 up = default);

    /// <summary>Place the listener (usually the camera) in world space, same conventions as
    /// <see cref="SetSourcePosition"/>. Typically driven per render frame by the host.</summary>
    void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up);

    /// <summary>Advance audio-side time on the sim thread, once per fixed tick with
    /// canonical sim time (mirrors <see cref="Ui.IUiInput.Tick"/>).</summary>
    void Tick(double simTimeSeconds);
}
