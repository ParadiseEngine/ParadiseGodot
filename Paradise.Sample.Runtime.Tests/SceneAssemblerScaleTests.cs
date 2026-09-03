using System.Numerics;
using Paradise.Physics;
using Paradise.Export.Data;
using Paradise.Export.Geometry;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>PR #27 review regression: entity WorldMatrix scale must not leak into collider
/// rotation (Quaternion.CreateFromRotationMatrix is not scale-invariant) and must fold into
/// shape dimensions/center — exported dimensions only carry the collider's scale RELATIVE to
/// its entity root; the root's own scale arrives via the contract matrix.</summary>
public class SceneAssemblerScaleTests
{
    private static readonly CollisionFilter HitAnything = new() { BelongsTo = ~0u, CollidesWith = ~0u };

    /// <summary>A static entity at a LOCAL pose. v6 carries no baked world matrix, so what these
    /// tests once passed as one is now the TRS the loader composes from.</summary>
    private static List<AuthoredComponentData> StaticEntity(
        string id, Vector3 position, Quaternion rotation, Vector3 scale, ColliderShapeData shape) => new()
    {
        AuthoredDocuments.Meta(id),
        AuthoredDocuments.Transform(position, rotation, scale),
        AuthoredDocuments.Entry(new RigidbodyComponentData { BodyType = PhysicsBodyType.Static }),
        AuthoredDocuments.Entry(new ColliderComponentData { Colliders = { shape } }),
    };

    private static bool CastDown(CollisionWorld world, float x, float z, out RaycastHit hit)
    {
        var input = new RaycastInput
        {
            Start = new Vector3(x, 10f, z),
            End = new Vector3(x, -10f, z),
            Filter = HitAnything,
        };
        return world.CastRay(input, out hit);
    }

    [Test]
    public async Task entity_scale_folds_into_box_dimensions_and_center()
    {
        // Unit cube at LocalCenter (1,0,0) on a root scaled ×2: the world box is centred at
        // (2,0,0) with half-extents (1,1,1) — a z=0.9 ray only hits once the scale is folded.
        var level = AuthoredDocuments.Scene(StaticEntity(
            "ScaledBox",
            Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f),
            new ColliderShapeData
            {
                ShapeType = PhysicsShapeType.Box,
                Size = new Vector3(1f, 1f, 1f),
                LocalCenter = new Vector3(1f, 0f, 0f),
            }));

        using var world = SceneAssembler.BuildCollisionWorld(level)!;
        await Assert.That(CastDown(world, 2f, 0.9f, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Y - 1f)).IsLessThan(1e-3f);
        // Sanity bound: just past the scaled extent still misses.
        await Assert.That(CastDown(world, 2f, 1.1f, out _)).IsFalse();
    }

    [Test]
    public async Task entity_scale_folds_into_sphere_radius()
    {
        // r=0.35 sphere on a root scaled ×3 → world radius 1.05 at (5,0,0).
        var level = AuthoredDocuments.Scene(StaticEntity(
            "ScaledSphere",
            new Vector3(5f, 0f, 0f), Quaternion.Identity, new Vector3(3f, 3f, 3f),
            new ColliderShapeData
            {
                ShapeType = PhysicsShapeType.Sphere,
                Radius = 0.35f,
            }));

        using var world = SceneAssembler.BuildCollisionWorld(level)!;
        await Assert.That(CastDown(world, 5.9f, 0f, out var hit)).IsTrue();
        await Assert.That(hit.Position.Y).IsGreaterThan(0f);
        await Assert.That(CastDown(world, 6.2f, 0f, out _)).IsFalse();
    }

    [Test]
    public async Task decompose_pose_returns_unit_rotation_under_nonuniform_scale()
    {
        // Spawn-pose path (Assemble): the sim must receive a UNIT quaternion equal to the
        // authored rotation, regardless of the authored scale.
        var authored = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var model = SceneAssembler.ToModelMatrix(
            ContractMatrix.Trs(new Vector3(3f, 4f, 5f), authored, new Vector3(2f, 1f, 4f)));

        var (position, rotation) = SceneAssembler.DecomposePose(model);
        await Assert.That((position - new Vector3(3f, 4f, 5f)).Length()).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(rotation.Length() - 1f)).IsLessThan(1e-5f);
        // Same rotation up to quaternion double-cover.
        await Assert.That(MathF.Abs(Quaternion.Dot(rotation, authored))).IsGreaterThan(1f - 1e-5f);
    }

    [Test]
    public async Task rotation_survives_nonuniform_scale()
    {
        // Unit cube on a root with scale (2,1,1) THEN a 90° yaw: the ×2 long axis (local X)
        // must end up along world Z. CreateFromRotationMatrix on the scaled basis produces a
        // garbage quaternion here; only a proper decomposition orients the box correctly.
        var level = AuthoredDocuments.Scene(StaticEntity(
            "ScaledRotatedBox",
            Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            new Vector3(2f, 1f, 1f),
            new ColliderShapeData
            {
                ShapeType = PhysicsShapeType.Box,
                Size = new Vector3(1f, 1f, 1f),
            }));

        using var world = SceneAssembler.BuildCollisionWorld(level)!;
        // Long (×2) extent along world Z: |z| ≤ 1. Short extent along world X: |x| ≤ 0.5.
        await Assert.That(CastDown(world, 0f, 0.9f, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Y - 0.5f)).IsLessThan(1e-3f);
        await Assert.That(CastDown(world, 0f, -0.9f, out _)).IsTrue();
        await Assert.That(CastDown(world, 0.9f, 0f, out _)).IsFalse();
    }
}
