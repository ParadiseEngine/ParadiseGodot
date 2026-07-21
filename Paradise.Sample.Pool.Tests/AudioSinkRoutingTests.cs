using System.Collections.Generic;
using System.Numerics;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Audio;

namespace Paradise.Sample.Pool.Tests;

/// <summary>The sim-side audio contract (mirror of the UI routing tests): the runner advances
/// the attached sink once per fixed tick with canonical sim time, on the sim thread, and a
/// null sink is a clean no-op.</summary>
public class AudioSinkRoutingTests
{
    private sealed class RecordingSink : IAudioSink
    {
        public readonly List<double> Ticks = new();
        public readonly List<(string Name, ulong Source)> Events = new();

        public readonly List<(ulong Source, Vector3 Position)> Positions = new();

        public void PostEvent(string eventName, ulong sourceId = 0) => Events.Add((eventName, sourceId));
        public void SetParameter(string parameterName, float value, ulong sourceId = 0) { }
        public void SetSwitch(string switchGroup, string switchState, ulong sourceId = 0) { }
        public void SetSourcePosition(ulong sourceId, Vector3 position, Vector3 forward = default, Vector3 up = default)
            => Positions.Add((sourceId, position));
        public void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up) { }
        public void Tick(double simTimeSeconds) => Ticks.Add(simTimeSeconds);
    }

    [Test]
    public async Task audio_ticks_once_per_fixed_tick_with_canonical_time()
    {
        using var runner = new SimulationRunner();
        var sink = new RecordingSink();
        runner.Audio = sink;

        runner.TickOnce();
        runner.TickOnce();
        runner.TickOnce();

        await Assert.That(sink.Ticks.Count).IsEqualTo(3);
        await Assert.That(sink.Ticks[0]).IsEqualTo(SimulationRunner.FixedDeltaSeconds);
        await Assert.That(sink.Ticks[2]).IsEqualTo(3 * SimulationRunner.FixedDeltaSeconds);
    }

    [Test]
    public async Task no_sink_ticks_cleanly()
    {
        using var runner = new SimulationRunner();
        runner.TickOnce(); // must not throw with Audio unset
        await Assert.That(runner.HasSnapshots).IsTrue();
    }
}
