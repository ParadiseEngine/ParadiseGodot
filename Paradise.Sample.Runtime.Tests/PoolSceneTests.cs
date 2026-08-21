using System.Numerics;
using Paradise.Physics;
using Paradise.Export.Data;
using Paradise.Sample.Pool.Physics;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>The committed pool-table scene (data/scenes/pool.json) through the CPU assembly
/// path: trigger pockets stay out of the solid collision world but come back through
/// ExtractPockets, cushions answer obstacle-filtered casts with pocket gaps open, and the
/// authored physics material params survive the exporter round trip.</summary>
public class PoolSceneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "pool.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }

    private static RuntimeLevel LoadPool() =>
        LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "pool.json"));

    [Test]
    public async Task loader_reads_the_committed_pool_scene()
    {
        var level = LoadPool();
        await Assert.That(level.Level.Entities.Count).IsEqualTo(45);
        foreach (var entity in level.Level.Entities)
        {
            foreach (var slot in entity.Materials)
            {
                if (slot is not null) await Assert.That(level.Materials.ContainsKey(slot)).IsTrue();
            }
        }
    }

    [Test]
    public async Task trigger_pockets_stay_out_of_the_solid_collision_world()
    {
        var level = LoadPool();
        using var world = SceneAssembler.BuildCollisionWorld(level.Level)!;
        // 12 solid statics: room floor, table bed, 6 cushion segments, 4 frame rails.
        // The 6 pocket trigger spheres must NOT become bodies (they'd plug the pocket mouths).
        await Assert.That(world.NumBodies).IsEqualTo(12);
    }

    [Test]
    public async Task extract_pockets_returns_the_six_authored_mouths()
    {
        var level = LoadPool();
        var pockets = SceneAssembler.ExtractPockets(level.Level);
        await Assert.That(pockets.Count).IsEqualTo(6);

        // 4 corner pockets (r 0.25) + 2 side pockets (r 0.22) at the authored mouth centers.
        var corners = pockets.FindAll(p => MathF.Abs(p.Radius - 0.25f) < 1e-4f);
        var sides = pockets.FindAll(p => MathF.Abs(p.Radius - 0.22f) < 1e-4f);
        await Assert.That(corners.Count).IsEqualTo(4);
        await Assert.That(sides.Count).IsEqualTo(2);
        foreach (var (center, _) in corners)
        {
            await Assert.That(MathF.Abs(MathF.Abs(center.X) - 3.05f)).IsLessThan(1e-4f);
            await Assert.That(MathF.Abs(MathF.Abs(center.Z) - 1.55f)).IsLessThan(1e-4f);
        }
        foreach (var (center, _) in sides)
        {
            await Assert.That(MathF.Abs(center.X)).IsLessThan(1e-4f);
            await Assert.That(MathF.Abs(MathF.Abs(center.Z) - 1.62f)).IsLessThan(1e-4f);
        }
    }

    [Test]
    public async Task cushions_block_at_the_play_field_edge_and_pocket_mouths_stay_open()
    {
        var level = LoadPool();
        using var world = SceneAssembler.BuildCollisionWorld(level.Level)!;

        // A ball-height cast (the DynamicBodyCast obstacle filter) into a cushion stops at the
        // authored inner face z = 1.5.
        var intoCushion = new RaycastInput
        {
            Start = new Vector3(1.4f, 0.95f, 0f),
            End = new Vector3(1.4f, 0.95f, 5f),
            Filter = PhysicsLayers.DynamicBodyCast,
        };
        await Assert.That(world.CastRay(intoCushion, out var cushionHit)).IsTrue();
        await Assert.That(MathF.Abs(cushionHit.Position.Z - 1.5f)).IsLessThan(1e-3f);

        // The same cast through the side-pocket gap (x = 0) travels past the cushion line and
        // only stops at the frame backstop (inner face z = 1.8) — the mouth is open.
        var throughPocketMouth = new RaycastInput
        {
            Start = new Vector3(0f, 0.95f, 0f),
            End = new Vector3(0f, 0.95f, 5f),
            Filter = PhysicsLayers.DynamicBodyCast,
        };
        await Assert.That(world.CastRay(throughPocketMouth, out var frameHit)).IsTrue();
        await Assert.That(MathF.Abs(frameHit.Position.Z - 1.8f)).IsLessThan(1e-3f);
    }

    [Test]
    public async Task project_physics_dynamics_load_from_the_committed_settings()
    {
        // data/ProjectSettings.json carries the global solver tuning (Paradise/Settings… →
        // ProjectSettingsExporter); the loader must surface it normalized. The committed values
        // are the contract defaults (nothing overridden in project.godot yet).
        var level = LoadPool();
        var dynamics = level.PhysicsDynamics;
        await Assert.That(MathF.Abs(dynamics.MinSpeed - 0.005f)).IsLessThan(1e-6f);
        await Assert.That(MathF.Abs(dynamics.Skin - 0.02f)).IsLessThan(1e-6f);
        await Assert.That(MathF.Abs(dynamics.PushStrength - 1.2f)).IsLessThan(1e-6f);
        await Assert.That(MathF.Abs(dynamics.DefaultStaticRestitution - 0.4f)).IsLessThan(1e-6f);
    }

    [Test]
    public async Task authored_physics_material_params_survive_the_export_round_trip()
    {
        var level = LoadPool();
        LevelEntityData? cue = null;
        foreach (var entity in level.Level.Entities)
        {
            if (entity.StableId == "CueBall") cue = entity;
        }
        await Assert.That(cue).IsNotNull();
        var rigidbody = cue!.Get<RigidbodyComponentData>()!;
        await Assert.That(rigidbody.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(MathF.Abs(rigidbody.LinearDamping - 0.6f)).IsLessThan(1e-4f);
        await Assert.That(MathF.Abs(rigidbody.Restitution - 0.92f)).IsLessThan(1e-4f);

        // Scene cushion bounce = the liveliest obstacle-layer static (the cushions' 0.75, not
        // the frame rails' 0.5, and never the trigger pockets).
        await Assert.That(MathF.Abs(SceneAssembler.StaticSurfaceRestitution(level.Level) - 0.75f)).IsLessThan(1e-4f);
    }
}
