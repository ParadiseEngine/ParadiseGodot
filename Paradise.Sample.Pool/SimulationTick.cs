using System.Numerics;

namespace Paradise.Sample.Pool;

/// <summary>
/// Shared per-tick prologue for every tick path (the threaded <see cref="SimulationRunner"/> and
/// the single-threaded <see cref="GameSimulation"/>), so the frame invariants live in one place:
/// steering intents are zeroed (stale desired velocities must never leak across ticks) and the
/// shared per-frame <see cref="SimulationContext"/> is refreshed before the systems run.
/// </summary>
public static class SimulationTick
{
    public static void PrepareFrame(World world, float deltaSeconds)
    {
        foreach (var data in world.Query(default(MoveIntents)))
        {
            data.MoveIntent.DesiredVelocity = Vector3.Zero;
        }

        foreach (var data in world.Query(default(SimulationContexts)))
        {
            data.SimulationContext.DeltaSeconds = deltaSeconds;
        }
    }

    /// <summary>
    /// Create every world-system query up front, ON THE OWNER THREAD, right after the world's
    /// schedule is built. World systems bind their query lazily inside the generated
    /// <c>RunWorld</c> (<c>ArchetypeRegistry.GetOrCreateQuery</c>), and the per-world query
    /// cache list is not synchronized — with more than one world system in the parallel wave,
    /// the FIRST tick has them all creating queries concurrently and racing the cache
    /// (observed as IndexOutOfRange in <c>List.set_Item</c>). Once warmed, every later call
    /// hits the read-only fast path, so the wave never mutates the registry again.
    /// Keep in sync with the systems added in <c>SimulationRunner.CreateWorldWithSchedule</c> /
    /// <c>GameSimulation</c>.
    /// </summary>
    public static void WarmSystemQueries(World world)
    {
        world.Query(default(Agents));            // MovementSystem
        world.Query(default(Balls));             // MovementSystem
        world.Query(default(SpriteAnimations));  // SpriteAnimationSystem
        world.Query(default(ParticleEmitters));  // ParticleSystem
    }
}
