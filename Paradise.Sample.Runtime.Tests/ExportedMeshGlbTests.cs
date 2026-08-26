using System.Text.Json;
using Paradise.Assets.Gltf;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>The real-producer cross-check: the committed source GLBs each entity REFERENCES
/// (<c>data/Models/*.glb</c> characters/plants, <c>data/primitives/*.glb</c> shared primitives)
/// must parse with the ENGINE's GLB reader, and each entity's GLB primitive count must equal its
/// Materials slot count — the schema-v2 contract rule the runtime's slot-wise material override
/// depends on. This also anchors the reader's TRS/column-major conventions against real
/// third-party producers (the independent-verification item from the engine PR #68 review).</summary>
public class ExportedMeshGlbTests
{
    private static string RepoRoot()
    {
        // bin/Debug/net10.0 → Paradise.Export.Tests → repo root.
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
            if (Renderable(entity) is not { } renderable) continue;
            var meshField = renderable.GetProperty("Mesh").GetString();
            await Assert.That(meshField).IsNotNull();

            var glbPath = Path.Combine(root, "data", meshField!.Replace('/', Path.DirectorySeparatorChar));
            // Textures are external KTX2 sidecars next to the GLB — resolve image URIs from there.
            var glbDir = Path.GetDirectoryName(glbPath)!;
            var asset = GltfSceneReader.Read(
                File.ReadAllBytes(glbPath),
                uri => File.ReadAllBytes(Path.Combine(glbDir, uri.Replace('/', Path.DirectorySeparatorChar))));

            var primitiveCount = 0;
            foreach (var instance in asset.Instances)
            {
                primitiveCount += asset.Meshes[instance.MeshIndex].Primitives.Length;
            }
            // v5: material slots moved off RenderableComponentData onto their own
            // MaterialsComponentData entry in the same component list.
            var slotCount = Materials(entity)?.GetArrayLength() ?? 0;
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
    public async Task entities_reference_shared_source_glbs_under_data()
    {
        var root = RepoRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        var meshFields = new List<string>();
        foreach (var entity in document.RootElement.GetProperty("Entities").EnumerateArray())
        {
            if (Renderable(entity) is not { } renderable) continue;
            meshFields.Add(renderable.GetProperty("Mesh").GetString()!);
        }

        // Source-GLB references (no per-entity bake): the primitive entities share the unit GLBs —
        // cube (Ground + 2 obstacles + 2 crates), sphere (3 balls), capsule (guard) — while the 11
        // character/plant entities each reference their own model. 20 renderables, 14 distinct GLBs,
        // all under data/ (Models/… or primitives/…).
        await Assert.That(meshFields.Count).IsEqualTo(28);
        var distinct = new HashSet<string>(meshFields, StringComparer.Ordinal);
        await Assert.That(distinct.Count).IsEqualTo(16);
        foreach (var field in distinct)
        {
            await Assert.That(field.StartsWith("Models/", StringComparison.Ordinal) ||
                              field.StartsWith("primitives/", StringComparison.Ordinal)).IsTrue();
        }
    }

    /// <summary>The component payload for <paramref name="typeName"/> on this entity, or null
    /// when it authors none. In v5 an entity IS the array of components — there is no wrapping
    /// object — so the entry is found by its type name, not by a position.</summary>
    private static JsonElement? Component(JsonElement entity, string typeName)
    {
        foreach (var component in entity.EnumerateArray())
        {
            if (component.TryGetProperty("Type", out var type)
                && type.GetString() == typeName)
            {
                return component.GetProperty("Data");
            }
        }
        return null;
    }

    private static JsonElement? Renderable(JsonElement entity) =>
        Component(entity, "Paradise.Export.Data.RenderableComponentData");

    private static JsonElement? Materials(JsonElement entity) =>
        Component(entity, "Paradise.Export.Data.MaterialsComponentData")?.GetProperty("Slots");
}
