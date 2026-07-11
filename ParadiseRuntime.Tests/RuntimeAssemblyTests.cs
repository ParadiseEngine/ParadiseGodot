using System.Numerics;
using Paradise.Physics;
using Paradise.Rendering.Pbr;
using ParadiseExport.Data;
using ParadiseGame.Physics;

namespace ParadiseRuntime.Tests;

/// <summary>CPU-side runtime assembly over the committed data/ fixtures: loader round trip,
/// contract-matrix conversion, data-driven CollisionWorld, camera picking, lighting mapping.
/// (The GPU path is covered by the ParadiseRuntime --headless end-to-end run.)</summary>
public class RuntimeAssemblyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "sample.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }

    private static RuntimeLevel LoadSample() =>
        LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "sample.json"));

    [Test]
    public async Task loader_reads_the_committed_sample_scene()
    {
        var level = LoadSample();
        await Assert.That(level.Level.Entities.Count).IsEqualTo(21);
        // Source-GLB references (no per-entity bake): cube (Ground+2 obstacles+2 crates),
        // sphere (3 balls), capsule (guard) + 11 unique character/plant GLBs = 14 distinct.
        await Assert.That(level.MeshAssets.Count).IsEqualTo(15);
        await Assert.That(level.NavigationMesh).IsNotNull();
        // Every referenced material slot resolved.
        foreach (var entity in level.Level.Entities)
        {
            foreach (var slot in entity.Materials)
            {
                if (slot is not null) await Assert.That(level.Materials.ContainsKey(slot)).IsTrue();
            }
        }
    }

    [Test]
    public async Task contract_world_matrix_transposes_to_the_numerics_model_matrix()
    {
        var level = LoadSample();
        LevelEntityData? ball = null;
        foreach (var entity in level.Level.Entities)
        {
            if (entity.Id == "Ball1") ball = entity;
        }
        // Ball1 was authored at (1, 0.85, 2) — the contract stores translation at flat 12–14
        // (column-vector layout); the transpose puts it in the numerics Translation.
        var model = SceneAssembler.ToModelMatrix(ball!.WorldMatrix);
        await Assert.That((model.Translation - new Vector3(1f, 0.85f, 2f)).Length()).IsLessThan(1e-5f);
    }

    [Test]
    public async Task collision_world_builds_from_data_and_answers_the_click_ray()
    {
        var level = LoadSample();
        using var world = SceneAssembler.BuildCollisionWorld(level.Level)!;
        await Assert.That(world).IsNotNull();
        // 5 static entities × 1 box each: ground, 2 obstacles, 2 crates.
        await Assert.That(world.NumBodies).IsEqualTo(5);

        // A downward click-filter ray from above the origin must hit the ground slab.
        var input = new RaycastInput
        {
            Start = new Vector3(0f, 10f, 0f),
            End = new Vector3(0f, -10f, 0f),
            Filter = PhysicsLayers.ClickRay,
        };
        await Assert.That(world.CastRay(input, out var hit)).IsTrue();
        await Assert.That(MathF.Abs(hit.Position.Y - 0.5f)).IsLessThan(1e-4f); // ground top (box center 0, half height 0.5)
    }

    [Test]
    public async Task obstacle_colliders_land_on_the_obstacle_layer_not_the_floor()
    {
        // Regression: the exporter must carry each collider's Godot collision_layer (obstacle
        // mask 2 → contract layer index 1). If it exports 0 (the old bug), every static lands on
        // the Floor bit, so the character's Obstacle-only movement cast passes straight through
        // obstacles — "no collider on obstacles in .NET". Guard both directions of the filter.
        var level = LoadSample();
        using var world = SceneAssembler.BuildCollisionWorld(level.Level)!;

        // Horizontal cast at obstacle height toward Obstacle1 (authored at x=5, box half-extent 1
        // → near face at x=4), filtered to the Obstacle layer only (the movement-capsule filter).
        var throughObstacle = new RaycastInput
        {
            Start = new Vector3(0f, 2f, 0f),
            End = new Vector3(10f, 2f, 0f),
            Filter = PhysicsLayers.CharacterCast,
        };
        await Assert.That(world.CastRay(throughObstacle, out var obstacleHit)).IsTrue();
        await Assert.That(MathF.Abs(obstacleHit.Position.X - 4f)).IsLessThan(1e-3f);

        // The same Obstacle-only filter must NOT see the ground slab (it belongs to the Floor
        // layer) — proves the layers are actually distinct, not just "everything hits everything".
        var intoGround = new RaycastInput
        {
            Start = new Vector3(0f, 10f, 0f),
            End = new Vector3(0f, -10f, 0f),
            Filter = PhysicsLayers.CharacterCast,
        };
        await Assert.That(world.CastRay(intoGround, out _)).IsFalse();
    }

    [Test]
    public async Task camera_ray_through_screen_center_hits_the_walkable_ground()
    {
        var level = LoadSample();
        using var world = SceneAssembler.BuildCollisionWorld(level.Level)!;
        var rig = new CameraRig(level.Level.Camera, useOrthographic: false, fovDegrees: 75f);
        var camera = rig.Build(aspect: 16f / 9f);
        var viewProjection = PbrMath.ViewProjection(camera.View, camera.Projection);

        var ok = PbrMath.TryScreenPointToRay(
            new Vector2(640f, 360f), new Vector2(1280f, 720f), viewProjection, out var origin, out var direction);
        await Assert.That(ok).IsTrue();

        var input = new RaycastInput
        {
            Start = origin,
            End = origin + direction * 1000f,
            Filter = PhysicsLayers.ClickRay,
        };
        await Assert.That(world.CastRay(input, out var hit)).IsTrue();
        // The sample camera sits at (0,12,14) pitched −40°: the center ray lands on the ground
        // plane (y=0.5) within the 20×20 slab.
        await Assert.That(MathF.Abs(hit.Position.Y - 0.5f)).IsLessThan(1e-3f);
        await Assert.That(MathF.Abs(hit.Position.X)).IsLessThan(10f);
        await Assert.That(MathF.Abs(hit.Position.Z)).IsLessThan(10f);
    }

    [Test]
    public async Task lighting_maps_directional_light_and_ambient()
    {
        var level = LoadSample();
        var scene = new PbrScene();
        SceneAssembler.PopulateLighting(level, scene);

        await Assert.That(scene.Lights.Count).IsGreaterThan(0);
        var sun = scene.Lights[0];
        await Assert.That(sun.Type).IsEqualTo(PbrLightType.Directional);
        // Contract stores the aim direction; the shader convention is surface→light, so the
        // mapped direction must oppose the exported forward (a downward sun lights from above).
        await Assert.That(sun.Direction.Y).IsGreaterThan(0f);
        await Assert.That(MathF.Abs(sun.Direction.Length() - 1f)).IsLessThan(1e-4f);
        await Assert.That(scene.Ambient.Exposure).IsGreaterThan(0f);
    }

    [Test]
    public async Task planar_basis_is_horizontal_and_orthonormal()
    {
        var level = LoadSample();
        var rig = new CameraRig(level.Level.Camera, useOrthographic: false, fovDegrees: 75f);
        var (forward, right) = rig.PlanarBasis();
        await Assert.That(MathF.Abs(forward.Y)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(right.Y)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(forward.Length() - 1f)).IsLessThan(1e-5f);
        await Assert.That(MathF.Abs(Vector3.Dot(forward, right))).IsLessThan(1e-5f);
        // The sample camera looks down −Z (yaw 0): planar forward is −Z and right is +X.
        await Assert.That(forward.Z).IsLessThan(-0.99f);
        await Assert.That(right.X).IsGreaterThan(0.99f);
    }
}
