using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Physics;
using Paradise.Sample.Game.Physics;

namespace Paradise.Sample.Game;

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
/// unobstructed movement. dt comes from the read-only <see cref="SimulationContext"/>,
/// which under snapshot-read execution is the PREVIOUS tick's value — seed it at spawn.
/// Characters stay planar (Y locked); BALLS are full 3D (gravity/jumps move their Y).
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

    /// <summary>How far a pocketed ball falls (m) before it parks in the tray — the visible drop.</summary>
    private const float PocketDropDepth = 0.7f;

    // Structural baseline only (filters, support policy). The tunable scalars — MinSpeed, Skin,
    // PushStrength, StaticRestitution — are overridden every step from authored data
    // (PhysicsTuning + DynamicBody components), never from these defaults.
    private static readonly SphereDynamicsSettings BallSettings = SphereDynamicsSettings.Default with
    {
        // 3D contacts vs BOTH floor (gravity rests balls on it) and obstacles (cushions).
        StaticFilter = PhysicsLayers.BallContact,
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
    /// run one stateless <see cref="RigidSphereDynamics"/> step, scatter back with rolling
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

        DropSinkingBalls(ballCount, dt); // fall pockets forward even when no ball is live this tick

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
                // Sunk balls are frozen; sinking balls fall free of table contact (handled below).
                if (Balls.PoolBall[i].Sunk != 0 || Balls.PoolBall[i].Sinking != 0)
                {
                    continue;
                }
                ref readonly DynamicBody body = ref Balls.DynamicBody[i];
                sphereScratch[liveCount] = new DynamicSphere
                {
                    Position = Balls.LocalTransform[i].Position,
                    Velocity = body.Velocity,
                    AngularVelocity = body.AngularVelocity,
                    Radius = body.Radius,
                    Mass = body.Mass,
                    LinearDamping = body.LinearDamping,
                    AngularDamping = body.AngularDamping,
                    Restitution = body.Restitution,
                    Friction = body.Friction,
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
            SphereDynamicsSettings settings = BallSettings with
            {
                Gravity = tuning.Gravity,
                MinSpeed = tuning.MinSpeed,
                MinAngularSpeed = tuning.MinAngularSpeed,
                Skin = tuning.Skin,
                PushStrength = tuning.PushStrength,
                StaticFriction = tuning.StaticFriction,
                StaticRestitution = Balls.DynamicBody[map[0]].StaticRestitution,
            };
            RigidSphereDynamics.Step(spheres, pushers, statics, settings, dt);

            for (int k = 0; k < liveCount; k++)
            {
                ref readonly DynamicSphere sphere = ref spheres[k];
                int i = map[k];
                ref LocalTransform transform = ref Balls.LocalTransform[i];
                // Full 3D now: the solver owns Y too (gravity, rest-on-felt, jumps).
                transform.Position = sphere.Position;
                Balls.DynamicBody[i].Velocity = sphere.Velocity;
                Balls.DynamicBody[i].AngularVelocity = sphere.AngularVelocity;

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

                // Integrate orientation from the REAL angular velocity the solver produced (world
                // frame): q ← normalize(Δ · q). Replaces the old cosmetic ω=(Up×v)/r — rolling now
                // emerges from friction, and this shows draw/follow/side spin honestly.
                Vector3 w = sphere.AngularVelocity;
                float wLen = w.Length();
                if (wLen > 1e-5f)
                {
                    Quaternion delta = Quaternion.CreateFromAxisAngle(w / wLen, wLen * dt);
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
    /// XZ check), an object ball begins SINKING — centered over the mouth, then dropped under
    /// gravity by <see cref="DropSinkingBalls"/> into the tray — while the cue ball scratches:
    /// instant respawn at the head spot, never marked sunk. Rewind resurrects: Sunk is recorded
    /// per tick and restored with the transform.</summary>
    private void CaptureInPocket(int i)
    {
        ref PoolBall pool = ref Balls.PoolBall[i];
        if (pool.PocketCount == 0 || pool.Sinking != 0)
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

            Balls.BallGlow[i].Intensity = 0f;
            if (pool.IsCue != 0)
            {
                // Scratch: instant respawn at the head spot.
                transform.Position = new Vector3(pool.RespawnPosition.X, position.Y, pool.RespawnPosition.Z);
                Balls.DynamicBody[i].Velocity = Vector3.Zero;
                Balls.DynamicBody[i].AngularVelocity = Vector3.Zero;
                return;
            }

            // Object ball: begin the fall. Center over the mouth, drop under gravity (excluded
            // from table contact from next tick) until SinkTargetY, keeping spin for the visual.
            pool.Sinking = 1;
            pool.SinkTargetY = position.Y - PocketDropDepth;
            transform.Position = new Vector3(pocket.X, position.Y, pocket.Y);
            ref DynamicBody body = ref Balls.DynamicBody[i];
            body.Velocity = new Vector3(0f, MathF.Min(body.Velocity.Y, -0.5f), 0f);
            return;
        }
    }

    /// <summary>Advance every ball currently dropping into a pocket: gravity + spin, then park it
    /// in the tray and mark it Sunk once it passes <see cref="PoolBall.SinkTargetY"/>. Runs every
    /// tick (even when no ball is live) so a pocketing finishes.</summary>
    private void DropSinkingBalls(int ballCount, float dt)
    {
        for (int i = 0; i < ballCount; i++)
        {
            ref PoolBall pool = ref Balls.PoolBall[i];
            if (pool.Sinking == 0)
            {
                continue;
            }

            ref LocalTransform transform = ref Balls.LocalTransform[i];
            ref DynamicBody body = ref Balls.DynamicBody[i];
            body.Velocity.Y += Balls.PhysicsTuning[i].Gravity.Y * dt;
            Vector3 position = transform.Position;
            position.Y += body.Velocity.Y * dt;
            transform.Position = position;

            Vector3 w = body.AngularVelocity;
            float wLen = w.Length();
            if (wLen > 1e-5f)
            {
                transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(
                    transform.Rotation, Quaternion.CreateFromAxisAngle(w / wLen, wLen * dt)));
            }

            if (position.Y <= pool.SinkTargetY)
            {
                transform.Position = pool.ParkPosition;
                body.Velocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                pool.Sinking = 0;
                pool.Sunk = 1;
            }
        }
    }

    private static float HorizontalDistanceSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
