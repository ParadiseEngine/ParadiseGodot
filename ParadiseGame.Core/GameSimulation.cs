using System;
using ParadiseGame.Core.Navigation;
using ParadiseGame.Core.Physics;

namespace ParadiseGame.Core;

/// <summary>
/// Owns the shared Paradise.ECS world, the navmesh-follow schedule, and the navmesh backend, and
/// advances them each tick. It deliberately does NOT wrap entity/component access — callers
/// (Godot, or the engine runtime) use <see cref="World"/> directly: <c>sim.World.CreateEntity(...)</c>,
/// <c>sim.World.GetComponent&lt;LocalTransform&gt;(entity)</c>, raw <see cref="Entity"/> handles, and
/// <see cref="NavigationPlanner"/>. This type exists only because the source-generated world/schedule
/// generic types can't be named in a caller's field; it holds them and exposes the world.
/// The BankHeist <c>GameState</c> + <c>GameSystemRunner</c> analog. Right-handed world space.
/// </summary>
public sealed class GameSimulation : IDisposable
{
    private readonly SharedWorld _shared;
    private readonly IDisposable _schedule;
    private readonly Action _runSchedule;
    private bool _disposed;

    /// <summary>The shared ECS world — spawn entities and read/write components directly on this.</summary>
    public World World { get; }

    /// <summary>The navmesh backend, for <see cref="NavigationPlanner.PlanMoveTo"/>.</summary>
    public INavigationMesh NavigationMesh { get; }

    /// <summary>The immutable static collision world, if any (null = unobstructed integration).</summary>
    public Paradise.Physics.CollisionWorld? CollisionWorld { get; }

    public GameSimulation(INavigationMesh navigationMesh, Paradise.Physics.CollisionWorld? collisionWorld = null)
    {
        NavigationMesh = navigationMesh ?? throw new ArgumentNullException(nameof(navigationMesh));
        CollisionWorld = collisionWorld;
        _shared = SharedWorldFactory.Create();
        World = _shared.CreateWorld();

        var schedule = SystemSchedule.Create(World)
            .Add<NavMeshFollowSystem>()
            .Build<SequentialWaveScheduler>();
        _schedule = schedule;
        _runSchedule = schedule.Run;
    }

    /// <summary>
    /// Advance the simulation by <paramref name="deltaSeconds"/>: zero steering intents, refresh
    /// the shared <see cref="SimulationContext"/>, run the steering schedule, then integrate
    /// intents against the collision world (unobstructed when none was provided).
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        SimulationTick.PrepareFrame(World, deltaSeconds);

        _runSchedule();

        CharacterMoveIntegrator.Step(World, CollisionWorld, deltaSeconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _schedule.Dispose();
        _shared.Dispose();
    }
}
