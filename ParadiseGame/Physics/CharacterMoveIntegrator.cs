using System.Numerics;
using Paradise.Physics;

namespace ParadiseGame.Physics;

/// <summary>
/// Integrates each character's <see cref="MoveIntent"/> against the static collision world with a
/// capsule-cast-and-slide loop, then writes the resolved position back to
/// <see cref="LocalTransform"/>. Managed (like <see cref="Navigation.DirectMover"/>) because it
/// calls the managed collision world; runs after the steering schedule each tick.
///
/// Planar contract: Y is NEVER modified — collision resolves horizontal motion only, and hit
/// normals are flattened to the XZ plane before sliding.
/// </summary>
public static class CharacterMoveIntegrator
{
    /// <summary>Clearance kept between the capsule and any surface (meters).</summary>
    public const float Skin = 0.02f;

    private const float MinMoveSq = 1e-10f;

    public static void Step(World world, CollisionWorld? collision, float deltaSeconds)
    {
        foreach (var row in world.Query(default(CharacterMovers)))
        {
            Vector3 desired = row.MoveIntent.DesiredVelocity;
            var displacement = new Vector3(desired.X, 0f, desired.Z) * deltaSeconds;
            if (displacement.LengthSquared() <= MinMoveSq)
            {
                continue;
            }

            Vector3 start = row.LocalTransform.Position;
            Vector3 position;
            if (collision is null)
            {
                position = start + displacement;
            }
            else
            {
                position = PlanarCapsuleSlide.Move(collision, PhysicsLayers.CharacterCast,
                    row.CharacterBody.Radius, row.CharacterBody.HalfLength, start, displacement, Skin);
                // Ground containment: never step off the walkable slab (slide along its edge).
                position = PlanarGroundSupport.Clamp(collision, PhysicsLayers.SupportRay,
                    start, position, PhysicsLayers.SupportProbeDepth);
            }

            row.LocalTransform.Position = new Vector3(position.X, start.Y, position.Z);
        }
    }
}
