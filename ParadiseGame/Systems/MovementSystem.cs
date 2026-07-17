using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Physics;
using ParadiseGame.Physics;

namespace ParadiseGame;

/// <summary>
/// The single owner of every simulated entity's final <see cref="LocalTransform"/>: one generated
/// world system (whole-query segment access, one <see cref="Execute"/> per tick) that runs, in
/// fixed order — (1) navmesh steering per agent (waypoint advance, intent, facing slerp),
/// (2) capsule cast-and-slide + ground containment per agent, (3) the global ball dynamics step
/// (character pushes, ball↔static, ball↔ball) with rolling rotation. Merging steering and
/// integration here means intents are consumed the same tick they are produced, and no other
/// system ever writes a transform (<c>[SingleWriter]</c> enforced).
///
/// Collision comes from the read-only <see cref="PhysicsWorldRef"/> component (an unmanaged
/// borrowed handle — the runner owns the <c>CollisionWorld</c>); an invalid handle means
/// unobstructed planar movement. dt comes from the read-only <see cref="SimulationContext"/>,
/// which under snapshot-read execution is the PREVIOUS tick's value — seed it at spawn.
/// Planar contract: Y is never modified.
/// </summary>
public ref partial struct MovementSystem : IWorldSystem
{
    /// <summary>Clearance kept between the capsule and any surface (meters).</summary>
    public const float Skin = 0.02f;

    private const float MinMoveSq = 1e-10f;

    /// <summary>Body counts up to this use stackalloc scratch; above it, NativeMemory — the
    /// tick never touches the GC heap either way (same idiom as the generated segment tables).</summary>
    private const int MaxStackBodies = 64;

    // Glow tuning: a 2 kg·m/s impulse (cue-strike scale) reads as a full-intensity hit; the
    // rolling decay holds a visible tail (~2 s to 30%), the still decay kills it in ~0.25 s.
    private const float GlowFullImpulse = 2f;
    private const float GlowStillSpeed = 0.05f;
    private const float GlowRollingDecay = 0.99f;
    private const float GlowStillDecay = 0.90f;

    // Structural baseline only (filters, support policy). The tunable scalars — MinSpeed, Skin,
    // PushStrength, StaticRestitution — are overridden every step from authored data
    // (PhysicsTuning + DynamicBody components), never from these defaults.
    private static readonly PlanarDynamicsSettings BallSettings = PlanarDynamicsSettings.Default with
    {
        StaticFilter = PhysicsLayers.DynamicBodyCast,
        RequireSupport = true, // balls stop/slide at slab edges instead of rolling into the void
        SupportFilter = PhysicsLayers.SupportRay,
        SupportProbeDepth = PhysicsLayers.SupportProbeDepth,
    };

    public AgentsSegments Agents;
    public BallsSegments Balls;

    public void Execute()
    {
        for (int i = 0; i < Agents.Length; i++)
        {
            float dt = Agents.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }

            Steer(i, dt);
            Slide(i, dt);
        }

        StepBalls();
    }

    /// <summary>Path following: writes <see cref="MoveIntent"/> and facing; never the position.
    /// Waypoint advance and arrival are measured on the previous tick's physics-resolved position
    /// (steering runs before this agent's slide).</summary>
    private void Steer(int i, float dt)
    {
        ref NavPath path = ref Agents.NavPath[i];
        if (path.HasPath == 0 || path.Count == 0)
        {
            return;
        }

        ref LocalTransform transform = ref Agents.LocalTransform[i];
        ref readonly NavAgent agent = ref Agents.NavAgent[i];
        Vector3 position = transform.Position;
        float arriveSq = agent.ArriveRadius * agent.ArriveRadius;

        // Skip any waypoints already within the arrive radius (handles the path's start corner).
        while (path.Cursor < path.Count && HorizontalDistanceSq(position, path.Waypoints[path.Cursor]) <= arriveSq)
        {
            path.Cursor++;
        }

        if (path.Cursor >= path.Count)
        {
            path.HasPath = 0;
            return;
        }

        Vector3 target = path.Waypoints[path.Cursor];
        Vector3 direction = new(target.X - position.X, 0f, target.Z - position.Z);
        float distance = direction.Length();
        if (distance <= 1e-5f)
        {
            return;
        }

        direction /= distance;
        // Steer toward the waypoint without overshooting it this tick; the slide step below
        // moves the transform.
        float speed = MathF.Min(agent.MoveSpeed, distance / dt);
        Agents.MoveIntent[i].DesiredVelocity = direction * speed;

        // Face the movement direction instantly (cosmetic). Model forward is −Z (right-handed).
        float yaw = MathF.Atan2(-direction.X, -direction.Z);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    /// <summary>Capsule cast-and-slide against static geometry, then ground containment — the
    /// mover never steps off the walkable slab (slides along its edge instead).</summary>
    private void Slide(int i, float dt)
    {
        Vector3 desired = Agents.MoveIntent[i].DesiredVelocity;
        var displacement = new Vector3(desired.X, 0f, desired.Z) * dt;
        if (displacement.LengthSquared() <= MinMoveSq)
        {
            return;
        }

        ref LocalTransform transform = ref Agents.LocalTransform[i];
        Vector3 start = transform.Position;
        CollisionWorldHandle statics = Agents.PhysicsWorldRef[i].Handle;
        Vector3 position;
        if (!statics.IsValid)
        {
            position = start + displacement;
        }
        else
        {
            ref readonly CharacterBody body = ref Agents.CharacterBody[i];
            position = PlanarCapsuleSlide.Move(statics, PhysicsLayers.CharacterCast,
                body.Radius, body.HalfLength, start, displacement, Skin);
            position = PlanarGroundSupport.Clamp(statics, PhysicsLayers.SupportRay,
                start, position, PhysicsLayers.SupportProbeDepth);
        }

        transform.Position = new Vector3(position.X, start.Y, position.Z);
    }

    /// <summary>Gather live (non-sunk) balls + character pushers into unmanaged scratch spans,
    /// run one stateless <see cref="PlanarSphereDynamics"/> step, scatter back with rolling
    /// rotation, then the pocket-capture pass. Global by nature (pairwise collisions) — the
    /// reason this is a world system. Sunk balls are parked and fully excluded.</summary>
    private unsafe void StepBalls()
    {
        int ballCount = Balls.Length;
        if (ballCount == 0)
        {
            return;
        }

        // dt and the collision handle are read from ball 0 and applied batch-wide: every entity
        // is seeded from the same runner-owned CollisionWorld and the same fixed timestep. A
        // future per-entity world/timestep would need per-ball plumbing here.
        float dt = Balls.SimulationContext[0].DeltaSeconds;
        if (dt <= 0f)
        {
            return;
        }

        int pusherCount = Agents.Length;
        DynamicSphere* sphereAlloc = null;
        KinematicCapsule* pusherAlloc = null;
        int* mapAlloc = null;
        try
        {
            Span<DynamicSphere> sphereScratch = (ballCount <= MaxStackBodies
                ? stackalloc DynamicSphere[MaxStackBodies]
                : new Span<DynamicSphere>(
                    sphereAlloc = (DynamicSphere*)NativeMemory.Alloc((nuint)ballCount, (nuint)sizeof(DynamicSphere)),
                    ballCount))[..ballCount];
            // map[k] = ball index of gathered sphere k (sunk balls are skipped at gather).
            Span<int> mapScratch = (ballCount <= MaxStackBodies
                ? stackalloc int[MaxStackBodies]
                : new Span<int>(
                    mapAlloc = (int*)NativeMemory.Alloc((nuint)ballCount, sizeof(int)),
                    ballCount))[..ballCount];
            Span<KinematicCapsule> pushers = (pusherCount <= MaxStackBodies
                ? stackalloc KinematicCapsule[MaxStackBodies]
                : new Span<KinematicCapsule>(
                    pusherAlloc = (KinematicCapsule*)NativeMemory.Alloc((nuint)pusherCount, (nuint)sizeof(KinematicCapsule)),
                    pusherCount))[..pusherCount];

            int liveCount = 0;
            for (int i = 0; i < ballCount; i++)
            {
                if (Balls.PoolBall[i].Sunk != 0)
                {
                    continue;
                }
                ref readonly DynamicBody body = ref Balls.DynamicBody[i];
                sphereScratch[liveCount] = new DynamicSphere
                {
                    Position = Balls.LocalTransform[i].Position,
                    Velocity = body.Velocity,
                    Radius = body.Radius,
                    Mass = body.Mass,
                    LinearDamping = body.LinearDamping,
                    Restitution = body.Restitution,
                    SpinY = body.SpinY,
                };
                mapScratch[liveCount] = i;
                liveCount++;
            }
            if (liveCount == 0)
            {
                return;
            }
            Span<DynamicSphere> spheres = sphereScratch[..liveCount];
            Span<int> map = mapScratch[..liveCount];

            // Pushers use this tick's post-slide positions and intents (agents ran above).
            for (int p = 0; p < pusherCount; p++)
            {
                pushers[p] = new KinematicCapsule
                {
                    Position = Agents.LocalTransform[p].Position,
                    Velocity = Agents.MoveIntent[p].DesiredVelocity,
                    Radius = Agents.CharacterBody[p].Radius,
                    HalfLength = Agents.CharacterBody[p].HalfLength,
                };
            }

            CollisionWorldHandle statics = Balls.PhysicsWorldRef[map[0]].Handle;
            // Scene-global solver tuning (authored project settings) and the static (cushion)
            // bounce are carried batch-wide by the first live ball — the same idiom as dt and
            // the collision handle above.
            ref readonly PhysicsTuning tuning = ref Balls.PhysicsTuning[map[0]];
            PlanarDynamicsSettings settings = BallSettings with
            {
                MinSpeed = tuning.MinSpeed,
                Skin = tuning.Skin,
                PushStrength = tuning.PushStrength,
                RailEnglish = tuning.RailEnglish,
                RailSpinLoss = tuning.RailSpinLoss,
                StaticRestitution = Balls.DynamicBody[map[0]].StaticRestitution,
            };
            PlanarSphereDynamics.Step(spheres, pushers, statics, settings, dt);

            for (int k = 0; k < liveCount; k++)
            {
                ref readonly DynamicSphere sphere = ref spheres[k];
                int i = map[k];
                ref LocalTransform transform = ref Balls.LocalTransform[i];
                Vector3 old = transform.Position;
                transform.Position = new Vector3(sphere.Position.X, old.Y, sphere.Position.Z);
                Balls.DynamicBody[i].Velocity = sphere.Velocity;
                // Persist the spin the solver bled at any cushion contact this step (stateless
                // engine: SpinY is owned here, round-tripped through the DynamicSphere span).
                Balls.DynamicBody[i].SpinY = sphere.SpinY;

                // Collision glow: spike with the pairwise contact impulse (normalized by an
                // impulse that reads as a "solid hit"), then decay — slowly while the ball still
                // rolls, quickly once it has effectively stopped, so lights die with the motion.
                ref BallGlow glow = ref Balls.BallGlow[i];
                float ballSpeed = MathF.Sqrt(sphere.Velocity.X * sphere.Velocity.X + sphere.Velocity.Z * sphere.Velocity.Z);
                float spike = MathF.Min(1f, sphere.ContactImpulse / GlowFullImpulse);
                float decay = ballSpeed > GlowStillSpeed ? GlowRollingDecay : GlowStillDecay;
                glow.Intensity = MathF.Max(glow.Intensity * decay, spike);
                if (glow.Intensity < 1e-3f)
                {
                    glow.Intensity = 0f;
                }

                // Rolling visual: for rolling-without-slipping on a Y-up plane, ω = (Up × v) / r.
                // Cosmetic and game-side (the engine library stays transcendental-free); rotation
                // lives in LocalTransform so the renderer's existing Slerp interpolates it.
                float speed = ballSpeed;
                if (speed > 1e-4f && sphere.Radius > 1e-4f)
                {
                    Vector3 axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, sphere.Velocity));
                    Quaternion delta = Quaternion.CreateFromAxisAngle(axis, speed * dt / sphere.Radius);
                    transform.Rotation = Quaternion.Normalize(
                        Quaternion.Concatenate(transform.Rotation, delta));
                }

                CaptureInPocket(i);
            }
        }
        finally
        {
            if (sphereAlloc != null) NativeMemory.Free(sphereAlloc);
            if (pusherAlloc != null) NativeMemory.Free(pusherAlloc);
            if (mapAlloc != null) NativeMemory.Free(mapAlloc);
        }
    }

    /// <summary>Pocket capture for one live ball: when its center enters a pocket mouth (planar
    /// XZ check), an object ball sinks — parked at its tray slot, velocity and glow killed,
    /// excluded from future steps — while the cue ball scratches: instant respawn at the head
    /// spot, never marked sunk. Y is untouched (planar contract). Rewind resurrects: Sunk is
    /// recorded per tick and restored with the transform.</summary>
    private void CaptureInPocket(int i)
    {
        ref PoolBall pool = ref Balls.PoolBall[i];
        if (pool.PocketCount == 0)
        {
            return;
        }

        ref LocalTransform transform = ref Balls.LocalTransform[i];
        Vector3 position = transform.Position;
        for (int p = 0; p < pool.PocketCount; p++)
        {
            Vector4 pocket = pool.Pockets[p];
            float dx = position.X - pocket.X;
            float dz = position.Z - pocket.Y;
            if (dx * dx + dz * dz >= pocket.Z)
            {
                continue;
            }

            Vector3 target = pool.IsCue != 0 ? pool.RespawnPosition : pool.ParkPosition;
            transform.Position = new Vector3(target.X, position.Y, target.Z);
            Balls.DynamicBody[i].Velocity = Vector3.Zero;
            Balls.BallGlow[i].Intensity = 0f;
            if (pool.IsCue == 0)
            {
                pool.Sunk = 1;
            }
            return;
        }
    }

    private static float HorizontalDistanceSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
