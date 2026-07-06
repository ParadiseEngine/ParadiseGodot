using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using ParadiseGame;
using ParadiseGame.Physics;
using ParadiseGame.Navigation.Detour;
using CollisionWorld = Paradise.Physics.CollisionWorld;

namespace ParadiseGame.Tests;

// End-to-end dynamics through the runner/simulation: the player pushes balls, balls bounce off
// obstacles and each other, all state lives in components (planar contract: Y untouched).
public class DynamicBallTests
{
    private static readonly Collider FloorBox = Collider.CreateBox(
        new Vector3(10f, 0.5f, 10f), new CollisionFilter { BelongsTo = PhysicsLayers.Floor, CollidesWith = ~0u });

    private static readonly RigidTransform FloorPose = new(new Vector3(10f, -0.5f, 10f), Quaternion.Identity);

    private static Collider ObstacleBox(Vector3 halfExtents) => Collider.CreateBox(
        halfExtents, new CollisionFilter { BelongsTo = PhysicsLayers.Obstacle, CollidesWith = ~0u });

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

    private static Vector3 LatestPosition(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<LocalTransform>(entity).Position;
    }

    [Test]
    public async Task character_pushes_ball_forward()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity player = runner.SpawnAgent(new Vector3(2f, 0.9f, 5f), Quaternion.Identity, 3.5f, 0.25f);
        Entity ball = runner.SpawnBall(new Vector3(4f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.SetMoveInput(player, new Vector3(1f, 0f, 0f)); // walk into the ball
        Tick(runner, 120);
        runner.SetMoveInput(player, Vector3.Zero);
        Tick(runner, 120); // let everything settle

        Vector3 ballPos = LatestPosition(runner, ball);
        Vector3 playerPos = LatestPosition(runner, player);

        await Assert.That(ballPos.X).IsGreaterThan(4.5f); // shoved along the push direction
        float dx = ballPos.X - playerPos.X;
        float dz = ballPos.Z - playerPos.Z;
        float centerDistance = MathF.Sqrt(dx * dx + dz * dz);
        await Assert.That(centerDistance).IsGreaterThanOrEqualTo(0.4f + 0.35f - 0.05f); // non-overlapping at rest
    }

    [Test]
    public async Task ball_never_penetrates_obstacle()
    {
        // Obstacle -X face at x = 8; ball radius 0.35 → center must stay ≤ 7.65 (+ tolerance).
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity ball = runner.SpawnBall(new Vector3(2f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        // Launch the ball at the obstacle by seeding velocity on the initial snapshot.
        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<DynamicBody>(ball).Velocity = new Vector3(8f, 0f, 0f);

        for (int i = 0; i < 300; i++)
        {
            runner.TickOnce();
            if (i % 10 != 0) continue;
            Vector3 position = LatestPosition(runner, ball);
            await Assert.That(position.X).IsLessThanOrEqualTo(8f - 0.35f + 1e-2f);
        }

        // Ended reflected (moving away) or at rest — never inside.
        runner.TrySampleInterpolation(double.MaxValue, out var final, out _, out _);
        await Assert.That(final.GetComponent<DynamicBody>(ball).Velocity.X).IsLessThanOrEqualTo(1e-3f);
    }

    [Test]
    public async Task balls_collide_and_transfer_momentum()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity ballA = runner.SpawnBall(new Vector3(4f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);
        Entity ballB = runner.SpawnBall(new Vector3(6f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<DynamicBody>(ballA).Velocity = new Vector3(6f, 0f, 0f);

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

    [Test]
    public async Task ball_y_is_never_modified()
    {
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity player = runner.SpawnAgent(new Vector3(2f, 0.9f, 5f), Quaternion.Identity, 3.5f, 0.25f);
        Entity ball = runner.SpawnBall(new Vector3(4f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.SetMoveInput(player, new Vector3(1f, 0f, 0f)); // push the ball into the obstacle
        for (int i = 0; i < 400; i++)
        {
            runner.TickOnce();
            if (i % 20 != 0) continue; // sample within the pool window so pins keep releasing
            await Assert.That(LatestPosition(runner, ball).Y).IsEqualTo(0.85f); // bitwise
        }
    }

    [Test]
    public async Task dynamics_are_bitwise_deterministic()
    {
        var results = new List<int>[2];
        for (int pass = 0; pass < 2; pass++)
        {
            CollisionWorld collision = CollisionWorld.Build(
                [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
                [FloorPose, new RigidTransform(new Vector3(9f, 1.5f, 5f), Quaternion.Identity)]);
            var (verts, tris) = (new List<Vector3> { new(0, 0, 0), new(20, 0, 0), new(20, 0, 20), new(0, 0, 20) },
                                 new List<int> { 0, 2, 1, 0, 3, 2 });
            using var sim = new GameSimulation(new DetourNavigationMesh(verts, tris), collision);

            Entity player = sim.World.CreateEntity(EntityBuilder.Create()
                .Add(new LocalTransform(new Vector3(2f, 0.9f, 5f), Quaternion.Identity))
                .Add(new NavAgent(3.5f, 0.25f))
                .Add(new NavPath())
                .Add(new MoveIntent())
                .Add(new CharacterBody(0.4f, 0.5f))
                .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
                .Add(new PhysicsWorldRef { Handle = collision.Handle }));
            Entity ballA = sim.World.CreateEntity(EntityBuilder.Create()
                .Add(new LocalTransform(new Vector3(4f, 0.85f, 5f), Quaternion.Identity))
                .Add(new DynamicBody(0.35f, 1f))
                .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
                .Add(new PhysicsWorldRef { Handle = collision.Handle }));
            Entity ballB = sim.World.CreateEntity(EntityBuilder.Create()
                .Add(new LocalTransform(new Vector3(5.2f, 0.85f, 5.3f), Quaternion.Identity))
                .Add(new DynamicBody(0.35f, 1f))
                .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
                .Add(new PhysicsWorldRef { Handle = collision.Handle }));

            sim.World.GetComponent<DynamicBody>(ballA).Velocity = new Vector3(5f, 0f, 1f);
            for (int i = 0; i < 300; i++)
            {
                sim.Tick(1f / 60f);
            }

            var sink = new List<int>();
            foreach (Entity e in new[] { player, ballA, ballB })
            {
                Vector3 p = sim.World.GetComponent<LocalTransform>(e).Position;
                sink.Add(BitConverter.SingleToInt32Bits(p.X));
                sink.Add(BitConverter.SingleToInt32Bits(p.Z));
            }
            results[pass] = sink;
        }

        await Assert.That(results[0].SequenceEqual(results[1])).IsTrue();
    }
}
