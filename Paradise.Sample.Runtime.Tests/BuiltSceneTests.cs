using Paradise.Assets.Mesh;
using Paradise.Export.Data;
using Zio;
using Zio.FileSystems;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>
/// The real-producer cross-check, against what `paradise assets build` actually writes: every
/// mesh a scene entity references reads as the engine's mesh blob, and its draw count equals the
/// entity's material slot count — the rule the runtime's slot-wise material override depends on.
/// Replaces the GLB cross-check from the days the scene referenced GLBs.
/// </summary>
public class BuiltSceneTests
{
    [Test]
    public async Task the_built_sample_scene_parses_through_the_contract_reader()
    {
        var files = new PhysicalFileSystem();
        var scene = files.ConvertPathFromInternal(BuiltFixture.Scene("sample"));

        var level = BuiltDocument.ReadPrefab(files, scene);

        await Assert.That(level.SchemaVersion).IsEqualTo(6);
        // 62 authored objects under the one root the document model requires.
        await Assert.That(level.Entities.Count).IsEqualTo(63);

        var settings = BuiltDocument.Find(files, scene.GetDirectory().GetDirectory(), "ProjectSettings");
        await Assert.That(settings).IsNotNull();
        await Assert.That(BuiltDocument.ReadProjectSettings(files, settings!.Value).Rendering).IsNotNull();
    }

    [Test]
    public async Task every_sample_entity_mesh_reads_as_a_blob_and_matches_its_material_slot_count()
    {
        var level = LevelLoader.Load(BuiltFixture.Scene("sample"));

        var checkedMeshes = 0;
        foreach (var entity in level.Scene.Entities)
        {
            if (entity.Get<RenderableComponentData>()?.Mesh is not { } meshField) continue;

            var cooked = level.Meshes[meshField];
            await Assert.That(MeshBlobFormat.IsMeshBlob(cooked.Blob)).IsTrue();
            var mesh = MeshBlobFormat.Read(cooked.Blob);

            // Draw i is glTF primitive i, and the scene's Materials list is one slot per primitive.
            var slotCount = entity.Get<MaterialsComponentData>()?.Slots.Count ?? 0;
            await Assert.That(mesh.Draws.Count).IsEqualTo(slotCount);

            // Geometry sanity: real vertices, non-degenerate.
            await Assert.That(mesh.VertexCount).IsGreaterThan(0);
            await Assert.That(mesh.Indices.Length).IsGreaterThan(0);
            await Assert.That(mesh.BoundsMax.X - mesh.BoundsMin.X).IsGreaterThan(0f);
            checkedMeshes++;
        }

        await Assert.That(checkedMeshes).IsGreaterThan(20);
    }

    /// <summary>A GLB's own materials come back through its seed prefab: the slot table the build
    /// recorded there, one entry per draw, naming the documents extract wrote.</summary>
    [Test]
    public async Task a_glb_with_materials_reports_them_through_its_seed_prefab()
    {
        var level = LevelLoader.Load(BuiltFixture.Scene("sample"));
        var knight = level.Scene.Entities.First(e => e.Name == "Knight");
        var cooked = level.Meshes[knight.Get<RenderableComponentData>()!.Mesh!];

        await Assert.That(cooked.Materials.Count).IsEqualTo(MeshBlobFormat.Read(cooked.Blob).Draws.Count);
        foreach (var material in cooked.Materials)
        {
            await Assert.That(material).IsNotNull();
            await Assert.That(level.Materials.ContainsKey(material!)).IsTrue();
        }
    }
}
