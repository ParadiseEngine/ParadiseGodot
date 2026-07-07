using ParadiseExport.Data;
using ParadiseExport.Serialization;

namespace ParadiseExport.Tests;

/// <summary>Schema-v3 resource identity: deterministic name-based GUID minting, the manifest
/// builder's idempotent registration, and the committed manifest's completeness against the
/// sample scene's references.</summary>
public class ResourceManifestTests
{
    [Test]
    public async Task minted_guid_is_deterministic_and_parseable()
    {
        var a = ResourceGuid.FromString("meshes/86ce4e74251e7f22.glb");
        var b = ResourceGuid.FromString("meshes/86ce4e74251e7f22.glb");
        var c = ResourceGuid.FromString("materials/mat_crate.json");

        await Assert.That(a).IsEqualTo(b);       // same input → same GUID (cross-export stability)
        await Assert.That(a).IsNotEqualTo(c);    // distinct inputs → distinct GUIDs
        await Assert.That(ResourceGuid.IsGuid(a)).IsTrue();
        await Assert.That(ResourceGuid.IsGuid("meshes/86ce4e74251e7f22.glb")).IsFalse();
    }

    [Test]
    public async Task builder_registers_idempotently()
    {
        var builder = new ResourceManifestBuilder();
        var first = builder.Register("meshes/crate.glb");
        var second = builder.Register("meshes/crate.glb");

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(builder.Data.Resources.Count).IsEqualTo(1);
        await Assert.That(builder.Data.Resources[first]).IsEqualTo("meshes/crate.glb");
    }

    [Test]
    public async Task committed_manifest_resolves_every_sample_reference()
    {
        var root = FindRepoRoot();
        var level = ExportJsonReader.ReadLevel(File.ReadAllText(Path.Combine(root, "data", "scenes", "sample.json")));
        var manifest = ExportJsonReader.ReadManifest(File.ReadAllText(Path.Combine(root, "data", "resources.json"))).Resources;

        await Assert.That(level.SchemaVersion).IsEqualTo(3);
        await Assert.That(ResourceGuid.IsGuid(level.NavMeshFile)).IsTrue();
        await Assert.That(manifest.ContainsKey(level.NavMeshFile!)).IsTrue();

        foreach (var entity in level.Entities)
        {
            if (entity.Components.Renderable?.Mesh is { } mesh)
            {
                await Assert.That(ResourceGuid.IsGuid(mesh)).IsTrue();
                await Assert.That(manifest.ContainsKey(mesh)).IsTrue();
                // The manifest points at a file that exists under data/.
                await Assert.That(File.Exists(Path.Combine(root, "data", manifest[mesh]))).IsTrue();
            }
            foreach (var slot in entity.Materials)
            {
                if (slot is null) continue;
                await Assert.That(ResourceGuid.IsGuid(slot)).IsTrue();
                await Assert.That(manifest.ContainsKey(slot)).IsTrue();
            }
        }
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
