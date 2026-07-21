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

// --- spatial (MotionSystem, the sole Position/Rotation writer, in two disjoint segments) ---

/// <summary>The piloted ship (MotionSystem, ship segment). Writes position/rotation/velocity/heading;
/// reads the pilot commands, the flight config, and dt. The DISTINGUISHING members (Velocity, Heading)
/// exclude every body — the ship is the only entity carrying them.</summary>
[Queryable]
[With<Position>]
[With<Rotation>]
[With<Velocity>]
[With<Heading>]
[With<ThrustInput>(IsReadOnly = true)]
[With<TurnInput>(IsReadOnly = true)]
[With<FlightConfig>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct Ships;

/// <summary>Every orbiting/spinning body (MotionSystem, body segment). Writes position/rotation and
/// advances the orbit angle + spin phase; reads the orbit centre/radius/speed, the spin speed, and dt.
/// The DISTINGUISHING member (OrbitAngle) excludes the ship — no body carries Velocity/Heading and the
/// ship carries no OrbitAngle, so the two segments are disjoint entity sets sharing one Position writer.</summary>
[Queryable]
[With<Position>]
[With<Rotation>]
[With<OrbitAngle>]
[With<SpinPhase>]
[With<OrbitCenter>(IsReadOnly = true)]
[With<OrbitRadius>(IsReadOnly = true)]
[With<OrbitSpeed>(IsReadOnly = true)]
[With<SpinSpeed>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct Bodies;
