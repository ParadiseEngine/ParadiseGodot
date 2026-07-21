using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Physics;
using CollisionWorld = Paradise.Physics.CollisionWorld;

namespace Paradise.Sample.Pool.Tests;

// End-to-end dynamics through the runner/simulation: the player pushes balls, balls bounce off
// obstacles and each other, all state lives in components (planar contract: Y untouched).
public class DynamicBallTests
{
    private static readonly Collider FloorBox = Collider.CreateBox(
        new Vector3(10f, 0.5f, 10f), new CollisionFilter { BelongsTo = PhysicsLayers.Floor, CollidesWith = ~0u });

    private static readonly RigidTransform FloorPose = new(new Vector3(10f, -0.5f, 10f), Quaternion.Identity);

    private static Collider ObstacleBox(Vector3 halfExtents) => Collider.CreateBox(
        halfExtents, new CollisionFilter { BelongsTo = PhysicsLayers.Obstacle, CollidesWith = ~0u });

    private static void Tick(SimulationRunner runner, int count)
    {
        for (int i = 0; i < count; i++)
        {
            runner.TickOnce();
        }
    }

    private static Vector3 LatestPosition(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<Position>(entity).Value;
    }

    [Test]
    public async Task global_tuning_min_speed_settles_slow_supported_balls_sooner()
    {
        // The rest threshold is authored data (PhysicsTuning), applied only to a SUPPORTED ball.
        // A ball resting on the floor and nudged to 0.4 m/s rolls under the default MinSpeed but
        // is snapped to rest immediately under MinSpeed 0.5.
        using var runner = new SimulationRunner(CollisionWorld.Build([FloorBox], [FloorPose]));
        Entity ball = runner.SpawnBall(new Vector3(4f, 0.35f, 4f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, angularDamping: 0f);
        runner.EnqueueBallImpulse(ball, new Vector3(0.4f, 0f, 0f));
        Tick(runner, 60);
        float rolled = LatestPosition(runner, ball).X;

        using var sticky = new SimulationRunner(CollisionWorld.Build([FloorBox], [FloorPose]));
        Entity snapped = sticky.SpawnBall(new Vector3(4f, 0.35f, 4f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, angularDamping: 0f, tuning: PhysicsTuning.Default with { MinSpeed = 0.5f });
        sticky.EnqueueBallImpulse(snapped, new Vector3(0.4f, 0f, 0f));
        for (int i = 0; i < 60; i++) sticky.TickOnce();
        float settled = LatestPosition(sticky, snapped).X;

        await Assert.That(rolled).IsGreaterThan(settled + 0.1f); // low MinSpeed rolls; high MinSpeed settles at once
    }

    [Test]
    public async Task ball_never_penetrates_obstacle()
    {
        // Obstacle -X face at x = 8; ball radius 0.35 → center must stay ≤ 7.65 (+ tolerance).
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(collision);
        Entity ball = runner.SpawnBall(new Vector3(2f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        // Launch the ball at the obstacle by seeding velocity on the initial snapshot.
        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<Velocity>(ball).Value = new Vector3(8f, 0f, 0f);

        for (int i = 0; i < 300; i++)
        {
            runner.TickOnce();
            if (i % 10 != 0) continue;
            Vector3 position = LatestPosition(runner, ball);
            await Assert.That(position.X).IsLessThanOrEqualTo(8f - 0.35f + 1e-2f);
        }

        // Not driving into the obstacle: reflected (moving away, ≤ 0) or nearly at rest. The
        // bound is 0.1 m/s (not ~0) because under the 3D solver a rebounded ball keeps rolling
        // with friction residual for a while; the strong guarantee is the never-penetrates check
        // in the loop above.
        runner.TrySampleInterpolation(double.MaxValue, out var final, out _, out _);
        await Assert.That(final.GetComponent<Velocity>(ball).Value.X).IsLessThanOrEqualTo(0.1f);
    }

    [Test]
    public async Task english_bends_the_ball_off_a_cushion_end_to_end()
    {
        // The headline spin feature through the FULL stack: EnqueueBallImpulse angular →
        // DynamicBody.AngularVelocity → MovementSystem gather → engine friction at the cushion.
        // Right english (ω.y) deflects the rebound in Z vs a spinless control on the same shot.
        static CollisionWorld Table() => CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);

        using var spun = new SimulationRunner(Table());
        Entity a = spun.SpawnBall(new Vector3(2f, 0.35f, 5f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, friction: 0.6f);
        spun.EnqueueBallImpulse(a, new Vector3(6f, 0f, 0f), new Vector3(0f, 30f, 0f)); // +X with strong english
        Tick(spun, 150);
        float spunZ = LatestPosition(spun, a).Z;

        using var plain = new SimulationRunner(Table());
        Entity b = plain.SpawnBall(new Vector3(2f, 0.35f, 5f), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, friction: 0.6f);
        plain.EnqueueBallImpulse(b, new Vector3(6f, 0f, 0f), Vector3.Zero);
        Tick(plain, 150);
        float plainZ = LatestPosition(plain, b).Z;

        await Assert.That(MathF.Abs(spunZ - plainZ)).IsGreaterThan(0.1f); // english bent the rebound
    }

    [Test]
    public async Task balls_collide_and_transfer_momentum()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(collision);
        Entity ballA = runner.SpawnBall(new Vector3(4f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);
        Entity ballB = runner.SpawnBall(new Vector3(6f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<Velocity>(ballA).Value = new Vector3(6f, 0f, 0f);

        for (int i = 0; i < 300; i++)
        {
            runner.TickOnce();
            if (i % 10 == 0) _ = LatestPosition(runner, ballA); // release snapshot pins as we go
        }

        Vector3 posA = LatestPosition(runner, ballA);
        Vector3 posB = LatestPosition(runner, ballB);

        await Assert.That(posB.X).IsGreaterThan(6.2f); // B was knocked forward
        float dx = posB.X - posA.X;
        float dz = posB.Z - posA.Z;
        await Assert.That(MathF.Sqrt(dx * dx + dz * dz)).IsGreaterThanOrEqualTo(0.7f - 0.02f); // separated
    }

    // NOTE: the old "ball Y is never modified" test was removed — balls are full 3D now and rest
    // on the felt via gravity+contact (Y is live). The planar Y-lock only remains for CHARACTERS.

    [Test]
    public async Task dynamics_are_bitwise_deterministic()
    {
        var results = new List<int>[2];
        for (int pass = 0; pass < 2; pass++)
        {
            CollisionWorld collision = CollisionWorld.Build(
                [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
                [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);
            using var sim = new GameSimulation(collision);

            Entity ballA = sim.World.CreateEntity(EntityBuilder.Create()
                .Add(new Position { Value = new Vector3(4f, 0.85f, 5f) })
                .Add(new Rotation { Value = Quaternion.Identity })
                .Add(new Velocity())
                .Add(new AngularVelocity())
                .Add(new BallPhysicsConfig(0.35f, 1f))
                .Add(new BallGlow())
                .Add(new BallSunk())
                .Add(new BallSinking())
                .Add(new SinkTargetY())
                .Add(new PocketConfig())
                .Add(PhysicsTuning.Default)
                .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
                .Add(new PhysicsWorldRef { Handle = collision.Handle }));
            Entity ballB = sim.World.CreateEntity(EntityBuilder.Create()
                .Add(new Position { Value = new Vector3(5.2f, 0.85f, 5.3f) })
                .Add(new Rotation { Value = Quaternion.Identity })
                .Add(new Velocity())
                .Add(new AngularVelocity())
                .Add(new BallPhysicsConfig(0.35f, 1f))
                .Add(new BallGlow())
                .Add(new BallSunk())
                .Add(new BallSinking())
                .Add(new SinkTargetY())
                .Add(new PocketConfig())
                .Add(PhysicsTuning.Default)
                .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
                .Add(new PhysicsWorldRef { Handle = collision.Handle }));

            sim.World.GetComponent<Velocity>(ballA).Value = new Vector3(5f, 0f, 1f);
            for (int i = 0; i < 300; i++)
            {
                sim.Tick(1f / 60f);
            }

            var sink = new List<int>();
            foreach (Entity e in new[] { ballA, ballB })
            {
                Vector3 p = sim.World.GetComponent<Position>(e).Value;
                sink.Add(BitConverter.SingleToInt32Bits(p.X));
                sink.Add(BitConverter.SingleToInt32Bits(p.Z));
            }
            results[pass] = sink;
        }

        await Assert.That(results[0].SequenceEqual(results[1])).IsTrue();
    }
}
