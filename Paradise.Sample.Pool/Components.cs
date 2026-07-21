using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paradise.Sample.Pool;

/// <summary>
/// Shared per-frame simulation data, injected into systems as a component (Paradise.ECS injects
/// component instances, not arbitrary objects — so shared state is modelled as a component the
/// systems read). Carried by every simulated agent; <see cref="GameSimulation.Tick"/> refreshes it
/// once per tick before the schedule runs. Extend with elapsed time / frame count as needed.
/// </summary>
[Component]
public partial struct SimulationContext
{
    public float DeltaSeconds;
}

/// <summary>World-space transform in right-handed (Y-up, −Z forward) coordinates.</summary>
[Component]
public partial struct LocalTransform
{
    public Vector3 Position;
    public Quaternion Rotation;

    public LocalTransform(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }
}

/// <summary>Steering parameters for a navmesh-following agent (metres/sec, metres). Facing is
/// instant — the agent snaps to its movement direction, no angular speed.</summary>
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

/// <summary>
/// A navmesh path the agent is following: an inline waypoint buffer (unmanaged, so it lives inline in
/// the component chunk) plus a cursor. Filled by <see cref="Navigation.NavigationPlanner"/> and
/// consumed by <see cref="MovementSystem"/>.
/// </summary>
[Component]
public partial struct NavPath
{
    public const int MaxWaypoints = 32;

    public WaypointBuffer Waypoints;
    public int Count;
    public int Cursor;
    public byte HasPath;
}

/// <summary>Fixed-capacity inline buffer of waypoints (C# 12 InlineArray — unmanaged, blittable).</summary>
[InlineArray(NavPath.MaxWaypoints)]
public struct WaypointBuffer
{
    private Vector3 _element0;
}

/// <summary>
/// This tick's desired velocity (m/s, horizontal), produced by steering (path following or direct
/// input) and consumed by <see cref="MovementSystem"/>. Zeroed at the start of
/// every tick so stale intent never leaks across frames.
/// </summary>
[Component]
public partial struct MoveIntent
{
    public Vector3 DesiredVelocity;
}

/// <summary>
/// Collision capsule of a movable character (Y-aligned, origin at the capsule CENTER — matching
/// scene authoring). <see cref="HalfLength"/> is the core segment half length: total capsule height
/// = 2 * (HalfLength + Radius). All character physics state lives in components; the collision
/// world holds only immutable static geometry.
/// </summary>
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

/// <summary>
/// Unmanaged handle to the session's static <c>Paradise.Physics.CollisionWorld</c>, carried as
/// a component (the <see cref="SimulationContext"/> pattern — Paradise.ECS has no singleton
/// store) so the generated <see cref="MovementSystem"/> can run collision queries without a
/// managed service. Borrowed: valid while the runner-owned CollisionWorld lives (the whole
/// session); <c>default</c> = no collision world (unobstructed movement).
/// </summary>
[Component]
public partial struct PhysicsWorldRef
{
    public Paradise.Physics.CollisionWorldHandle Handle;
}

/// <summary>
/// A dynamic physics body (sphere-only in this phase). ALL of its physics state lives here —
/// the stateless resolver (<c>Paradise.Physics.RigidSphereDynamics</c>) reads and writes
/// components each tick, so snapshots stay complete. Position is the sphere center
/// (<see cref="LocalTransform"/>) in full 3D — gravity, resting on the felt, and jumps all
/// move Y now.
/// </summary>
/// <summary>Collision feedback for a dynamic ball: <see cref="Intensity"/> spikes to 1 on a
/// ball↔ball hit (scaled by contact impulse) and decays each tick — fast once the ball is
/// still, slow while it rolls. The renderer maps it onto the ball's point light.</summary>
[Component]
public partial struct BallGlow
{
    public float Intensity;
}

[Component]
public partial struct DynamicBody
{
    public Vector3 Velocity;

    /// <summary>Full 3D angular velocity (rad/s). Sidespin ("english") is the Y component; a
    /// horizontal-axis component is top/back-spin (follow/draw). The stateless solver couples it
    /// to linear motion through Coulomb friction at contacts — draw, follow, throw, rolling and
    /// english all emerge. The renderer/game integrates orientation from this each tick.</summary>
    public Vector3 AngularVelocity;

    public float Radius;
    public float Mass;

    /// <summary>Per-second linear damping (felt roll); fed into the per-sphere dynamics.</summary>
    public float LinearDamping;

    /// <summary>Per-second angular damping (spin/rolling resistance of the cloth).</summary>
    public float AngularDamping;

    /// <summary>Ball ↔ ball bounce factor; pairs bounce with the average of both balls' values.</summary>
    public float Restitution;

    /// <summary>Ball ↔ static bounce factor. Batch-wide like dt: the step uses the first
    /// simulated ball's value (one cushion surface type per scene).</summary>
    public float StaticRestitution;

    /// <summary>Coulomb friction coefficient μ for this ball's contacts (the only spin↔linear
    /// coupling). Authored (BodyFriction); default 0.3.</summary>
    public float Friction;

    public DynamicBody(float radius, float mass,
        float linearDamping = 1.5f, float restitution = 0.6f, float staticRestitution = 0.4f,
        float friction = 0.3f, float angularDamping = 0.4f)
    {
        Velocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        Radius = radius;
        Mass = mass;
        LinearDamping = linearDamping;
        AngularDamping = angularDamping;
        Restitution = restitution;
        StaticRestitution = staticRestitution;
        Friction = friction;
    }
}

/// <summary>
/// Pool-game state of a dynamic ball: the pocket set it can fall into (inline, unmanaged — the
/// whole config lives in the snapshot) and its sunk/park bookkeeping. Default value = inert
/// (<see cref="PocketCount"/> 0): non-pool scenes and tests behave exactly as before.
/// A sunk ball keeps its entity (rewind can resurrect it) but is parked at
/// <see cref="ParkPosition"/> and excluded from dynamics, aiming, and glow.
/// </summary>
[Component]
public partial struct PoolBall
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
    public byte Sunk;

    /// <summary>1 while the ball is dropping into a pocket (centered over the mouth, falling under
    /// gravity, excluded from table contact) — before it reaches <see cref="SinkTargetY"/> and is
    /// parked/marked Sunk. Gives a real visible fall rather than an instant teleport.</summary>
    public byte Sinking;

    /// <summary>Y the sinking ball falls to before it parks (the pocket bottom).</summary>
    public float SinkTargetY;
}

/// <summary>Fixed-capacity inline buffer of pocket definitions (unmanaged, blittable).</summary>
[InlineArray(PoolBall.MaxPockets)]
public struct PocketBuffer
{
    private Vector4 _element0;
}

/// <summary>
/// Flipbook 2D-animation clock. The SIMULATION owns sprite time so both hosts (Godot and the
/// .NET renderer) read the same <see cref="Frame"/> out of the world snapshot — the sprite
/// equivalent of ball transforms. <see cref="SpriteAnimationSystem"/> is the sole writer.
/// Sheet layout / quad geometry are presentation data and stay in the export contract.
/// </summary>
[Component]
public partial struct SpriteAnimation
{
    /// <summary>Frames per second (&gt; 0).</summary>
    public float Fps;

    /// <summary>Total flipbook frames (&gt;= 1).</summary>
    public int FrameCount;

    /// <summary>1 = wrap around; 0 = hold the last frame.</summary>
    public byte Loop;

    /// <summary>Seconds since spawn (advanced each fixed tick).</summary>
    public float Time;

    /// <summary>Current frame index — derived from <see cref="Time"/>, stored so renderers
    /// read it straight from the snapshot without duplicating the sampling rule.</summary>
    public int Frame;

    public SpriteAnimation(float fps, int frameCount, bool loop)
    {
        Fps = fps > 0f && float.IsFinite(fps) ? fps : 10f;
        FrameCount = Math.Max(1, frameCount);
        Loop = loop ? (byte)1 : (byte)0;
        Time = 0f;
        Frame = 0;
    }
}

/// <summary>One simulated particle. Lives in WORLD space inside its emitter's inline buffer;
/// <see cref="Lifetime"/> &lt;= 0 marks a free slot (slots are STABLE across ticks — a live
/// particle never moves buffers, so renderers can interpolate slot-wise between snapshots).</summary>
public struct Particle
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public float Lifetime;
}

/// <summary>
/// A deterministic CPU particle emitter: config + ALL runtime state (seeded xorshift RNG,
/// spawn accumulator, the inline particle pool), so world snapshots carry complete particle
/// state and both hosts render identical particles. <see cref="ParticleSystem"/> is the sole
/// writer. Emission is a cone of <see cref="SpreadRadians"/> half-angle around the entity's
/// +Y axis; particles integrate gravity + drag in world space. Render kind (sprite quad vs
/// voxel cube), sheet and tint are presentation data and stay in the export contract.
/// </summary>
[Component]
public partial struct ParticleEmitter
{
    public const int MaxParticles = 64;

    // -- authored config --
    public float EmitRate;
    public float LifetimeSeconds;
    public float InitialSpeed;
    public float SpreadRadians;
    public float Gravity;
    public float Drag;
    public int Capacity;

    // -- runtime state --
    public uint RngState;
    public float SpawnCarry;
    public ParticleBuffer Particles;

    public ParticleEmitter(
        float emitRate, float lifetimeSeconds, float initialSpeed, float spreadRadians,
        float gravity, float drag, int capacity, uint seed)
    {
        EmitRate = emitRate > 0f && float.IsFinite(emitRate) ? emitRate : 8f;
        LifetimeSeconds = lifetimeSeconds > 0f && float.IsFinite(lifetimeSeconds) ? lifetimeSeconds : 1.5f;
        InitialSpeed = initialSpeed >= 0f && float.IsFinite(initialSpeed) ? initialSpeed : 2f;
        SpreadRadians = float.IsFinite(spreadRadians) ? Math.Clamp(spreadRadians, 0f, MathF.PI) : 0.436f;
        Gravity = float.IsFinite(gravity) ? gravity : -9.8f;
        Drag = drag >= 0f && float.IsFinite(drag) ? drag : 0f;
        Capacity = Math.Clamp(capacity, 1, MaxParticles);
        RngState = seed == 0 ? 1u : seed; // xorshift must never be seeded 0 (fixed point)
        SpawnCarry = 0f;
    }
}

/// <summary>Fixed-capacity inline particle pool (unmanaged, blittable — snapshot-complete).</summary>
[InlineArray(ParticleEmitter.MaxParticles)]
public struct ParticleBuffer
{
    private Particle _element0;
}

/// <summary>
/// Global dynamics-solver tuning for ball physics, carried per ball like
/// <see cref="SimulationContext"/> (Paradise.ECS has no singleton store) and applied batch-wide
/// from the first simulated ball. Authored in editor project settings
/// (data/ProjectSettings.json → Physics.Dynamics); <see cref="Default"/> mirrors the contract
/// defaults so spawns without scene data behave identically.
/// </summary>
[Component]
public partial struct PhysicsTuning
{
    /// <summary>Linear speeds below this settle to rest when supported (m/s).</summary>
    public float MinSpeed;

    /// <summary>Clearance kept between balls and static surfaces (meters).</summary>
    public float Skin;

    /// <summary>Scale applied to a character pusher's velocity when injected into a ball.</summary>
    public float PushStrength;

    /// <summary>Gravity acceleration (m/s²) applied to every ball each step (points −Y).</summary>
    public Vector3 Gravity;

    /// <summary>Coulomb friction coefficient for ball ↔ static (cushion/cloth) contacts.</summary>
    public float StaticFriction;

    /// <summary>Angular speeds below this settle to rest when supported (rad/s).</summary>
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
