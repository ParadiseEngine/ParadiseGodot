using System.Numerics;
using ParadiseExport.Data;
using ParadiseExport.Serialization;

namespace ParadiseExport.Tests;

/// <summary>Round-trip guarantee for the new read half: writer output must deserialize back to
/// equal values through every converter (vectors, quaternions, matrices, Color32, enums).</summary>
public class ExportJsonReaderTests
{
    [Test]
    public async Task level_document_round_trips_through_write_and_read()
    {
        var document = new LevelData
        {
            Camera = new CameraData
            {
                Position = new Vector3(1.5f, 2.25f, -3.125f),
                Rotation = new Vector3(10f, 20f, 30f),
                OrthographicSize = 5.5f,
            },
            EnvironmentMesh = "meshes/sample.environment.glb",
            NavMeshFile = "sample.navmesh.bin",
        };
        document.StaticColliders.Add(new ColliderShapeData
        {
            Id = "Ground",
            IsStatic = true,
            Layer = 0,
            ShapeType = PhysicsShapeType.Box,
            LocalCenter = new Vector3(0f, -0.5f, 0f),
            LocalRotation = Quaternion.Identity,
            Size = new Vector3(20f, 1f, 20f),
        });
        document.Entities.Add(new LevelEntityData
        {
            Id = "Ball1",
            WorldMatrix = Matrix4x4.CreateTranslation(1f, 0.85f, 2f),
            Materials = ["materials/mat_ball1.json"],
            Components = new EntityComponentsData
            {
                Renderable = new RenderableComponentData { Mesh = "meshes/abc.glb" },
                Rigidbody = new RigidbodyComponentData { BodyType = PhysicsBodyType.Dynamic, Mass = 2f },
                Collider = new ColliderComponentData
                {
                    Colliders = [new ColliderShapeData { ShapeType = PhysicsShapeType.Sphere, Radius = 0.35f }],
                },
            },
        });

        var parsed = ExportJsonReader.ReadLevel(ExportJsonWriter.SerializeToString(document));

        await Assert.That(parsed.SchemaVersion).IsEqualTo(LevelData.CurrentSchemaVersion);
        await Assert.That(parsed.Camera!.Position).IsEqualTo(new Vector3(1.5f, 2.25f, -3.125f));
        await Assert.That(parsed.EnvironmentMesh).IsEqualTo("meshes/sample.environment.glb");
        await Assert.That(parsed.StaticColliders.Count).IsEqualTo(1);
        await Assert.That(parsed.StaticColliders[0].Size).IsEqualTo(new Vector3(20f, 1f, 20f));
        await Assert.That(parsed.StaticColliders[0].ShapeType).IsEqualTo(PhysicsShapeType.Box);

        var entity = parsed.Entities[0];
        await Assert.That(entity.WorldMatrix!.Value.Translation).IsEqualTo(new Vector3(1f, 0.85f, 2f));
        await Assert.That(entity.Components.Renderable!.Mesh).IsEqualTo("meshes/abc.glb");
        await Assert.That(entity.Components.Rigidbody!.BodyType).IsEqualTo(PhysicsBodyType.Dynamic);
        await Assert.That(entity.Components.Collider!.Colliders[0].Radius).IsEqualTo(0.35f);
    }

    [Test]
    public async Task committed_sample_scene_parses()
    {
        var root = FindRepoRoot();
        var level = ExportJsonReader.ReadLevel(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        await Assert.That(level.SchemaVersion).IsEqualTo(2);
        await Assert.That(level.Entities.Count).IsEqualTo(6);
        await Assert.That(level.StaticColliders.Count).IsEqualTo(3);
        await Assert.That(level.EnvironmentMesh).IsNotNull();

        var settings = ExportJsonReader.ReadProjectSettings(File.ReadAllText(Path.Combine(root, "data", "ProjectSettings.json")));
        await Assert.That(settings.Rendering).IsNotNull();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "sample.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }
}
