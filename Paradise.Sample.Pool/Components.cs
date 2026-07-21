using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paradise.Sample.Pool;

// ---------------------------------------------------------------------------------------------------
// SINGLE-VARIABLE COMPONENTS (the immortal-cultivation discipline, applied to the pool sample).
//
// Writer-first split: each MUTATED variable is its own component, so single-writer ownership
// (PECS3008, [assembly: SingleWriter]) is enforced per-variable and write conflicts stay rare.
// New single components carry one `Value` field; a `[Queryable]` composes the singles each system
// reads/writes.
//
// THREE sanctioned exceptions keep a whole struct (same as immortal-cultivation):
//   (1) read-only BAKED CONFIG bags — an atomic snapshot of authored data, never partially written
//       (NavAgent, CharacterBody, BallPhysicsConfig, SpriteConfig, ParticleConfig, PhysicsTuning);
//   (2) INLINE-BUFFER / runtime-state bags — an unmanaged inline array must live inside one component
//       (NavWaypoints, PocketConfig, ParticleState);
//   (3) nothing else.
// Everything mutated as a plain scalar/vector is one variable per component.
// ---------------------------------------------------------------------------------------------------

/// <summary>Shared per-tick delta time, refreshed by <see cref="SimulationTick.PrepareFrame"/> before
/// the schedule runs (Paradise.ECS injects component instances, so shared per-frame data is a component
/// every simulated entity carries). Read-only in systems (previous-tick under snapshot-read).</summary>
[Component]
public partial struct SimulationContext
{
    public float DeltaSeconds;
}

// --- transform (sole writer: MovementSystem) --------------------------------------------------------

/// <summary>World-space position, right-handed (Y-up, −Z forward). One variable.</summary>
[Component]
public partial struct Position
{
    public Vector3 Value;
}

/// <summary>World-space orientation. One variable.</summary>
[Component]
public partial struct Rotation
{
    public Quaternion Value;
}

// --- navmesh steering -------------------------------------------------------------------------------

/// <summary>Steering config for a navmesh agent (m/s, m). CONFIG BAG — read-only, authored at spawn.</summary>
[Component]
public partial struct NavAgent
{
    public float MoveSpeed;
    public float ArriveRadius;

    public NavAgent(float moveSpeed, float arriveRadius)
    {
        MoveSpeed = moveSpeed;
        ArriveRadius = arriveRadius;
    }
}

/// <summary>The navmesh path's waypoints. INLINE-BUFFER BAG — filled atomically by
/// <see cref="Navigation.NavigationPlanner"/> (managed) and read by <see cref="MovementSystem"/>.</summary>
[Component]
public partial struct NavWaypoints
{
    public const int MaxWaypoints = 32;

    public WaypointBuffer Waypoints;
    public int Count;
}

/// <summary>Fixed-capacity inline buffer of waypoints (C# 12 InlineArray — unmanaged, blittable).</summary>
[InlineArray(NavWaypoints.MaxWaypoints)]
public struct WaypointBuffer
{
    private Vector3 _element0;
}

/// <summary>Cursor into <see cref="NavWaypoints"/> (sole writer: MovementSystem's steer). One variable.</summary>
[Component]
public partial struct NavCursor
{
    public int Value;
}

/// <summary>1 while a path is being followed; cleared by MovementSystem on arrival, set by the planner.
/// One variable (MovementSystem is the sole SYSTEM-writer; the planner writes untracked/managed).</summary>
[Component]
public partial struct HasPath
{
    public byte Value;
}

/// <summary>This tick's desired velocity (m/s, horizontal) — the steering INTENT, produced by steering
/// (or direct input) and consumed by <see cref="MovementSystem"/>. Zeroed each tick. One variable.</summary>
[Component]
public partial struct MoveIntent
{
    public Vector3 DesiredVelocity;
}

/// <summary>Character collision capsule (Y-aligned, origin at center). CONFIG BAG — read-only.</summary>
[Component]
public partial struct CharacterBody
{
    public float Radius;
    public float HalfLength;

    public CharacterBody(float radius, float halfLength)
    {
        Radius = radius;
        HalfLength = halfLength;
    }
}

/// <summary>Borrowed handle to the session's static <c>CollisionWorld</c>, carried as a component so
/// the generated system can query collision without a managed service. One variable.</summary>
[Component]
public partial struct PhysicsWorldRef
{
    public Paradise.Physics.CollisionWorldHandle Handle;
}

// --- ball dynamics (sole writer: MovementSystem) ----------------------------------------------------

/// <summary>Linear velocity (m/s). One variable.</summary>
[Component]
public partial struct Velocity
{
    public Vector3 Value;
}

/// <summary>Full 3D angular velocity (rad/s) — sidespin (Y) + top/back-spin (horizontal axis). Coupled
/// to linear motion by the solver's Coulomb friction. One variable.</summary>
[Component]
public partial struct AngularVelocity
{
    public Vector3 Value;
}

/// <summary>Per-ball physics constants marshalled into the stateless <c>RigidSphereDynamics</c> solver
/// each tick. CONFIG BAG — authored at spawn, never mutated (the mutated state is
/// <see cref="Velocity"/>/<see cref="AngularVelocity"/>/<see cref="Position"/>).</summary>
[Component]
public partial struct BallPhysicsConfig
{
    public float Radius;
    public float Mass;
    public float LinearDamping;
    public float AngularDamping;
    public float Restitution;
    public float StaticRestitution;
    public float Friction;

    public BallPhysicsConfig(float radius, float mass,
        float linearDamping = 1.5f, float restitution = 0.6f, float staticRestitution = 0.4f,
        float friction = 0.3f, float angularDamping = 0.4f)
    {
        Radius = radius;
        Mass = mass;
        LinearDamping = linearDamping;
        AngularDamping = angularDamping;
        Restitution = restitution;
        StaticRestitution = staticRestitution;
        Friction = friction;
    }
}

/// <summary>Collision glow intensity: spikes on a ball↔ball hit and decays each tick; the renderer maps
/// it onto the ball's point light. Sole writer: MovementSystem. One variable.</summary>
[Component]
public partial struct BallGlow
{
    public float Intensity;
}

// --- pool bookkeeping (sole writer: MovementSystem) -------------------------------------------------

/// <summary>1 once the ball is pocketed and parked in the tray (excluded from dynamics). One variable.</summary>
[Component]
public partial struct BallSunk
{
    public byte Value;
}

/// <summary>1 while the ball is dropping into a pocket (centered, falling, excluded from table contact)
/// before it reaches <see cref="SinkTargetY"/> and parks. One variable.</summary>
[Component]
public partial struct BallSinking
{
    public byte Value;
}

/// <summary>Y a sinking ball falls to before it parks (pocket bottom). One variable.</summary>
[Component]
public partial struct SinkTargetY
{
    public float Value;
}

/// <summary>Pocket set + tray/respawn spots + cue flag. INLINE-BUFFER / CONFIG BAG — the pocket mouths
/// are an inline buffer authored at spawn (default <see cref="PocketCount"/> 0 = inert: non-pool scenes
/// behave exactly as before).</summary>
[Component]
public partial struct PocketConfig
{
    public const int MaxPockets = 8;

    /// <summary>Pocket mouths as (centerX, centerZ, radiusSquared, unused) — planar capture.</summary>
    public PocketBuffer Pockets;
    public int PocketCount;

    /// <summary>Where this ball rests once sunk (its tray slot).</summary>
    public Vector3 ParkPosition;

    /// <summary>Where the cue ball reappears after a scratch (the head spot).</summary>
    public Vector3 RespawnPosition;

    public byte IsCue;
}

/// <summary>Fixed-capacity inline buffer of pocket definitions (unmanaged, blittable).</summary>
[InlineArray(PocketConfig.MaxPockets)]
public struct PocketBuffer
{
    private Vector4 _element0;
}

// --- sprite flipbook (sole writer: SpriteAnimationSystem) -------------------------------------------

/// <summary>Seconds since spawn, advanced each fixed tick. One variable.</summary>
[Component]
public partial struct SpriteTime
{
    public float Value;
}

/// <summary>Current flipbook frame index, derived from <see cref="SpriteTime"/> and stored so renderers
/// read it straight from the snapshot. One variable.</summary>
[Component]
public partial struct SpriteFrame
{
    public int Value;
}

/// <summary>Flipbook layout. CONFIG BAG — read-only (fps, frame count, loop).</summary>
[Component]
public partial struct SpriteConfig
{
    public float Fps;
    public int FrameCount;
    public byte Loop;

    public SpriteConfig(float fps, int frameCount, bool loop)
    {
        Fps = fps > 0f && float.IsFinite(fps) ? fps : 10f;
        FrameCount = Math.Max(1, frameCount);
        Loop = loop ? (byte)1 : (byte)0;
    }
}

// --- particles (sole writer: ParticleSystem) --------------------------------------------------------

/// <summary>One simulated particle. Lives in WORLD space inside its emitter's inline buffer;
/// <see cref="Lifetime"/> &lt;= 0 marks a free slot (slots are STABLE across ticks).</summary>
public struct Particle
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public float Lifetime;
}

/// <summary>Deterministic emitter RUNTIME STATE — seeded xorshift RNG, spawn accumulator, the inline
/// particle pool. INLINE-BUFFER BAG (the pool must live inline); the whole thing is snapshot-carried so
/// both hosts render identical particles. Sole writer: ParticleSystem.</summary>
[Component]
public partial struct ParticleState
{
    public const int MaxParticles = 64;

    public uint RngState;
    public float SpawnCarry;
    public ParticleBuffer Particles;
}

/// <summary>Emitter config. CONFIG BAG — read-only (rate, lifetime, speed, spread, gravity, drag, capacity).
/// Provides the constructor that also seeds the paired <see cref="ParticleState"/> deterministically.</summary>
[Component]
public partial struct ParticleConfig
{
    public float EmitRate;
    public float LifetimeSeconds;
    public float InitialSpeed;
    public float SpreadRadians;
    public float Gravity;
    public float Drag;
    public int Capacity;

    public ParticleConfig(
        float emitRate, float lifetimeSeconds, float initialSpeed, float spreadRadians,
        float gravity, float drag, int capacity)
    {
        EmitRate = emitRate > 0f && float.IsFinite(emitRate) ? emitRate : 8f;
        LifetimeSeconds = lifetimeSeconds > 0f && float.IsFinite(lifetimeSeconds) ? lifetimeSeconds : 1.5f;
        InitialSpeed = initialSpeed >= 0f && float.IsFinite(initialSpeed) ? initialSpeed : 2f;
        SpreadRadians = float.IsFinite(spreadRadians) ? Math.Clamp(spreadRadians, 0f, MathF.PI) : 0.436f;
        Gravity = float.IsFinite(gravity) ? gravity : -9.8f;
        Drag = drag >= 0f && float.IsFinite(drag) ? drag : 0f;
        Capacity = Math.Clamp(capacity, 1, ParticleState.MaxParticles);
    }

    /// <summary>The runtime-state seed paired with this config (xorshift must never be seeded 0).</summary>
    public static ParticleState SeedState(uint seed) => new() { RngState = seed == 0 ? 1u : seed, SpawnCarry = 0f };
}

/// <summary>Fixed-capacity inline particle pool (unmanaged, blittable — snapshot-complete).</summary>
[InlineArray(ParticleState.MaxParticles)]
public struct ParticleBuffer
{
    private Particle _element0;
}

// --- batch dynamics tuning --------------------------------------------------------------------------

/// <summary>Global dynamics-solver tuning, carried per ball and applied batch-wide from the first
/// simulated ball. CONFIG BAG — read-only, authored in editor project settings.</summary>
[Component]
public partial struct PhysicsTuning
{
    public float MinSpeed;
    public float Skin;
    public float PushStrength;
    public Vector3 Gravity;
    public float StaticFriction;
    public float MinAngularSpeed;

    public PhysicsTuning(float minSpeed, float skin, float pushStrength,
        Vector3 gravity = default, float staticFriction = 0.2f, float minAngularSpeed = 0.05f)
    {
        MinSpeed = minSpeed;
        Skin = skin;
        PushStrength = pushStrength;
        Gravity = gravity == default ? new Vector3(0f, -9.81f, 0f) : gravity;
        StaticFriction = staticFriction;
        MinAngularSpeed = minAngularSpeed;
    }

    public static PhysicsTuning Default => new(0.01f, 0.02f, 1.2f);
}
