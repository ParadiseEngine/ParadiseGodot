namespace ParadiseCultivation;

/// <summary>
/// All entities carrying the shared <see cref="SimulationContext"/> — used by the runner to
/// refresh per-tick data (dt, day, month crossings) on every simulated entity before the
/// schedule runs (the ParadiseGame SimulationTick.PrepareFrame pattern).
/// </summary>
[Queryable]
[With<SimulationContext>]
public readonly ref partial struct SimulationContexts;
