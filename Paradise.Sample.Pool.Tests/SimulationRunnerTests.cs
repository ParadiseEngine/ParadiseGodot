using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;

namespace Paradise.Sample.Pool.Tests;

// Drives SimulationRunner synchronously via TickOnce (no thread) to verify the double-buffer model:
// each tick publishes a new immutable snapshot copied from the previous, entity handles survive CopyFrom
// across snapshots, and TrySampleInterpolation brackets the right pair with the right alpha. Motion is
// produced by a planar (zero-gravity) ball rolling at constant velocity — no damping, no collision
// world, so its position advances a fixed amount every tick.
public class SimulationRunnerTests
{
    private static readonly PhysicsTuning Planar = new(0.01f, 0.02f, 1.2f, gravity: Vector3.Zero);

    private static Entity SpawnMover(SimulationRunner runner, float speed = 3f)
    {
        Entity ball = runner.SpawnBall(new Vector3(2, 0, 2), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, angularDamping: 0f, tuning: Planar);
        runner.EnqueueBallImpulse(ball, new Vector3(speed, 0f, 0f)); // rolls +X at constant velocity
        return ball;
    }

    private static void Tick(SimulationRunner runner, int count)
    {
        for (int i = 0; i < count; i++)
        {
            runner.TickOnce();
        }
    }

    [Test]
    public async Task consecutive_snapshots_preserve_the_handle_and_interpolate()
    {
        using var runner = new SimulationRunner();
        Entity ball = SpawnMover(runner);

        Tick(runner, 20); // ball is mid-travel; several snapshots in the ring

        double latestTime = runner.LatestSnapshotTime;
        // Sample halfway between the two latest snapshots (one FixedDeltaSeconds apart).
        double sampleTime = latestTime - SimulationRunner.FixedDeltaSeconds * 0.5;

        bool ok = runner.TrySampleInterpolation(sampleTime, out var a, out var b, out float alpha);
        await Assert.That(ok).IsTrue();

        // The same entity handle is valid in BOTH snapshot worlds (CopyFrom preserved it).
        await Assert.That(a.IsAlive(ball)).IsTrue();
        await Assert.That(b.IsAlive(ball)).IsTrue();

        // Distinct snapshots, alpha ~0.5, and the interpolated X lies between the two.
        await Assert.That(alpha).IsGreaterThan(0.1f);
        await Assert.That(alpha).IsLessThan(0.9f);
        float xa = a.GetComponent<Position>(ball).Value.X;
        float xb = b.GetComponent<Position>(ball).Value.X;
        await Assert.That(xb).IsGreaterThan(xa); // moved +X between the two snapshots
        float xi = float.Lerp(xa, xb, alpha);
        await Assert.That(xi).IsGreaterThanOrEqualTo(xa);
        await Assert.That(xi).IsLessThanOrEqualTo(xb);
    }

    [Test]
    public async Task held_snapshot_is_not_recycled_while_the_renderer_reads_it()
    {
        using var runner = new SimulationRunner();
        Entity ball = SpawnMover(runner);
        Tick(runner, 10);

        // Acquire (pin) a pair and keep the references — mimicking a render frame in progress.
        runner.TrySampleInterpolation(runner.LatestSnapshotTime - SimulationRunner.FixedDeltaSeconds * 0.5, out var a, out _, out _);
        float xBefore = a.GetComponent<Position>(ball).Value.X;

        // Advance the sim far past the interpolation window WITHOUT sampling again. A recycle-based design
        // would overwrite `a`; the pin must keep it alive and unchanged.
        Tick(runner, 300);

        await Assert.That(a.IsAlive(ball)).IsTrue();
        await Assert.That(a.GetComponent<Position>(ball).Value.X).IsEqualTo(xBefore);
    }

    [Test]
    public async Task ball_moves_on_the_very_first_tick()
    {
        // E2E pin of snapshot-read execution + dt seeding: MovementSystem reads
        // SimulationContext from the CURRENT (previous-tick) world. On tick 1 that is the
        // initial snapshot — if SpawnBall didn't seed DeltaSeconds, the system would see
        // dt == 0 and skip the tick.
        using var runner = new SimulationRunner();
        Entity ball = SpawnMover(runner, speed: 6f);

        runner.TickOnce();

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        Vector3 p = latest.GetComponent<Position>(ball).Value;
        await Assert.That(p.X).IsGreaterThan(2f); // moved immediately, no warmup tick
    }

    [Test]
    public async Task sample_before_first_and_after_latest_clamp()
    {
        using var runner = new SimulationRunner();
        SpawnMover(runner);
        Tick(runner, 5);

        // Way in the past clamps to a single snapshot (a == b).
        runner.TrySampleInterpolation(-100, out var pa, out var pb, out float pAlpha);
        await Assert.That(ReferenceEquals(pa, pb)).IsTrue();
        await Assert.That(pAlpha).IsEqualTo(0f);

        // Way in the future clamps to the latest single snapshot.
        runner.TrySampleInterpolation(1e9, out var fa, out var fb, out _);
        await Assert.That(ReferenceEquals(fa, fb)).IsTrue();
    }
}
