using System.Text.Json;
using Paradise.Assets.Gltf;

namespace ParadiseExport.Tests;

/// <summary>The real-producer cross-check: the committed <c>data/meshes/*.glb</c> fixtures
/// (exported by Godot's GltfDocument through MeshGlbExporter) must parse with the ENGINE's GLB
/// reader, and each entity's GLB primitive count must equal its Materials slot count — the
/// schema-v2 contract rule the runtime's slot-wise material override depends on. This also
/// anchors the reader's TRS/column-major conventions against a real third-party producer
/// (the independent-verification item from the engine PR #68 review).</summary>
public class ExportedMeshGlbTests
{
    private static string RepoRoot()
    {
        // bin/Debug/net10.0 → ParadiseExport.Tests → repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "sample.json")))
        {
            dir = dir.Parent!;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root with data/scenes/sample.json not found.");
    }

    [Test]
    public async Task every_sample_entity_mesh_parses_and_matches_its_material_slot_count()
    {
        var root = RepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        var entities = document.RootElement.GetProperty("Entities");

        var checkedMeshes = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var renderable = entity.GetProperty("Components").GetProperty("Renderable");
            if (renderable.ValueKind == JsonValueKind.Null) continue;
            var meshField = renderable.GetProperty("Mesh").GetString();
            await Assert.That(meshField).IsNotNull();

            var glbPath = Path.Combine(root, "data", meshField!.Replace('/', Path.DirectorySeparatorChar));
            var asset = GltfSceneReader.Read(File.ReadAllBytes(glbPath));

            var primitiveCount = 0;
            foreach (var instance in asset.Instances)
            {
                primitiveCount += asset.Meshes[instance.MeshIndex].Primitives.Length;
            }
            var slotCount = entity.GetProperty("Materials").GetArrayLength();
            await Assert.That(primitiveCount).IsEqualTo(slotCount);

            // Geometry sanity: real vertices with normals, non-degenerate.
            foreach (var mesh in asset.Meshes)
            {
                foreach (var primitive in mesh.Primitives)
                {
                    await Assert.That(primitive.VertexCount).IsGreaterThan(0);
                    await Assert.That(primitive.Indices.Length % 3).IsEqualTo(0);
                    await Assert.That(primitive.HasNormals).IsTrue();
                }
            }
            checkedMeshes++;
        }

        await Assert.That(checkedMeshes).IsGreaterThan(0);
    }

    [Test]
    public async Task deduplicated_crates_share_one_glb_file()
    {
        var root = RepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        var meshFields = new List<string>();
        foreach (var entity in document.RootElement.GetProperty("Entities").EnumerateArray())
        {
            var renderable = entity.GetProperty("Components").GetProperty("Renderable");
            if (renderable.ValueKind == JsonValueKind.Null) continue;
            meshFields.Add(renderable.GetProperty("Mesh").GetString()!);
        }

        // Crate1+Crate2 share one GLB and Ball1..3 share another (dedupe ignores material
        // overrides — those live in the per-entity Materials slots): 6 renderables, 3 GLBs.
        await Assert.That(meshFields.Count).IsEqualTo(6);
        var distinct = new HashSet<string>(meshFields, StringComparer.Ordinal);
        await Assert.That(distinct.Count).IsEqualTo(3);
    }

    [Test]
    public async Task environment_mesh_holds_the_non_entity_visuals()
    {
        var root = RepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        var envField = document.RootElement.GetProperty("EnvironmentMesh").GetString();
        await Assert.That(envField).IsNotNull();

        var asset = GltfSceneReader.Read(File.ReadAllBytes(Path.Combine(root, "data", envField!)));
        var primitiveCount = 0;
        foreach (var instance in asset.Instances)
        {
            primitiveCount += asset.Meshes[instance.MeshIndex].Primitives.Length;
        }
        // Ground + Obstacle1 + Obstacle2 — the balls are entities now, not scenery.
        await Assert.That(primitiveCount).IsEqualTo(3);

        var statics = document.RootElement.GetProperty("StaticColliders");
        await Assert.That(statics.GetArrayLength()).IsEqualTo(3);
    }
}
