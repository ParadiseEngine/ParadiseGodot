using System.Numerics;
using Paradise.Physics;

namespace ParadiseGame.Core.Physics;

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

    private const int MaxSlideIterations = 4;
    private const float MinMoveSq = 1e-10f;
    private const float MinHorizontalNormal = 1e-3f;

    public static void Step(World world, CollisionWorld? collision, float deltaSeconds)
    {
        foreach (var row in world.Query(default(CharacterMovers)))
        {
            Vector3 desired = row.MoveIntent.DesiredVelocity;
            var remaining = new Vector3(desired.X, 0f, desired.Z) * deltaSeconds;
            if (remaining.LengthSquared() <= MinMoveSq)
            {
                continue;
            }

            Vector3 start = row.LocalTransform.Position;
            Vector3 position = start;

            if (collision is null)
            {
                position += remaining;
            }
            else
            {
                Collider capsule = Collider.CreateCapsule(
                    row.CharacterBody.Radius, row.CharacterBody.HalfLength, PhysicsLayers.CharacterCast);

                for (int iteration = 0; iteration < MaxSlideIterations && remaining.LengthSquared() > MinMoveSq; iteration++)
                {
                    var input = new ColliderCastInput
                    {
                        Collider = capsule,
                        Orientation = Quaternion.Identity,
                        Start = position,
                        End = position + remaining,
                    };
                    if (!collision.CastCollider(input, out ColliderCastHit hit))
                    {
                        position += remaining;
                        break;
                    }

                    // Advance to a skin's clearance short of the contact. A fraction-0 hit (already
                    // touching) advances nothing and the remainder slides along the wall instead.
                    float length = remaining.Length();
                    Vector3 direction = remaining / length;
                    float travel = length * hit.Fraction - Skin;
                    if (travel > 0f)
                    {
                        position += direction * travel;
                    }

                    var normal = new Vector3(hit.SurfaceNormal.X, 0f, hit.SurfaceNormal.Z);
                    float normalLength = normal.Length();
                    if (normalLength < MinHorizontalNormal)
                    {
                        break; // near-vertical normal (floor/ceiling-ish): planar backstop, stop here
                    }

                    normal /= normalLength;
                    Vector3 rest = remaining * (1f - hit.Fraction);
                    remaining = rest - Vector3.Dot(rest, normal) * normal;
                    float into = Vector3.Dot(remaining, normal);
                    if (into < 0f)
                    {
                        remaining -= into * normal; // numeric guard: never slide INTO the wall
                    }
                }
            }

            row.LocalTransform.Position = new Vector3(position.X, start.Y, position.Z);
        }
    }
}
