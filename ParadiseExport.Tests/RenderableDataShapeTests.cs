using ParadiseExport.Data;
using ParadiseExport.Paths;
using ParadiseExport.Serialization;

namespace ParadiseExport.Tests;

/// <summary>Schema v2 shape: <see cref="RenderableComponentData"/> carries the mesh GLB
/// reference. Pins the serialized keys and the mesh field path convention.</summary>
public class RenderableDataShapeTests
{
    [Test]
    public async Task renderable_serializes_mesh_and_mesh_node_keys()
    {
        var renderable = new RenderableComponentData { Mesh = "meshes/abc123.glb" };
        string json = ExportJsonWriter.SerializeToString(renderable);

        await Assert.That(json.Contains("\"Mesh\": \"meshes/abc123.glb\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"MeshNode\": null", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task schema_version_is_two()
    {
        await Assert.That(LevelData.CurrentSchemaVersion).IsEqualTo(2);
        await Assert.That(new LevelData().SchemaVersion).IsEqualTo(2);
    }

    [Test]
    public async Task mesh_file_field_maps_under_meshes_directory()
    {
        await Assert.That(ExportPaths.MeshFileField("86ce4e74251e7f22")).IsEqualTo("meshes/86ce4e74251e7f22.glb");
        var paths = new ExportPaths("/tmp/paradise-data");
        var full = paths.GetMeshOutputPath("meshes/foo.glb").Replace('\\', '/');
        await Assert.That(full.EndsWith("/paradise-data/meshes/foo.glb", StringComparison.Ordinal)).IsTrue();
    }
}
