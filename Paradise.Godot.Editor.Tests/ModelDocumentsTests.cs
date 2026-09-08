using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;
using Paradise.Authoring;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;
using Zio;
using Zio.FileSystems;

namespace Paradise.Godot.Editor.Tests;

// The engine ships mesh documents, not GLBs; previews must resolve documents back to source GLBs.
public class ModelDocumentsTests
{
    private static readonly UPath Root = "/repo/Pingu";

    private static readonly Guid CrateGlb = new("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid CrateMesh = new("bbbbbbbb-2222-4222-8222-222222222222");

    private static (MemoryFileSystem Files, AssetProjectLayout Layout) Project()
    {
        var files = new MemoryFileSystem();
        var layout = new AssetProjectLayout(Root);
        files.CreateDirectory(layout.Assets / "models");
        return (files, layout);
    }

    private static void Glb(MemoryFileSystem files, AssetProjectLayout layout, string relative, Guid guid, AssetReference? mesh)
    {
        files.WriteAllText(layout.Assets / relative, "glb bytes");
        var meta = new SidecarMeta(guid);
        if (mesh is not null)
        {
            GlbImportSettings.WriteExtraction(meta, new GlbExtraction(null, mesh, null, [], [], []));
        }
        meta.Save(files, SidecarMeta.PathFor(layout.Assets / relative));
    }

    private static void MeshDocument(MemoryFileSystem files, AssetProjectLayout layout, string relative, AssetReference source)
    {
        files.WriteAllText(
            layout.Assets / relative,
            new MeshReferenceDocument(source, MeshSlot.Mesh).Write());
    }

    [Test]
    [Arguments("models/crate.mesh", true)]
    [Arguments("models/hero.skinnedmesh", true)]
    [Arguments("models/hero.skeleton", false)]
    [Arguments("models/crate.glb", false)]
    [Arguments("models/crate.prefab", false)]
    public async Task only_geometry_documents_are_mesh_documents(string path, bool expected)
    {
        await Assert.That(ModelDocuments.IsMeshDocument(path)).IsEqualTo(expected);
    }

    [Test]
    public async Task a_glb_resolves_to_the_mesh_document_its_sidecar_records()
    {
        var (files, layout) = Project();
        Glb(files, layout, "models/crate.glb", CrateGlb, new AssetReference(CrateMesh, "models/crate.mesh"));

        var reference = ModelDocuments.MeshDocumentOf(files, layout, "models/crate.glb", out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(reference!.Value.Kind).IsEqualTo(AuthoredValueKind.Reference);
        await Assert.That(reference.Value.Identity).IsEqualTo(CrateMesh);
        await Assert.That(reference.Value.Text).IsEqualTo("models/crate.mesh");
    }

    [Test]
    public async Task a_glb_nobody_extracted_is_refused_with_the_command_that_would()
    {
        var (files, layout) = Project();
        Glb(files, layout, "models/crate.glb", CrateGlb, mesh: null);

        var reference = ModelDocuments.MeshDocumentOf(files, layout, "models/crate.glb", out var problem);

        await Assert.That(reference).IsNull();
        await Assert.That(problem).Contains("paradise assets watch");
    }

    [Test]
    public async Task a_glb_with_no_sidecar_is_refused_rather_than_minted()
    {
        var (files, layout) = Project();
        files.WriteAllText(layout.Assets / "models/crate.glb", "glb bytes");

        var reference = ModelDocuments.MeshDocumentOf(files, layout, "models/crate.glb", out var problem);

        await Assert.That(reference).IsNull();
        await Assert.That(problem).Contains("no sidecar");
        await Assert.That(files.FileExists(SidecarMeta.PathFor(layout.Assets / "models/crate.glb"))).IsFalse();
    }

    [Test]
    public async Task a_mesh_document_leads_back_to_the_glb_it_is_cooked_from()
    {
        var (files, layout) = Project();
        files.WriteAllText(layout.Assets / "models/crate.glb", "glb bytes");
        MeshDocument(files, layout, "models/crate.mesh", new AssetReference(CrateGlb, "models/crate.glb"));

        var glb = ModelDocuments.SourceGlbOf(
            files, layout, "models/crate.mesh", _ => throw new InvalidOperationException("not needed"), out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(glb).IsEqualTo("models/crate.glb");
    }

    [Test]
    public async Task a_moved_glb_is_found_by_identity_when_the_spelled_path_is_gone()
    {
        var (files, layout) = Project();
        files.WriteAllText(layout.Assets / "models/box.glb", "glb bytes");
        MeshDocument(files, layout, "models/crate.mesh", new AssetReference(CrateGlb, "models/crate.glb"));

        var glb = ModelDocuments.SourceGlbOf(
            files, layout, "models/crate.mesh", guid => guid == CrateGlb ? "models/box.glb" : null, out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(glb).IsEqualTo("models/box.glb");
    }

    [Test]
    public async Task a_source_that_left_the_tree_is_reported_rather_than_guessed()
    {
        var (files, layout) = Project();
        MeshDocument(files, layout, "models/crate.mesh", new AssetReference(CrateGlb, "models/crate.glb"));

        var glb = ModelDocuments.SourceGlbOf(files, layout, "models/crate.mesh", _ => null, out var problem);

        await Assert.That(glb).IsNull();
        await Assert.That(problem).Contains("models/crate.glb");
    }

    [Test]
    public async Task a_file_that_is_not_a_mesh_document_is_reported()
    {
        var (files, layout) = Project();
        files.WriteAllText(layout.Assets / "models/crate.mesh", "not a document {{{");

        var glb = ModelDocuments.SourceGlbOf(files, layout, "models/crate.mesh", _ => null, out var problem);

        await Assert.That(glb).IsNull();
        await Assert.That(problem).Contains("models/crate.mesh");
    }
}
