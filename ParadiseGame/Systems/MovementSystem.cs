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

        // Face the movement direction (cosmetic). Model forward is −Z (right-handed).
        float yaw = MathF.Atan2(-direction.X, -direction.Z);
        Quaternion desired = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        transform.Rotation = RotateTowards(transform.Rotation, desired, DegToRad(agent.AngularSpeed) * dt);
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

    /// <summary>Gather balls + character pushers into unmanaged scratch spans, run one stateless
    /// <see cref="PlanarSphereDynamics"/> step, scatter back with rolling rotation. Global by
    /// nature (pairwise collisions) — the reason this is a world system.</summary>
    private unsafe void StepBalls()
    {
        int sphereCount = Balls.Length;
        if (sphereCount == 0)
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
        try
        {
            Span<DynamicSphere> spheres = (sphereCount <= MaxStackBodies
                ? stackalloc DynamicSphere[MaxStackBodies]
                : new Span<DynamicSphere>(
                    sphereAlloc = (DynamicSphere*)NativeMemory.Alloc((nuint)sphereCount, (nuint)sizeof(DynamicSphere)),
                    sphereCount))[..sphereCount];
            Span<KinematicCapsule> pushers = (pusherCount <= MaxStackBodies
                ? stackalloc KinematicCapsule[MaxStackBodies]
                : new Span<KinematicCapsule>(
                    pusherAlloc = (KinematicCapsule*)NativeMemory.Alloc((nuint)pusherCount, (nuint)sizeof(KinematicCapsule)),
                    pusherCount))[..pusherCount];

            for (int i = 0; i < sphereCount; i++)
            {
                spheres[i] = new DynamicSphere
                {
                    Position = Balls.LocalTransform[i].Position,
                    Velocity = Balls.DynamicBody[i].Velocity,
                    Radius = Balls.DynamicBody[i].Radius,
                    Mass = Balls.DynamicBody[i].Mass,
                };
            }

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

            CollisionWorldHandle statics = Balls.PhysicsWorldRef[0].Handle;
            PlanarSphereDynamics.Step(spheres, pushers, statics, BallSettings, dt);

            for (int i = 0; i < sphereCount; i++)
            {
                ref readonly DynamicSphere sphere = ref spheres[i];
                ref LocalTransform transform = ref Balls.LocalTransform[i];
                Vector3 old = transform.Position;
                transform.Position = new Vector3(sphere.Position.X, old.Y, sphere.Position.Z);
                Balls.DynamicBody[i].Velocity = sphere.Velocity;

                // Rolling visual: for rolling-without-slipping on a Y-up plane, ω = (Up × v) / r.
                // Cosmetic and game-side (the engine library stays transcendental-free); rotation
                // lives in LocalTransform so the renderer's existing Slerp interpolates it.
                float speed = MathF.Sqrt(sphere.Velocity.X * sphere.Velocity.X + sphere.Velocity.Z * sphere.Velocity.Z);
                if (speed > 1e-4f && sphere.Radius > 1e-4f)
                {
                    Vector3 axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, sphere.Velocity));
                    Quaternion delta = Quaternion.CreateFromAxisAngle(axis, speed * dt / sphere.Radius);
                    transform.Rotation = Quaternion.Normalize(
                        Quaternion.Concatenate(transform.Rotation, delta));
                }
            }
        }
        finally
        {
            if (sphereAlloc != null) NativeMemory.Free(sphereAlloc);
            if (pusherAlloc != null) NativeMemory.Free(pusherAlloc);
        }
    }

    private static float HorizontalDistanceSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static float DegToRad(float degrees) => degrees * (MathF.PI / 180f);

    private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxRadians)
    {
        from = Quaternion.Normalize(from);
        to = Quaternion.Normalize(to);
        float dot = Math.Clamp(Quaternion.Dot(from, to), -1f, 1f);
        if (dot < 0f)
        {
            to = -to;
            dot = -dot;
        }

        float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 2f;
        if (angle <= 1e-4f || maxRadians <= 0f)
        {
            return from;
        }

        float t = Math.Clamp(maxRadians / angle, 0f, 1f);
        return Quaternion.Normalize(Quaternion.Slerp(from, to, t));
    }
}
