using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Navigation.Detour;

namespace Paradise.Sample.Pool.Tests;

// Drives SimulationRunner synchronously via TickOnce (no thread) to verify the double-buffer model:
// each tick publishes a new immutable snapshot copied from the previous, entity handles survive CopyFrom
// across snapshots, and TrySampleInterpolation brackets the right pair with the right alpha.
public class SimulationRunnerTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(20, 0, 0), new(20, 0, 20), new(0, 0, 20) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 }; // +Y winding
        return new DetourNavigationMesh(verts, tris);
    }

    private static void Tick(SimulationRunner runner, int count)
    {
        for (int i = 0; i < count; i++)
        {
            runner.TickOnce();
        }
    }

    [Test]
    public async Task snapshots_advance_the_agent_toward_its_destination()
    {
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(2, 0, 2), Quaternion.Identity, moveSpeed: 6f, arriveRadius: 0.25f);
        var goal = new Vector3(18, 0, 18);
        runner.EnqueueMoveTo(agent, goal);

        Tick(runner, 400);

        // Read the latest published snapshot.
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        Vector3 t = latest.GetComponent<Position>(agent).Value;
        byte hasPath = latest.GetComponent<HasPath>(agent).Value;

        await Assert.That(HorizontalDistance(t, goal)).IsLessThan(0.6f);
        await Assert.That(hasPath).IsEqualTo((byte)0);
    }

    [Test]
    public async Task consecutive_snapshots_preserve_the_handle_and_interpolate()
    {
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(2, 0, 2), Quaternion.Identity, moveSpeed: 3f, arriveRadius: 0.25f);
        runner.EnqueueMoveTo(agent, new Vector3(18, 0, 2)); // move along +X so positions change each tick

        Tick(runner, 20); // agent is mid-travel; several snapshots in the ring

        double latestTime = runner.LatestSnapshotTime;
        // Sample halfway between the two latest snapshots (one FixedDeltaSeconds apart).
        double sampleTime = latestTime - SimulationRunner.FixedDeltaSeconds * 0.5;

        bool ok = runner.TrySampleInterpolation(sampleTime, out var a, out var b, out float alpha);
        await Assert.That(ok).IsTrue();

        // The same entity handle is valid in BOTH snapshot worlds (CopyFrom preserved it).
        await Assert.That(a.IsAlive(agent)).IsTrue();
        await Assert.That(b.IsAlive(agent)).IsTrue();

        // Distinct snapshots, alpha ~0.5, and the interpolated X lies between the two.
        await Assert.That(alpha).IsGreaterThan(0.1f);
        await Assert.That(alpha).IsLessThan(0.9f);
        float xa = a.GetComponent<Position>(agent).Value.X;
        float xb = b.GetComponent<Position>(agent).Value.X;
        await Assert.That(xb).IsGreaterThan(xa); // moved +X between the two snapshots
        float xi = float.Lerp(xa, xb, alpha);
        await Assert.That(xi).IsGreaterThanOrEqualTo(xa);
        await Assert.That(xi).IsLessThanOrEqualTo(xb);
    }

    [Test]
    public async Task held_snapshot_is_not_recycled_while_the_renderer_reads_it()
    {
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(2, 0, 2), Quaternion.Identity, 3f, 0.25f);
        runner.EnqueueMoveTo(agent, new Vector3(18, 0, 2));
        Tick(runner, 10);

        // Acquire (pin) a pair and keep the references — mimicking a render frame in progress.
        runner.TrySampleInterpolation(runner.LatestSnapshotTime - SimulationRunner.FixedDeltaSeconds * 0.5, out var a, out _, out _);
        float xBefore = a.GetComponent<Position>(agent).Value.X;

        // Advance the sim far past the interpolation window WITHOUT sampling again. A recycle-based design
        // would overwrite `a`; the pin must keep it alive and unchanged.
        Tick(runner, 300);

        await Assert.That(a.IsAlive(agent)).IsTrue();
        await Assert.That(a.GetComponent<Position>(agent).Value.X).IsEqualTo(xBefore);
    }

    [Test]
    public async Task agent_moves_on_the_very_first_tick()
    {
        // E2E pin of snapshot-read execution + dt seeding: MovementSystem reads
        // SimulationContext from the CURRENT (previous-tick) world. On tick 1 that is the
        // initial snapshot — if SpawnAgent didn't seed DeltaSeconds, the system would see
        // dt == 0 and skip the tick.
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(2, 0, 2), Quaternion.Identity, moveSpeed: 6f, arriveRadius: 0.25f);
        runner.EnqueueMoveTo(agent, new Vector3(18, 0, 18));

        runner.TickOnce();

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        Vector3 p = latest.GetComponent<Position>(agent).Value;
        await Assert.That(p.X > 2f || p.Z > 2f).IsTrue(); // moved immediately, no warmup tick
    }

    [Test]
    public async Task direct_move_input_moves_the_agent_without_touching_y()
    {
        // Movement collision is owned by physics now (see CharacterPhysicsTests); with no collision
        // world the intent integrates unobstructed. The navmesh no longer clamps WASD movement.
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(2, 0.9f, 2), Quaternion.Identity, moveSpeed: 6f, arriveRadius: 0.25f);

        runner.SetMoveInput(agent, new Vector3(1, 0, 0)); // hold +X
        Tick(runner, 60);
        runner.TrySampleInterpolation(double.MaxValue, out var w1, out _, out _);
        Vector3 p1 = w1.GetComponent<Position>(agent).Value;
        await Assert.That(p1.X).IsGreaterThan(2f);   // moved +X at MoveSpeed
        await Assert.That(p1.Y).IsEqualTo(0.9f);     // planar contract: Y untouched
        await Assert.That(p1.Z).IsEqualTo(2f);
    }

    [Test]
    public async Task direct_move_overrides_an_active_path_the_same_tick()
    {
        // WASD is applied BEFORE the schedule: DirectMover clears HasPath (steering skips, so the
        // path intent is never written) and its own intent is what MovementSystem integrates —
        // the very tick both inputs are present, WASD wins.
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(10, 0, 10), Quaternion.Identity, moveSpeed: 6f, arriveRadius: 0.25f);
        runner.EnqueueMoveTo(agent, new Vector3(10, 0, 18)); // path wants +Z
        runner.SetMoveInput(agent, new Vector3(1, 0, 0));    // WASD wants +X

        runner.TickOnce();

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        Vector3 p = latest.GetComponent<Position>(agent).Value;
        await Assert.That(p.X).IsGreaterThan(10f);                        // moved +X (WASD)
        await Assert.That(p.Z).IsEqualTo(10f);                            // not +Z (path suppressed)
        await Assert.That(latest.GetComponent<HasPath>(agent).Value).IsEqualTo((byte)0); // path cleared
    }

    [Test]
    public async Task sample_before_first_and_after_latest_clamp()
    {
        using var runner = new SimulationRunner(FlatGround());
        runner.SpawnAgent(new Vector3(5, 0, 5), Quaternion.Identity, 3f, 0.25f);
        Tick(runner, 5);

        // Way in the past clamps to a single snapshot (a == b).
        runner.TrySampleInterpolation(-100, out var pa, out var pb, out float pAlpha);
        await Assert.That(ReferenceEquals(pa, pb)).IsTrue();
        await Assert.That(pAlpha).IsEqualTo(0f);

        // Way in the future clamps to the latest single snapshot.
        runner.TrySampleInterpolation(1e9, out var fa, out var fb, out _);
        await Assert.That(ReferenceEquals(fa, fb)).IsTrue();
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
