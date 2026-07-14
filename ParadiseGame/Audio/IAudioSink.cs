namespace ParadiseGame.Audio;

/// <summary>The SIMULATION-thread half of an audio system — the mirror of
/// <see cref="Ui.IUiInput"/> with the data flowing the other way: UI events flow platform →
/// sim, audio commands flow sim → device. Game logic calls these on the sim thread (event
/// posts, parameters, switches) and the runner advances <see cref="Tick"/> once per fixed
/// tick; the system's other half (the engine pump, e.g. Wwise's RenderAudio) runs on the
/// render thread. Implementations are responsible for their own cross-thread handoff —
/// Wwise's public API is internally thread-safe, so its sink calls straight through.</summary>
public interface IAudioSink
{
    /// <summary>Post a named audio event (fire-and-forget). <paramref name="sourceId"/>
    /// selects the emitting game object; 0 = the default 2D object.</summary>
    void PostEvent(string eventName, ulong sourceId = 0);

    /// <summary>Set a real-time parameter (game → mixer control curve).</summary>
    void SetParameter(string parameterName, float value, ulong sourceId = 0);

    /// <summary>Set a switch state on a source (e.g. footstep surface material).</summary>
    void SetSwitch(string switchGroup, string switchState, ulong sourceId = 0);

    /// <summary>Advance audio-side time on the sim thread, once per fixed tick with
    /// canonical sim time (mirrors <see cref="Ui.IUiInput.Tick"/>).</summary>
    void Tick(double simTimeSeconds);
}
