using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Physics;
using Paradise.Sample.Pool.Physics;

namespace Paradise.Sample.Pool;

/// <summary>
/// The single owner of every simulated entity's final <see cref="Position"/>/<see cref="Rotation"/>:
/// one generated world system (whole-query segment access, one <see cref="Execute"/> per tick) that
/// runs, in fixed order — (1) navmesh steering per agent (waypoint advance, intent, facing),
/// (2) capsule cast-and-slide + ground containment per agent, (3) the global ball dynamics step
/// (character pushes, ball↔static, ball↔ball) with rolling rotation. Merging steering and integration
/// here means intents are consumed the same tick they are produced, and no other system ever writes a
/// transform (<c>[SingleWriter]</c> enforced).
///
/// Under snapshot-read execution the read-only fields it reads (dt, physics handle, agent/ball config)
/// are the PREVIOUS tick's values — seed them at spawn. Characters stay planar (Y locked); BALLS are
/// full 3D (gravity/jumps move their Y).
/// </summary>
public ref partial struct MovementSystem : IWorldSystem
{
    /// <summary>Clearance kept between the capsule and any surface (meters).</summary>
    public const float Skin = 0.02f;

    private const float MinMoveSq = 1e-10f;

    private const int MaxStackBodies = 64;

    private const float GlowFullImpulse = 2f;
    private const float GlowStillSpeed = 0.05f;
    private const float GlowRollingDecay = 0.99f;
    private const float GlowStillDecay = 0.90f;

    /// <summary>How far a pocketed ball falls (m) before it parks in the tray — the visible drop.</summary>
    private const float PocketDropDepth = 0.7f;

    private static readonly SphereDynamicsSettings BallSettings = SphereDynamicsSettings.Default with
    {
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

    /// <summary>Path following: writes <see cref="MoveIntent"/> and facing; never the position.</summary>
    private void Steer(int i, float dt)
    {
        if (Agents.HasPath[i].Value == 0 || Agents.NavWaypoints[i].Count == 0)
        {
            return;
        }

        ref readonly NavWaypoints waypoints = ref Agents.NavWaypoints[i];
        ref int cursor = ref Agents.NavCursor[i].Value;
        ref readonly NavAgent agent = ref Agents.NavAgent[i];
        Vector3 position = Agents.Position[i].Value;
        float arriveSq = agent.ArriveRadius * agent.ArriveRadius;

        // Skip any waypoints already within the arrive radius (handles the path's start corner).
        while (cursor < waypoints.Count && HorizontalDistanceSq(position, waypoints.Waypoints[cursor]) <= arriveSq)
        {
            cursor++;
        }

        if (cursor >= waypoints.Count)
        {
            Agents.HasPath[i].Value = 0;
            return;
        }

        Vector3 target = waypoints.Waypoints[cursor];
        Vector3 direction = new(target.X - position.X, 0f, target.Z - position.Z);
        float distance = direction.Length();
        if (distance <= 1e-5f)
        {
            return;
        }

        direction /= distance;
        // Steer toward the waypoint without overshooting it this tick; the slide step below moves it.
        float speed = MathF.Min(agent.MoveSpeed, distance / dt);
        Agents.MoveIntent[i].DesiredVelocity = direction * speed;

        // Face the movement direction instantly (cosmetic). Model forward is −Z (right-handed).
        float yaw = MathF.Atan2(-direction.X, -direction.Z);
        Agents.Rotation[i].Value = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    /// <summary>Capsule cast-and-slide against static geometry, then ground containment.</summary>
    private void Slide(int i, float dt)
    {
        Vector3 desired = Agents.MoveIntent[i].DesiredVelocity;
        var displacement = new Vector3(desired.X, 0f, desired.Z) * dt;
        if (displacement.LengthSquared() <= MinMoveSq)
        {
            return;
        }

        ref Vector3 transform = ref Agents.Position[i].Value;
        Vector3 start = transform;
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

        transform = new Vector3(position.X, start.Y, position.Z);
    }

    /// <summary>Gather live (non-sunk) balls + character pushers into unmanaged scratch spans, run one
    /// stateless <see cref="RigidSphereDynamics"/> step, scatter back with rolling rotation, then the
    /// pocket-capture pass. Global by nature (pairwise collisions) — the reason this is a world system.</summary>
    private unsafe void StepBalls()
    {
        int ballCount = Balls.Length;
        if (ballCount == 0)
        {
            return;
        }

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
                if (Balls.BallSunk[i].Value != 0 || Balls.BallSinking[i].Value != 0)
                {
                    continue;
                }
                ref readonly BallPhysicsConfig cfg = ref Balls.BallPhysicsConfig[i];
                sphereScratch[liveCount] = new DynamicSphere
                {
                    Position = Balls.Position[i].Value,
                    Velocity = Balls.Velocity[i].Value,
                    AngularVelocity = Balls.AngularVelocity[i].Value,
                    Radius = cfg.Radius,
                    Mass = cfg.Mass,
                    LinearDamping = cfg.LinearDamping,
                    AngularDamping = cfg.AngularDamping,
                    Restitution = cfg.Restitution,
                    Friction = cfg.Friction,
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
                    Position = Agents.Position[p].Value,
                    Velocity = Agents.MoveIntent[p].DesiredVelocity,
                    Radius = Agents.CharacterBody[p].Radius,
                    HalfLength = Agents.CharacterBody[p].HalfLength,
                };
            }

            CollisionWorldHandle statics = Balls.PhysicsWorldRef[map[0]].Handle;
            ref readonly PhysicsTuning tuning = ref Balls.PhysicsTuning[map[0]];
            SphereDynamicsSettings settings = BallSettings with
            {
                Gravity = tuning.Gravity,
                MinSpeed = tuning.MinSpeed,
                MinAngularSpeed = tuning.MinAngularSpeed,
                Skin = tuning.Skin,
                PushStrength = tuning.PushStrength,
                StaticFriction = tuning.StaticFriction,
                StaticRestitution = Balls.BallPhysicsConfig[map[0]].StaticRestitution,
            };
            RigidSphereDynamics.Step(spheres, pushers, statics, settings, dt);

            for (int k = 0; k < liveCount; k++)
            {
                ref readonly DynamicSphere sphere = ref spheres[k];
                int i = map[k];
                // Full 3D now: the solver owns Y too (gravity, rest-on-felt, jumps).
                Balls.Position[i].Value = sphere.Position;
                Balls.Velocity[i].Value = sphere.Velocity;
                Balls.AngularVelocity[i].Value = sphere.AngularVelocity;

                // Collision glow: spike with the pairwise contact impulse, then decay — slowly while
                // rolling, quickly once stopped, so lights die with the motion.
                ref float glow = ref Balls.BallGlow[i].Intensity;
                float ballSpeed = MathF.Sqrt(sphere.Velocity.X * sphere.Velocity.X + sphere.Velocity.Z * sphere.Velocity.Z);
                float spike = MathF.Min(1f, sphere.ContactImpulse / GlowFullImpulse);
                float decay = ballSpeed > GlowStillSpeed ? GlowRollingDecay : GlowStillDecay;
                glow = MathF.Max(glow * decay, spike);
                if (glow < 1e-3f)
                {
                    glow = 0f;
                }

                // Integrate orientation from the REAL angular velocity the solver produced (world frame).
                ref Quaternion rotation = ref Balls.Rotation[i].Value;
                Vector3 w = sphere.AngularVelocity;
                float wLen = w.Length();
                if (wLen > 1e-5f)
                {
                    Quaternion delta = Quaternion.CreateFromAxisAngle(w / wLen, wLen * dt);
                    rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, delta));
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

    /// <summary>Pocket capture for one live ball: when its center enters a pocket mouth (planar XZ
    /// check), an object ball begins SINKING while the cue ball scratches (instant respawn).</summary>
    private void CaptureInPocket(int i)
    {
        ref readonly PocketConfig pocket = ref Balls.PocketConfig[i];
        if (pocket.PocketCount == 0 || Balls.BallSinking[i].Value != 0)
        {
            return;
        }

        ref Vector3 pos = ref Balls.Position[i].Value;
        Vector3 position = pos;
        for (int p = 0; p < pocket.PocketCount; p++)
        {
            Vector4 mouth = pocket.Pockets[p];
            float dx = position.X - mouth.X;
            float dz = position.Z - mouth.Y;
            if (dx * dx + dz * dz >= mouth.Z)
            {
                continue;
            }

            Balls.BallGlow[i].Intensity = 0f;
            if (pocket.IsCue != 0)
            {
                // Scratch: instant respawn at the head spot.
                pos = new Vector3(pocket.RespawnPosition.X, position.Y, pocket.RespawnPosition.Z);
                Balls.Velocity[i].Value = Vector3.Zero;
                Balls.AngularVelocity[i].Value = Vector3.Zero;
                return;
            }

            // Object ball: begin the fall. Center over the mouth, drop under gravity until SinkTargetY.
            Balls.BallSinking[i].Value = 1;
            Balls.SinkTargetY[i].Value = position.Y - PocketDropDepth;
            pos = new Vector3(mouth.X, position.Y, mouth.Y);
            ref Vector3 vel = ref Balls.Velocity[i].Value;
            vel = new Vector3(0f, MathF.Min(vel.Y, -0.5f), 0f);
            return;
        }
    }

    /// <summary>Advance every ball currently dropping into a pocket: gravity + spin, then park it in the
    /// tray and mark it Sunk once it passes <see cref="SinkTargetY"/>. Runs every tick.</summary>
    private void DropSinkingBalls(int ballCount, float dt)
    {
        for (int i = 0; i < ballCount; i++)
        {
            if (Balls.BallSinking[i].Value == 0)
            {
                continue;
            }

            ref Vector3 pos = ref Balls.Position[i].Value;
            ref Quaternion rotation = ref Balls.Rotation[i].Value;
            ref Vector3 vel = ref Balls.Velocity[i].Value;
            vel.Y += Balls.PhysicsTuning[i].Gravity.Y * dt;
            Vector3 position = pos;
            position.Y += vel.Y * dt;
            pos = position;

            Vector3 w = Balls.AngularVelocity[i].Value;
            float wLen = w.Length();
            if (wLen > 1e-5f)
            {
                rotation = Quaternion.Normalize(Quaternion.Concatenate(
                    rotation, Quaternion.CreateFromAxisAngle(w / wLen, wLen * dt)));
            }

            if (position.Y <= Balls.SinkTargetY[i].Value)
            {
                pos = Balls.PocketConfig[i].ParkPosition;
                vel = Vector3.Zero;
                Balls.AngularVelocity[i].Value = Vector3.Zero;
                Balls.BallSinking[i].Value = 0;
                Balls.BallSunk[i].Value = 1;
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
