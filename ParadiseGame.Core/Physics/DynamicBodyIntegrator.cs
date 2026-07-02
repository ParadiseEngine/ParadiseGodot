using System;
using System.Buffers;
using System.Numerics;
using Paradise.Physics;

namespace ParadiseGame.Core.Physics;

/// <summary>
/// Thin ECS marshaller around the stateless <see cref="PlanarSphereDynamics"/> resolver: gathers
/// <see cref="DynamicBody"/> spheres and character pushers from components into pooled spans,
/// runs one dynamics step, and writes positions/velocities back. Runs right after
/// <see cref="CharacterMoveIntegrator.Step"/> each tick. All physics state stays in components;
/// the resolver itself keeps none (snapshots remain complete).
/// </summary>
public static class DynamicBodyIntegrator
{
    private static readonly PlanarDynamicsSettings Settings = PlanarDynamicsSettings.Default with
    {
        StaticFilter = PhysicsLayers.DynamicBodyCast,
        RequireSupport = true, // balls stop/slide at slab edges instead of rolling into the void
        SupportFilter = PhysicsLayers.SupportRay,
        SupportProbeDepth = PhysicsLayers.SupportProbeDepth,
    };

    public static void Step(World world, CollisionWorld? collision, float deltaSeconds)
    {
        int sphereCount = 0;
        foreach (var _ in world.Query(default(DynamicBodies))) sphereCount++;
        if (sphereCount == 0)
        {
            return;
        }

        int pusherCount = 0;
        foreach (var _ in world.Query(default(CharacterMovers))) pusherCount++;

        DynamicSphere[] spheres = ArrayPool<DynamicSphere>.Shared.Rent(sphereCount);
        KinematicCapsule[] pushers = ArrayPool<KinematicCapsule>.Shared.Rent(Math.Max(1, pusherCount));
        try
        {
            int i = 0;
            foreach (var row in world.Query(default(DynamicBodies)))
            {
                spheres[i++] = new DynamicSphere
                {
                    Position = row.LocalTransform.Position,
                    Velocity = row.DynamicBody.Velocity,
                    Radius = row.DynamicBody.Radius,
                    Mass = row.DynamicBody.Mass,
                };
            }

            int p = 0;
            foreach (var row in world.Query(default(CharacterMovers)))
            {
                pushers[p++] = new KinematicCapsule
                {
                    Position = row.LocalTransform.Position,
                    Velocity = row.MoveIntent.DesiredVelocity,
                    Radius = row.CharacterBody.Radius,
                    HalfLength = row.CharacterBody.HalfLength,
                };
            }

            PlanarSphereDynamics.Step(spheres.AsSpan(0, sphereCount), pushers.AsSpan(0, p),
                collision, Settings, deltaSeconds);

            // Same query, no structural changes in between → same iteration order as the gather.
            i = 0;
            foreach (var row in world.Query(default(DynamicBodies)))
            {
                ref readonly DynamicSphere sphere = ref spheres[i++];
                Vector3 old = row.LocalTransform.Position;
                row.LocalTransform.Position = new Vector3(sphere.Position.X, old.Y, sphere.Position.Z);
                row.DynamicBody.Velocity = sphere.Velocity;

                // Rolling visual: for rolling-without-slipping on a Y-up plane, ω = (Up × v) / r.
                // Cosmetic and game-side (the engine library stays transcendental-free); rotation
                // lives in LocalTransform so the renderer's existing Slerp interpolates it.
                float speed = MathF.Sqrt(sphere.Velocity.X * sphere.Velocity.X + sphere.Velocity.Z * sphere.Velocity.Z);
                if (speed > 1e-4f && sphere.Radius > 1e-4f)
                {
                    Vector3 axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, sphere.Velocity));
                    Quaternion delta = Quaternion.CreateFromAxisAngle(axis, speed * deltaSeconds / sphere.Radius);
                    row.LocalTransform.Rotation = Quaternion.Normalize(
                        Quaternion.Concatenate(row.LocalTransform.Rotation, delta));
                }
            }
        }
        finally
        {
            ArrayPool<DynamicSphere>.Shared.Return(spheres);
            ArrayPool<KinematicCapsule>.Shared.Return(pushers);
        }
    }
}
