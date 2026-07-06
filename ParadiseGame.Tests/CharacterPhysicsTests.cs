using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using ParadiseGame;
using ParadiseGame.Physics;
using ParadiseGame.Navigation.Detour;
using CollisionWorld = Paradise.Physics.CollisionWorld;

namespace ParadiseGame.Tests;

// End-to-end proof that character movement is resolved by the stateless Paradise.Physics collision
// world: WASD intent stops at obstacle surfaces, slides along walls, never touches Y (planar
// contract), and click-to-move keeps working through steering → intent → cast-and-slide integration.
public class CharacterPhysicsTests
{
    // Matches the FlatGround() navmesh quad [0..20]x[0..20] at y=0: box top face at y=0.
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

    private static Vector3 LatestPosition(SimulationRunner runner, Entity agent)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<LocalTransform>(agent).Position;
    }

    [Test]
    public async Task wasd_into_obstacle_stops_a_body_radius_short_of_the_face()
    {
        // Obstacle -X face at x=4; capsule radius 0.4 → the center can never pass ~3.6.
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(5f, 1.5f, 2f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 3.5f, 0.25f);

        runner.SetMoveInput(agent, new Vector3(1f, 0f, 0f)); // drive straight into the face
        Tick(runner, 600);

        Vector3 final = LatestPosition(runner, agent);
        await Assert.That(final.X).IsGreaterThan(3.4f); // actually reached the obstacle
        await Assert.That(final.X).IsLessThanOrEqualTo(3.6f + 1e-3f); // never penetrates the radius
    }

    [Test]
    public async Task wasd_at_45_degrees_slides_along_the_wall()
    {
        // A long wall with its -X face at x=4: diagonal input keeps making +Z progress while X clamps.
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 10f))],
            [FloorPose, new RigidTransform(new Vector3(5f, 1.5f, 10f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 3.5f, 0.25f);

        runner.SetMoveInput(agent, Vector3.Normalize(new Vector3(1f, 0f, 1f)));
        Tick(runner, 600);

        Vector3 final = LatestPosition(runner, agent);
        await Assert.That(final.X).IsLessThanOrEqualTo(3.6f + 1e-3f); // clamped by the wall
        await Assert.That(final.Z).IsGreaterThan(8f);                 // slid along it
    }

    [Test]
    public async Task y_is_never_modified_by_movement_or_collision()
    {
        CollisionWorld collision = CollisionWorld.Build(
            [FloorBox, ObstacleBox(new Vector3(1f, 1.5f, 1f))],
            [FloorPose, new RigidTransform(new Vector3(5f, 1.5f, 2f), Quaternion.Identity)]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 3.5f, 0.25f);

        runner.SetMoveInput(agent, new Vector3(1f, 0f, 0f));
        for (int i = 0; i < 600; i++)
        {
            runner.TickOnce();
            if (i % 100 != 0) continue;
            Vector3 position = LatestPosition(runner, agent);
            await Assert.That(position.Y).IsEqualTo(0.9f); // bitwise: planar contract
        }
    }

    [Test]
    public async Task click_to_move_arrives_through_the_physics_integrator()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 6f, 0.25f);

        // Simulated click: ray from above the goal straight down, ground-picking filter.
        var click = new RaycastInput
        {
            Start = new Vector3(18f, 5f, 18f),
            End = new Vector3(18f, -5f, 18f),
            Filter = PhysicsLayers.ClickRay,
        };
        bool picked = collision.CastRay(click, out RaycastHit ground);
        await Assert.That(picked).IsTrue();
        await Assert.That(MathF.Abs(ground.Position.Y)).IsLessThanOrEqualTo(1e-3f); // floor top at y=0

        runner.EnqueueMoveTo(agent, ground.Position);
        Tick(runner, 400);

        Vector3 final = LatestPosition(runner, agent);
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        await Assert.That(latest.GetComponent<NavPath>(agent).HasPath).IsEqualTo((byte)0);
        float dx = final.X - 18f;
        float dz = final.Z - 18f;
        await Assert.That(MathF.Sqrt(dx * dx + dz * dz)).IsLessThan(0.6f);
    }

    [Test]
    public async Task agent_stops_when_wasd_input_is_released()
    {
        // Pins the shared tick prologue (SimulationTick.PrepareFrame): MoveIntent is zeroed every
        // tick, so once input stops the stale intent must not keep integrating.
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 3.5f, 0.25f);

        runner.SetMoveInput(agent, new Vector3(1f, 0f, 0f));
        Tick(runner, 30);
        runner.SetMoveInput(agent, Vector3.Zero); // key released

        Tick(runner, 1); // the release takes effect on the next tick
        Vector3 afterRelease = LatestPosition(runner, agent);
        Tick(runner, 60);
        Vector3 later = LatestPosition(runner, agent);

        await Assert.That(afterRelease.X).IsGreaterThan(2f); // it did move while held
        await Assert.That(later).IsEqualTo(afterRelease);    // bitwise frozen after release
    }

    [Test]
    public async Task character_cast_filter_ignores_the_floor_it_rests_on()
    {
        // Capsule bottom rests EXACTLY on the floor top (center y = halfLength + radius = 0.9).
        // The Floor layer is excluded from character casts, so horizontal movement is unobstructed.
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity agent = runner.SpawnAgent(new Vector3(2f, 0.9f, 2f), Quaternion.Identity, 3.5f, 0.25f);

        runner.SetMoveInput(agent, new Vector3(1f, 0f, 0f));
        Tick(runner, 60);

        Vector3 final = LatestPosition(runner, agent);
        await Assert.That(final.X).IsGreaterThan(5f); // ~3.5 m in 60 ticks, nothing blocked it
        await Assert.That(final.Y).IsEqualTo(0.9f);
    }
}
