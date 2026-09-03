using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Physics;
using Paradise.Sample.Pool.Physics;

namespace Paradise.Sample.Pool;

/// <summary>
/// The single owner of every simulated entity's final <see cref="Position"/>/<see cref="Rotation"/>:
/// one generated world system (whole-query segment access, one <see cref="Execute"/> per tick) that
/// runs the global ball dynamics step (ball↔static, ball↔ball) with rolling rotation. No other system
/// ever writes a transform (<c>[SingleWriter]</c> enforced).
///
/// Under snapshot-read execution the read-only fields it reads (dt, physics handle, ball config)
/// are the PREVIOUS tick's values — seed them at spawn. BALLS are full 3D (gravity moves their Y).
/// </summary>
public ref partial struct MovementSystem : IWorldSystem
{
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

    public Balls.Segments Balls;

    /// <summary>Deferred <c>SystemEvents</c> writer (engine 0.5.2): a <see cref="BallPocketed"/> is
    /// appended the tick a ball drops (scratch OR sink) for next-frame fan-out. The owner-reactor
    /// <see cref="ScoreSystem"/> is the sole consumer — this system never touches <see cref="Score"/>,
    /// so cross-entity scoring stays clear of per-entity single-writer ownership. See GameEvents.cs.</summary>
    public SystemEventWriter Events;

    public void Execute()
    {
        StepBalls();
    }

    /// <summary>Gather live (non-sunk) balls into an unmanaged scratch span, run one stateless
    /// <see cref="RigidSphereDynamics"/> step, scatter back with rolling rotation, then the
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

        DynamicSphere* sphereAlloc = null;
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
            RigidSphereDynamics.Step(spheres, ReadOnlySpan<KinematicCapsule>.Empty, statics, settings, dt);

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
                // Announce the drop for next-frame reactors (cue = scratch → score penalty).
                Events.Append(new BallPocketed { BallId = Balls.BallId[i].Value, IsCue = pocket.IsCue });
                return;
            }

            // Object ball: begin the fall. Center over the mouth, drop under gravity until SinkTargetY.
            Balls.BallSinking[i].Value = 1;
            Balls.SinkTargetY[i].Value = position.Y - PocketDropDepth;
            pos = new Vector3(mouth.X, position.Y, mouth.Y);
            ref Vector3 vel = ref Balls.Velocity[i].Value;
            vel = new Vector3(0f, MathF.Min(vel.Y, -0.5f), 0f);
            // Announce the drop for next-frame reactors (object ball → score point).
            Events.Append(new BallPocketed { BallId = Balls.BallId[i].Value, IsCue = pocket.IsCue });
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
}
