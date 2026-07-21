using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;

namespace Paradise.Sample.Pool.Tests;

/// <summary>The SystemEvents reactor demo, driven synchronously through TickOnce: MovementSystem appends
/// a <see cref="BallPocketed"/> when a ball drops (SYSTEM producer), SimulationRunner emits a
/// <see cref="GameReset"/> via <c>world.Events.Emit</c> (MANAGED producer), and the owner-reactor
/// <see cref="ScoreSystem"/> is the sole writer of <see cref="Score"/>, folding both one frame later.</summary>
public class ScoreReactorTests
{
    /// <summary>One pocket at (x, z) with capture radius 0.3, park/respawn as given.</summary>
    private static PocketConfig OnePocket(float x, float z, Vector3 park, Vector3 respawn = default, bool isCue = false)
    {
        var pool = new PocketConfig
        {
            PocketCount = 1,
            ParkPosition = park,
            RespawnPosition = respawn,
            IsCue = isCue ? (byte)1 : (byte)0,
        };
        pool.Pockets[0] = new Vector4(x, z, 0.3f * 0.3f, 0f);
        return pool;
    }

    [Test]
    public async Task pocketing_an_object_ball_raises_the_score()
    {
        using var runner = new SimulationRunner();
        var ball = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park: new Vector3(1f, 0.85f, 1f)));

        await Assert.That(runner.Score).IsEqualTo(0);

        runner.EnqueueBallImpulse(ball, new Vector3(4f, 0f, 0f)); // rolls +X across the mouth
        // Same window as the pocket test (~180), plus a couple ticks for the one-frame-deferred event.
        for (var i = 0; i < 182; i++) runner.TickOnce();

        await Assert.That(runner.Score).IsEqualTo(1);
    }

    [Test]
    public async Task a_cue_ball_scratch_does_not_raise_the_score()
    {
        using var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park: default, respawn: new Vector3(3f, 0.85f, 3f), isCue: true));

        runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 182; i++) runner.TickOnce();

        // The −1 decrement is clamped at 0: a scratch never drives the score negative.
        await Assert.That(runner.Score).IsEqualTo(0);
    }

    [Test]
    public async Task request_reset_returns_the_score_to_zero()
    {
        using var runner = new SimulationRunner();
        var ball = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park: new Vector3(1f, 0.85f, 1f)));

        runner.EnqueueBallImpulse(ball, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 182; i++) runner.TickOnce();
        await Assert.That(runner.Score).IsEqualTo(1);

        runner.RequestReset();
        // Tick 1 emits the managed GameReset (before the schedule commits); tick 2 lets the reactor
        // read it and zero the score.
        for (var i = 0; i < 3; i++) runner.TickOnce();

        await Assert.That(runner.Score).IsEqualTo(0);
    }
}
