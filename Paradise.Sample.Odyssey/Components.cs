namespace Paradise.Sample.Odyssey;

// ---------------------------------------------------------------------------------------------------
// SINGLE-VARIABLE COMPONENTS (the immortal-cultivation discipline, sci-fi skin).
//
// One `Value` (or one logical variable) per component, so single-writer ownership (PECS3008) is
// per-variable. The whole voyage lives on ONE "ship" entity carrying all of these. Ownership:
//   ChargeSystem  → WarpEnergy, DistanceLy
//   WarpSystem    → WarpIntent, RngState
//   VoyageSystem  → SectorIndex, HullIntegrity, Credits, Destroyed
//   managed       → Charging (a command flag), SimulationContext (per-tick dt)
// SectorLadder is the one sanctioned whole-struct exception: a read-only baked config bag.
// ---------------------------------------------------------------------------------------------------

/// <summary>Shared per-tick delta time, refreshed by <see cref="SimulationTick.PrepareFrame"/> before
/// the schedule runs. Read-only in systems (previous-tick under snapshot-read).</summary>
[Component]
public partial struct SimulationContext
{
    public float DeltaSeconds;
}

/// <summary>The sector the ship has reached — the "realm" analog. Sole writer: VoyageSystem
/// (advanced by a successful warp). One variable.</summary>
[Component]
public partial struct SectorIndex
{
    public int Value;
}

/// <summary>Charge accumulated in the warp drive toward the next jump — the "cultivation points"
/// analog. Sole writer: ChargeSystem. One variable.</summary>
[Component]
public partial struct WarpEnergy
{
    public double Value;
}

/// <summary>Light-years travelled — the "age/time" analog (flavor readout). Sole writer:
/// ChargeSystem. One variable.</summary>
[Component]
public partial struct DistanceLy
{
    public double Value;
}

/// <summary>Hull integrity — the "lifespan" analog. Degrades over time and on failed jumps; a breach
/// (≤ 0) ends the voyage. Sole writer: VoyageSystem. One variable.</summary>
[Component]
public partial struct HullIntegrity
{
    public double Value;
}

/// <summary>Credits — the "spirit stones" analog, awarded per successful jump. Sole writer:
/// VoyageSystem. One variable.</summary>
[Component]
public partial struct Credits
{
    public int Value;
}

/// <summary>Command flag: 1 while the warp drive is charging (the "cultivate" toggle). Written by the
/// runner (managed, untracked), read by ChargeSystem. One variable.</summary>
[Component]
public partial struct Charging
{
    public byte Value;
}

/// <summary>The warp-jump INTENT — the rng-bound action's request (the immortal-cultivation
/// intent→system→event seam). The runner sets <see cref="Pending"/> = 1 (managed); WarpSystem is the
/// sole SYSTEM writer, rolling the jump and clearing it. One logical variable.</summary>
[Component]
public partial struct WarpIntent
{
    public byte Pending;
}

/// <summary>Per-ship deterministic xorshift32 RNG stream for the warp roll — the repo's established
/// determinism pattern (never seeded 0). Sole writer: WarpSystem. One variable.</summary>
[Component]
public partial struct RngState
{
    public uint Value;
}

/// <summary>1 once the hull has breached — the voyage is over until a new one. Sole writer:
/// VoyageSystem. One variable.</summary>
[Component]
public partial struct Destroyed
{
    public byte Value;
}

/// <summary>CONFIG BAG (read-only, authored at spawn) — the warp/hull tuning the systems read. The
/// one sanctioned whole-struct component.</summary>
[Component]
public partial struct SectorLadder
{
    /// <summary>Energy required to attempt a jump.</summary>
    public double EnergyPerJump;

    /// <summary>Energy gained per second while <see cref="Charging"/>.</summary>
    public double ChargeRate;

    /// <summary>Light-years covered per second (flavor).</summary>
    public double CruiseSpeed;

    /// <summary>Jump success chance at sector 0.</summary>
    public float BaseJumpChance;

    /// <summary>Success chance lost per sector already reached (deeper space is riskier).</summary>
    public float ChancePenaltyPerSector;

    /// <summary>Minimum jump chance (a floor so a jump is never hopeless).</summary>
    public float MinJumpChance;

    /// <summary>Hull lost per second of travel.</summary>
    public double HullDrainPerSec;

    /// <summary>Hull lost on a FAILED jump.</summary>
    public double HullDamageOnFail;

    /// <summary>Hull restored on a SUCCESSFUL jump (clamped to <see cref="FullHull"/>).</summary>
    public double HullRepairOnJump;

    /// <summary>Full / starting hull.</summary>
    public double FullHull;

    /// <summary>Credits awarded per successful jump.</summary>
    public int CreditsPerJump;
}
