namespace Paradise.Sample.Odyssey;

/// <summary>Shared per-tick prologue: refresh the per-frame <see cref="SimulationContext"/> before the
/// systems run, and (once, on the owner thread) warm every world-system query to avoid a first-tick
/// concurrent cache race. Keep <see cref="WarmSystemQueries"/> in sync with the schedule in
/// <see cref="OdysseyRunner"/>.</summary>
public static class SimulationTick
{
    public static void PrepareFrame(World world, float deltaSeconds)
    {
        foreach (var data in world.Query(default(SimulationContexts)))
        {
            data.SimulationContext.DeltaSeconds = deltaSeconds;
        }
    }

    public static void WarmSystemQueries(World world)
    {
        world.Query(default(Chargers));  // ChargeSystem
        world.Query(default(Warpers));   // WarpSystem
        world.Query(default(Voyagers));  // VoyageSystem
    }
}
