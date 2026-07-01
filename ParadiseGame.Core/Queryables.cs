namespace ParadiseGame.Core;

/// <summary>
/// All entities carrying the shared <see cref="SimulationContext"/>. Used by
/// <see cref="GameSimulation.Tick"/> to refresh per-frame data (delta time) on every simulated agent
/// before the systems run.
/// </summary>
[Queryable]
[With<SimulationContext>]
public readonly ref partial struct SimulationContexts;
