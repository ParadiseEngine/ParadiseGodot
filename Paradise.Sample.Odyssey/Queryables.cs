namespace Paradise.Sample.Odyssey;

// Queryables compose the single-variable components into the exact per-variable read/write set each
// system touches (per-variable single-writer ownership). The one ship entity carries every component,
// so it matches all of these. Config/command reads are claimed read-only.

/// <summary>All entities carrying the shared <see cref="SimulationContext"/> — refreshed by
/// <see cref="SimulationTick.PrepareFrame"/>.</summary>
[Queryable]
[With<SimulationContext>]
public readonly ref partial struct SimulationContexts;

/// <summary>The warp-drive charging composition (ChargeSystem). Writes energy + distance; reads the
/// charge command, dt, destroyed-state, and the config bag.</summary>
[Queryable]
[With<WarpEnergy>]
[With<DistanceLy>]
[With<Charging>(IsReadOnly = true)]
[With<Destroyed>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
[With<SectorLadder>(IsReadOnly = true)]
public readonly ref partial struct Chargers;

/// <summary>The warp-jump roll composition (WarpSystem). Writes the intent + rng stream; reads the
/// current energy/sector/destroyed-state and the config bag.</summary>
[Queryable]
[With<WarpIntent>]
[With<RngState>]
[With<WarpEnergy>(IsReadOnly = true)]
[With<SectorIndex>(IsReadOnly = true)]
[With<Destroyed>(IsReadOnly = true)]
[With<SectorLadder>(IsReadOnly = true)]
public readonly ref partial struct Warpers;

/// <summary>The voyage-state owner composition (VoyageSystem). Writes sector/hull/credits/destroyed;
/// reads dt + the config bag (and last frame's events via a reader).</summary>
[Queryable]
[With<SectorIndex>]
[With<HullIntegrity>]
[With<Credits>]
[With<Destroyed>]
[With<SimulationContext>(IsReadOnly = true)]
[With<SectorLadder>(IsReadOnly = true)]
public readonly ref partial struct Voyagers;
