using System.Numerics;

namespace ParadiseGame.Core;

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
}
