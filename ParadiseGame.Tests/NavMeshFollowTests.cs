using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using ParadiseGame;
using ParadiseGame.Navigation;
using ParadiseGame.Navigation.Detour;

namespace ParadiseGame.Tests;

// End-to-end proof that the shared simulation drives an agent across a DotRecast navmesh with no
// engine host. Entities/components are accessed DIRECTLY on GameSimulation.World (no wrapper), the
// same way the Godot bridge does.
public class NavMeshFollowTests
{
    // A flat 20x20 ground quad on the XZ plane at y=0. Winding gives +Y normals (required by
    // DotRecast — the reversed fan; the naive 0,1,2/0,2,3 order points −Y and makes the funnel zig-zag).
    private static (List<Vector3> verts, List<int> tris) FlatGround()
    {
        var verts = new List<Vector3>
        {
            new(0f, 0f, 0f),
            new(20f, 0f, 0f),
            new(20f, 0f, 20f),
            new(0f, 0f, 20f),
        };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return (verts, tris);
    }

    // An L-shaped floor: arm A covers x[0..8] z[0..4]; the vertical part covers x[4..8] z[4..8].
    // The region x[0..4] z[4..8] is NOT walkable, so a path from arm A's left to the top arm must
    // bend around the inner corner (4,4) — a genuine navmesh detour.
    private static (List<Vector3> verts, List<int> tris) LShapedGround()
    {
        var verts = new List<Vector3>
        {
            new(0f, 0f, 0f),  // 0
            new(4f, 0f, 0f),  // 1
            new(8f, 0f, 0f),  // 2
            new(0f, 0f, 4f),  // 3
            new(4f, 0f, 4f),  // 4
            new(8f, 0f, 4f),  // 5
            new(4f, 0f, 8f),  // 6
            new(8f, 0f, 8f),  // 7
        };
        // +Y-normal winding (reversed fan).
        var tris = new List<int>
        {
            0, 4, 1, 0, 3, 4, // Q1  x[0..4] z[0..4]
            1, 5, 2, 1, 4, 5, // Q2  x[4..8] z[0..4]
            4, 7, 5, 4, 6, 7, // Q3  x[4..8] z[4..8]
        };
        return (verts, tris);
    }

    // ---- Direct-access helpers (mirror how the Godot bridge talks to the world) ----

    private static Entity SpawnAgent(GameSimulation sim, Vector3 position, float moveSpeed)
    {
        return sim.World.CreateEntity(EntityBuilder.Create()
            .Add(new LocalTransform(position, Quaternion.Identity))
            .Add(new NavAgent(moveSpeed, arriveRadius: 0.25f))
            .Add(new NavPath())
            .Add(new MoveIntent())
            .Add(new CharacterBody(radius: 0.4f, halfLength: 0.5f))
            // Seeded: read-only system fields see LAST tick's SimulationContext under snapshot
            // reads — seeding avoids a dt=0 first-tick warmup.
            .Add(new SimulationContext { DeltaSeconds = 1f / 60f })
            .Add(new PhysicsWorldRef())); // no collision world → unobstructed movement
    }

    private static Vector3 PositionOf(GameSimulation sim, Entity e) => sim.World.GetComponent<LocalTransform>(e).Position;

    private static bool HasPath(GameSimulation sim, Entity e) => sim.World.GetComponent<NavPath>(e).HasPath != 0;

    private static void RunUntilArrived(GameSimulation sim, Entity agent, int maxSteps)
    {
        for (int i = 0; i < maxSteps && HasPath(sim, agent); i++)
        {
            sim.Tick(1f / 60f);
        }
    }

    // ---- Tests ----

    [Test]
    public async Task find_path_on_flat_ground_is_taut_not_zigzag()
    {
        var (verts, tris) = FlatGround();
        var nav = new DetourNavigationMesh(verts, tris);

        var start = new Vector3(2f, 0f, 2f);
        var goal = new Vector3(18f, 0f, 18f);
        var path = nav.FindPath(start, goal);

        await Assert.That(path.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(HorizontalDistance(path[^1], goal)).IsLessThan(1.0f);

        // A straight shot across open ground must be taut: total length ≈ straight-line distance.
        // Wrong navmesh winding makes FindStraightPath zig-zag, inflating this well past 1x.
        float total = PathLength(path);
        float straight = HorizontalDistance(start, goal);
        await Assert.That(total).IsLessThan(straight * 1.1f);
    }

    private static float PathLength(IReadOnlyList<Vector3> path)
    {
        float sum = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            sum += HorizontalDistance(path[i - 1], path[i]);
        }
        return sum;
    }

    [Test]
    public async Task agent_walks_to_destination_on_flat_ground()
    {
        var (verts, tris) = FlatGround();
        using var sim = new GameSimulation(new DetourNavigationMesh(verts, tris));

        var goal = new Vector3(18f, 0f, 18f);
        Entity agent = SpawnAgent(sim, new Vector3(2f, 0f, 2f), moveSpeed: 6f);

        bool planned = NavigationPlanner.PlanMoveTo(sim.World, agent, goal, sim.NavigationMesh);
        await Assert.That(planned).IsTrue();

        RunUntilArrived(sim, agent, maxSteps: 3000);

        await Assert.That(HasPath(sim, agent)).IsFalse();
        await Assert.That(HorizontalDistance(PositionOf(sim, agent), goal)).IsLessThan(0.6f);
    }

    [Test]
    public async Task agent_detours_around_the_corner_on_l_shaped_ground()
    {
        var (verts, tris) = LShapedGround();
        var nav = new DetourNavigationMesh(verts, tris);

        var start = new Vector3(2f, 0f, 2f);   // arm A (left)
        var goal = new Vector3(6f, 0f, 7f);    // top arm

        // The path must bend, so it has an intermediate corner (more than just start+goal).
        var path = nav.FindPath(start, goal);
        await Assert.That(path.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(HorizontalDistance(path[^1], goal)).IsLessThan(1.0f);

        // And the agent actually reaches the top arm by following that detour.
        using var sim = new GameSimulation(nav);
        Entity agent = SpawnAgent(sim, start, moveSpeed: 4f);
        NavigationPlanner.PlanMoveTo(sim.World, agent, goal, sim.NavigationMesh);
        RunUntilArrived(sim, agent, maxSteps: 3000);

        await Assert.That(HorizontalDistance(PositionOf(sim, agent), goal)).IsLessThan(0.6f);
    }

    [Test]
    public async Task agent_stays_put_after_arrival()
    {
        // Pins the shared tick prologue on the GameSimulation path: after the path clears, the
        // zeroed MoveIntent must not keep drifting the agent past its goal.
        var (verts, tris) = FlatGround();
        using var sim = new GameSimulation(new DetourNavigationMesh(verts, tris));

        Entity agent = SpawnAgent(sim, new Vector3(2f, 0f, 2f), moveSpeed: 6f);
        NavigationPlanner.PlanMoveTo(sim.World, agent, new Vector3(18f, 0f, 18f), sim.NavigationMesh);
        RunUntilArrived(sim, agent, maxSteps: 3000);
        await Assert.That(HasPath(sim, agent)).IsFalse();

        Vector3 arrived = PositionOf(sim, agent);
        for (int i = 0; i < 60; i++)
        {
            sim.Tick(1f / 60f);
        }

        await Assert.That(PositionOf(sim, agent)).IsEqualTo(arrived); // bitwise: no post-arrival drift
    }

    [Test]
    public async Task agent_without_a_path_does_not_move()
    {
        var (verts, tris) = FlatGround();
        using var sim = new GameSimulation(new DetourNavigationMesh(verts, tris));

        var spawn = new Vector3(5f, 0f, 5f);
        Entity agent = SpawnAgent(sim, spawn, moveSpeed: 6f);

        for (int i = 0; i < 60; i++)
        {
            sim.Tick(1f / 60f);
        }

        await Assert.That(HorizontalDistance(PositionOf(sim, agent), spawn)).IsLessThan(1e-3f);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
