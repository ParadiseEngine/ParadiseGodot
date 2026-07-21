using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;

namespace Paradise.Sample.Pool.Tests;

/// <summary>Pocket capture and the data-driven ball material params, driven synchronously
/// through TickOnce: a ball rolling over a pocket mouth sinks (parked, dead, excluded from
/// dynamics), the cue ball scratches (instant head-spot respawn), rewind resurrects a sunk
/// ball, and per-ball damping/restitution actually shape the motion.</summary>
public class PoolPocketTests
{
    private static Vector3 PositionOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<Position>(entity).Value;
    }

    private static BallSunk PoolStateOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<BallSunk>(entity);
    }

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
    public async Task ball_rolling_over_a_pocket_sinks_and_parks()
    {
        using var runner = new SimulationRunner();
        var park = new Vector3(1f, 0.85f, 1f);
        var ball = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park));

        runner.EnqueueBallImpulse(ball, new Vector3(4f, 0f, 0f)); // rolls +X across the mouth
        for (var i = 0; i < 180; i++) runner.TickOnce();

        await Assert.That(PoolStateOf(runner, ball).Value).IsEqualTo((byte)1);
        Vector3 parked = PositionOf(runner, ball);
        await Assert.That(MathF.Abs(parked.X - park.X)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(parked.Z - park.Z)).IsLessThan(1e-4f);
        // (Y is no longer asserted — balls are full 3D now; capture parks X/Z and freezes the ball.)

        // Sunk = out of the simulation: it never moves again.
        for (var i = 0; i < 120; i++) runner.TickOnce();
        await Assert.That(PositionOf(runner, ball)).IsEqualTo(parked);
    }

    [Test]
    public async Task cue_ball_scratch_respawns_at_the_head_spot()
    {
        using var runner = new SimulationRunner();
        var headSpot = new Vector3(3f, 0.85f, 3f);
        var cue = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park: default, respawn: headSpot, isCue: true));

        runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 180; i++) runner.TickOnce();

        // Scratched, not sunk: back at the head spot, at rest, still playable.
        await Assert.That(PoolStateOf(runner, cue).Value).IsEqualTo((byte)0);
        Vector3 position = PositionOf(runner, cue);
        await Assert.That(MathF.Abs(position.X - headSpot.X)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(position.Z - headSpot.Z)).IsLessThan(1e-4f);

        runner.EnqueueBallImpulse(cue, new Vector3(0f, 0f, 2f)); // still strikeable
        for (var i = 0; i < 60; i++) runner.TickOnce();
        await Assert.That(PositionOf(runner, cue).Z).IsGreaterThan(headSpot.Z + 0.2f);
    }

    [Test]
    public async Task rewind_resurrects_a_sunk_ball()
    {
        using var runner = new SimulationRunner();
        var ball = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park: new Vector3(1f, 0.85f, 1f)));

        runner.EnqueueBallImpulse(ball, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 120; i++) runner.TickOnce();
        await Assert.That(PoolStateOf(runner, ball).Value).IsEqualTo((byte)1);

        // Restore to tick 20 — long before the ball reached the mouth (~tick 55).
        runner.Paused = true;
        await Assert.That(runner.RestoreFromRewind(100)).IsTrue();
        runner.Paused = false;

        await Assert.That(PoolStateOf(runner, ball).Value).IsEqualTo((byte)0);
        Vector3 restored = PositionOf(runner, ball);
        await Assert.That(restored.X).IsGreaterThan(5f);   // it had started rolling…
        await Assert.That(restored.X).IsLessThan(6.7f);    // …but was not at the pocket yet

        // The resurrected timeline keeps playing — it rolls on and sinks again.
        for (var i = 0; i < 120; i++) runner.TickOnce();
        await Assert.That(PoolStateOf(runner, ball).Value).IsEqualTo((byte)1);
    }

    [Test]
    public async Task parked_ball_is_excluded_from_dynamics()
    {
        using var runner = new SimulationRunner();
        var park = new Vector3(10f, 0.85f, 10f);
        var sunk = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
            pocket: OnePocket(7f, 5f, park));
        var rolling = runner.SpawnBall(new Vector3(10f, 0.85f, 6f), Quaternion.Identity, radius: 0.35f);

        runner.EnqueueBallImpulse(sunk, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 180; i++) runner.TickOnce();
        await Assert.That(PoolStateOf(runner, sunk).Value).IsEqualTo((byte)1);

        // Drive the live ball straight through the tray slot: no bounce, no displacement.
        runner.EnqueueBallImpulse(rolling, new Vector3(0f, 0f, 8f));
        for (var i = 0; i < 300; i++) runner.TickOnce();

        await Assert.That(PositionOf(runner, rolling).Z).IsGreaterThan(park.Z + 0.3f); // passed through
        Vector3 parked = PositionOf(runner, sunk);
        await Assert.That(MathF.Abs(parked.X - park.X)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(parked.Z - park.Z)).IsLessThan(1e-4f);
    }

    [Test]
    public async Task per_ball_damping_shapes_the_roll()
    {
        using var runner = new SimulationRunner();
        var felt = runner.SpawnBall(new Vector3(2f, 0.85f, 4f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0.6f);
        var carpet = runner.SpawnBall(new Vector3(2f, 0.85f, 12f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 3f);

        runner.EnqueueBallImpulse(felt, new Vector3(3f, 0f, 0f));
        runner.EnqueueBallImpulse(carpet, new Vector3(3f, 0f, 0f));
        for (var i = 0; i < 1200; i++) runner.TickOnce();

        // Analytic travel bound is v0/damping: 5 m vs 1 m from the same strike.
        float feltTravel = PositionOf(runner, felt).X - 2f;
        float carpetTravel = PositionOf(runner, carpet).X - 2f;
        await Assert.That(carpetTravel).IsGreaterThan(0.2f);
        await Assert.That(feltTravel).IsGreaterThan(2f * carpetTravel);
    }

    [Test]
    public async Task per_ball_restitution_shapes_the_impact()
    {
        // Same head-on hit under two restitution pairings: the lively pair hands the target
        // more exit speed, so it travels farther before damping stops it.
        float TargetTravel(float restitution)
        {
            using var runner = new SimulationRunner();
            var cue = runner.SpawnBall(new Vector3(5f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
                restitution: restitution);
            var target = runner.SpawnBall(new Vector3(7f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f,
                restitution: restitution);
            runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));
            for (var i = 0; i < 600; i++) runner.TickOnce();
            return PositionOf(runner, target).X - 7f;
        }

        float lively = TargetTravel(0.95f);
        float dead = TargetTravel(0.05f);
        await Assert.That(lively).IsGreaterThan(dead * 1.3f);
    }

    [Test]
    public async Task poolrack_built_ball_sinks_and_parks_at_the_computed_slot()
    {
        // The shared factory BOTH hosts feed into SpawnBall (.NET SceneAssembler + the Godot
        // bridge's ExtractPockets). Proves the pocket set + tray layout produce a working capture.
        using var runner = new SimulationRunner();
        var pockets = new List<(Vector3 Center, float Radius)> { (new Vector3(7f, 0f, 5f), 0.3f) };
        var authored = new Vector3(5f, 0.85f, 5f);
        var poolBall = PoolRack.BuildBall(pockets, isCue: false, authoredPosition: authored, trayIndex: 2);

        // Tray slot = (minX + trayIndex*0.45, authoredY, maxZ + 0.75) = (7 + 0.9, 0.85, 5.75).
        await Assert.That(poolBall.PocketCount).IsEqualTo(1);
        await Assert.That(MathF.Abs(poolBall.ParkPosition.X - 7.9f)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(poolBall.ParkPosition.Z - 5.75f)).IsLessThan(1e-4f);

        var ball = runner.SpawnBall(authored, Quaternion.Identity, radius: 0.2f, pocket: poolBall);
        runner.EnqueueBallImpulse(ball, new Vector3(4f, 0f, 0f)); // roll +X across the pocket mouth
        for (var i = 0; i < 180; i++) runner.TickOnce();

        await Assert.That(PoolStateOf(runner, ball).Value).IsEqualTo((byte)1);
        Vector3 parked = PositionOf(runner, ball);
        await Assert.That(MathF.Abs(parked.X - 7.9f)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(parked.Z - 5.75f)).IsLessThan(1e-4f);
    }
}
