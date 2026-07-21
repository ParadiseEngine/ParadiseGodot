using System.Numerics;

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

// ---------------------------------------------------------------------------------------------------
// SPATIAL LAYER (3D piloting + the procedural sector map).
//
// SOLE writer of Position/Rotation: MotionSystem — over BOTH the ship and every body, in two disjoint
// segments of one system (Ships carry Velocity/Heading; bodies carry OrbitAngle; the sets never
// overlap). Ship kinematics (Velocity/Heading) and body orbit (OrbitAngle/SpinPhase) are likewise
// MotionSystem-owned. The rest are read-only config authored at spawn, managed-written pilot commands
// (ThrustInput/TurnInput), or fields the runner reshuffles between ticks on a warp (OrbitCenter/
// Radius/Speed) — all untracked managed writes, outside the system-injection model.
// ---------------------------------------------------------------------------------------------------

/// <summary>World-space position (right-handed, Y-up, −Z forward) of the ship or a body. Sole writer:
/// MotionSystem. One variable.</summary>
[Component]
public partial struct Position
{
    public Vector3 Value;
}

/// <summary>World-space orientation of the ship or a body. Sole writer: MotionSystem. One variable.</summary>
[Component]
public partial struct Rotation
{
    public Quaternion Value;
}

/// <summary>Ship linear velocity (units/s). Sole writer: MotionSystem. One variable.</summary>
[Component]
public partial struct Velocity
{
    public Vector3 Value;
}

/// <summary>Ship yaw heading (radians; 0 = +Z). Sole writer: MotionSystem. One variable.</summary>
[Component]
public partial struct Heading
{
    public float Value;
}

/// <summary>Pilot thrust command in [-1..1] (forward+), managed-written each tick from the host keys.
/// Read by MotionSystem. One variable.</summary>
[Component]
public partial struct ThrustInput
{
    public float Value;
}

/// <summary>Pilot turn command in [-1..1] (+yaw), managed-written each tick. Read by MotionSystem.
/// One variable.</summary>
[Component]
public partial struct TurnInput
{
    public float Value;
}

/// <summary>CONFIG BAG (read-only, authored at spawn) — the ship flight tuning MotionSystem reads. The
/// flight analog of <see cref="SectorLadder"/>.</summary>
[Component]
public partial struct FlightConfig
{
    /// <summary>Acceleration (units/s²) at full thrust.</summary>
    public float ThrustAccel;

    /// <summary>Yaw rate (rad/s) at full turn.</summary>
    public float TurnRate;

    /// <summary>Velocity decay fraction per second (drag).</summary>
    public float LinearDamping;

    /// <summary>Speed clamp (units/s).</summary>
    public float MaxSpeed;

    /// <summary>Half-extent of the sector cube the ship is clamped within.</summary>
    public float SectorBounds;

    /// <summary>How close (units) to the warp gate counts as "entered" — the fly-to-gate trigger.</summary>
    public float GateCaptureRadius;
}

// --- bodies (star / planets / asteroids / warp gate) ---

/// <summary>Body archetype for the renderers: 0 = star, 1 = planet, 2 = asteroid, 3 = warp gate.
/// Read-only, fixed per entity across warps. One variable.</summary>
[Component]
public partial struct BodyKind
{
    public int Value;
}

/// <summary>Body render scale (radius units). Read-only config, fixed per entity. One variable.</summary>
[Component]
public partial struct BodyScale
{
    public float Value;
}

/// <summary>Body base tint (linear RGBA); star/gate use it as an emissive colour. Read-only config,
/// fixed per entity (so both hosts can bake a material once). One variable.</summary>
[Component]
public partial struct BodyTint
{
    public Vector4 Value;
}

/// <summary>Centre the body orbits about (the star sits at origin; the gate anchors here at radius 0).
/// Managed-reshuffled on warp; read by MotionSystem. One variable.</summary>
[Component]
public partial struct OrbitCenter
{
    public Vector3 Value;
}

/// <summary>Orbit radius (0 = stationary at the centre). Managed-reshuffled on warp; read by
/// MotionSystem. One variable.</summary>
[Component]
public partial struct OrbitRadius
{
    public float Value;
}

/// <summary>Current orbit angle (radians), advanced each tick. Sole writer: MotionSystem (also
/// managed-seeded on warp). One variable.</summary>
[Component]
public partial struct OrbitAngle
{
    public float Value;
}

/// <summary>Orbit angular speed (rad/s). Managed-reshuffled on warp; read by MotionSystem. One variable.</summary>
[Component]
public partial struct OrbitSpeed
{
    public float Value;
}

/// <summary>Accumulated self-spin phase (radians about Y), advanced each tick. Sole writer:
/// MotionSystem. One variable.</summary>
[Component]
public partial struct SpinPhase
{
    public float Value;
}

/// <summary>Self-spin speed (rad/s about Y) for a little visible life (the gate ring rotates in
/// place). Read-only config; read by MotionSystem. One variable.</summary>
[Component]
public partial struct SpinSpeed
{
    public float Value;
}
