using System;
using ParadiseGame.Navigation;

namespace ParadiseGame;

/// <summary>
/// Owns the shared Paradise.ECS world, the navmesh-follow schedule, and the navmesh backend, and
/// advances them each tick. It deliberately does NOT wrap entity/component access — callers
/// (Godot, or the engine runtime) use <see cref="World"/> directly: <c>sim.World.CreateEntity(...)</c>,
/// <c>sim.World.GetComponent&lt;LocalTransform&gt;(entity)</c>, raw <see cref="Entity"/> handles, and
/// <see cref="NavigationPlanner"/>. This type exists only because the source-generated world/schedule
/// generic types can't be named in a caller's field; it holds them and exposes the world.
/// The BankHeist <c>GameState</c> + <c>GameSystemRunner</c> analog. Right-handed world space.
///
/// Runs the same snapshot-read parallel model as <see cref="SimulationRunner"/>: each tick the
/// private previous-world snapshot is refreshed via <c>CopyFrom(World)</c>, systems' read-only
/// fields bind to that immutable snapshot while writable fields bind to <see cref="World"/>
/// (<c>[assembly: SnapshotReadSystems]</c>), and with disjoint writes
/// (<c>[assembly: SingleWriter]</c>) every system executes in one fully parallel wave
/// (<see cref="SnapshotDagScheduler"/> + <see cref="ParallelWaveScheduler"/>).
/// </summary>
public sealed class GameSimulation : IDisposable
{
    private readonly SharedWorld _shared;
    private readonly World _previous;
    private readonly IDisposable _schedule;
    private readonly Action<World> _runSchedule;
    private bool _disposed;

    /// <summary>The shared ECS world — spawn entities and read/write components directly on this.
    /// Always the same instance; the internal snapshot copy is refreshed from it each tick.</summary>
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
        _previous = _shared.CreateWorld();

        var schedule = SystemSchedule.Create(World)
            .AddWorld<MovementSystem>()
            .AddWorld<SpriteAnimationSystem>()
            .AddWorld<ParticleSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        SimulationTick.WarmSystemQueries(World);
        _schedule = schedule;
        _runSchedule = schedule.Run;
    }

    /// <summary>
    /// Advance the simulation by <paramref name="deltaSeconds"/>: capture the previous-tick
    /// snapshot, zero steering intents, refresh the shared <see cref="SimulationContext"/>, then
    /// run the schedule in snapshot-read mode — <see cref="MovementSystem"/> steers, slides, and
    /// resolves ball dynamics in one pass, writing every final transform.
    /// Note: read-only system fields observe last tick's values — the dt and the
    /// <see cref="PhysicsWorldRef"/> a system reads are the ones written the PREVIOUS tick, so
    /// seed <see cref="SimulationContext.DeltaSeconds"/> and <see cref="PhysicsWorldRef"/> at
    /// spawn (as <see cref="SimulationRunner.SpawnAgent"/> does) or movement is a no-op.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        // Capture last tick's final state (including any between-tick caller mutations) as the
        // immutable read source. No structural changes may happen between here and the run.
        _previous.CopyFrom(World);

        SimulationTick.PrepareFrame(World, deltaSeconds);

        _runSchedule(_previous);
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
