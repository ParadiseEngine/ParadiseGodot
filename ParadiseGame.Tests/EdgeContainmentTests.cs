using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using ParadiseGame;
using ParadiseGame.Physics;
using ParadiseGame.Navigation.Detour;
using CollisionWorld = Paradise.Physics.CollisionWorld;

namespace ParadiseGame.Tests;

// Ground-support containment (movers can't leave the walkable slab) and rolling visuals.
public class EdgeContainmentTests
{
    // Floor slab top at y = 0, covering x ∈ [0,20], z ∈ [0,20] — matches the FlatGround navmesh.
    private static readonly Collider FloorBox = Collider.CreateBox(
        new Vector3(10f, 0.5f, 10f), new CollisionFilter { BelongsTo = PhysicsLayers.Floor, CollidesWith = ~0u });

    private static readonly RigidTransform FloorPose = new(new Vector3(10f, -0.5f, 10f), Quaternion.Identity);

    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(20, 0, 0), new(20, 0, 20), new(0, 0, 20) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 }; // +Y winding
        return new DetourNavigationMesh(verts, tris);
    }

    private static Vector3 LatestPosition(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<LocalTransform>(entity).Position;
    }

    [Test]
    public async Task player_cannot_walk_off_the_slab_edge()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity player = runner.SpawnAgent(new Vector3(2f, 0.9f, 5f), Quaternion.Identity, 3.5f, 720f, 0.25f);

        runner.SetMoveInput(player, new Vector3(-1f, 0f, 0f)); // straight at the x = 0 edge
        for (int i = 0; i < 600; i++)
        {
            runner.TickOnce();
            if (i % 20 == 0) _ = LatestPosition(runner, player); // release snapshot pins
        }

        Vector3 final = LatestPosition(runner, player);
        await Assert.That(final.X).IsGreaterThanOrEqualTo(-1e-3f); // held at the rim
        await Assert.That(final.X).IsLessThan(2f);                 // it did walk to the edge
        await Assert.That(final.Y).IsEqualTo(0.9f);
    }

    [Test]
    public async Task player_slides_along_the_slab_edge_on_a_diagonal()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity player = runner.SpawnAgent(new Vector3(2f, 0.9f, 5f), Quaternion.Identity, 3.5f, 720f, 0.25f);

        runner.SetMoveInput(player, Vector3.Normalize(new Vector3(-1f, 0f, 1f)));
        for (int i = 0; i < 400; i++)
        {
            runner.TickOnce();
            if (i % 20 == 0) _ = LatestPosition(runner, player);
        }

        Vector3 final = LatestPosition(runner, player);
        await Assert.That(final.X).IsGreaterThanOrEqualTo(-1e-3f); // clamped by the edge
        await Assert.That(final.Z).IsGreaterThan(8f);              // kept sliding along it
    }

    [Test]
    public async Task ball_stops_at_the_slab_edge()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity ball = runner.SpawnBall(new Vector3(3f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<DynamicBody>(ball).Velocity = new Vector3(-10f, 0f, 0f);

        for (int i = 0; i < 300; i++)
        {
            runner.TickOnce();
            if (i % 10 != 0) continue;
            await Assert.That(LatestPosition(runner, ball).X).IsGreaterThanOrEqualTo(-1e-3f);
        }

        runner.TrySampleInterpolation(double.MaxValue, out var final, out _, out _);
        await Assert.That(final.GetComponent<DynamicBody>(ball).Velocity).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task ball_rolls_visually_while_moving()
    {
        CollisionWorld collision = CollisionWorld.Build([FloorBox], [FloorPose]);
        using var runner = new SimulationRunner(FlatGround(), collision);
        Entity ball = runner.SpawnBall(new Vector3(3f, 0.85f, 5f), Quaternion.Identity, radius: 0.35f);

        runner.TrySampleInterpolation(double.MaxValue, out var world, out _, out _);
        world.GetComponent<DynamicBody>(ball).Velocity = new Vector3(4f, 0f, 0f);

        for (int i = 0; i < 30; i++)
        {
            runner.TickOnce();
        }

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        Quaternion rotation = latest.GetComponent<LocalTransform>(ball).Rotation;
        // Rolled a measurable amount (≈ v·t/r radians) about a horizontal axis.
        await Assert.That(MathF.Abs(Quaternion.Dot(rotation, Quaternion.Identity))).IsLessThan(0.999f);
        // Rolling about Up × v = -Z for +X motion: the axis must be horizontal (no yaw spin).
        await Assert.That(MathF.Abs(rotation.Y)).IsLessThan(1e-3f);
    }
}
