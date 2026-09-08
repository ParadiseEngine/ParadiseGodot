using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;
using Zio.FileSystems;

namespace Paradise.Godot.Editor.Tests;

public class AssetSidecarsTests
{
    private static readonly UPath Root = "/repo/Pingu";

    private static readonly Guid Adelie = new("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid Jeremy = new("bbbbbbbb-2222-4222-8222-222222222222");

    private static (MemoryFileSystem Files, AssetProjectLayout Layout) Project()
    {
        var files = new MemoryFileSystem();
        var layout = new AssetProjectLayout(Root);
        files.CreateDirectory(layout.Assets / "penguins");
        return (files, layout);
    }

    private static void Asset(MemoryFileSystem files, AssetProjectLayout layout, string relative, Guid? guid)
    {
        var path = layout.Assets / relative;
        files.CreateDirectory(path.GetDirectory());
        files.WriteAllText(path, "glb bytes");
        if (guid is { } value) new SidecarMeta(value).Save(files, SidecarMeta.PathFor(path));
    }

    [Test]
    public async Task an_asset_with_a_sidecar_is_indexed_both_ways()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/adelie.glb", Adelie);

        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Count).IsEqualTo(1);
        await Assert.That(index.PathOf(Adelie)).IsEqualTo("penguins/adelie.glb");
        await Assert.That(index.GuidAt("penguins/adelie.glb")).IsEqualTo(Adelie);
    }

    [Test]
    public async Task a_renamed_asset_still_resolves_by_its_identity()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/renamed.glb", Adelie);

        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Resolve(Adelie, "penguins/adelie.glb")).IsEqualTo("penguins/renamed.glb");
    }

    [Test]
    public async Task an_unknown_identity_falls_back_to_the_path()
    {
        var (files, layout) = Project();
        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Resolve(Jeremy, "penguins/jeremy.glb")).IsEqualTo("penguins/jeremy.glb");
        await Assert.That(index.Resolve(Guid.Empty, "penguins/jeremy.glb")).IsEqualTo("penguins/jeremy.glb");
        await Assert.That(index.Resolve(Guid.Empty, null)).IsNull();
    }

    // Match build scan order; paradise assets verify reports duplicate identities.
    [Test]
    public async Task a_duplicate_identity_resolves_to_the_first_asset_in_scan_order()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/copy.glb", Adelie);
        Asset(files, layout, "penguins/adelie.glb", Adelie);

        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.PathOf(Adelie)).IsEqualTo("penguins/adelie.glb");
    }

    [Test]
    public async Task an_unreadable_sidecar_leaves_its_asset_without_an_identity()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/adelie.glb", Adelie);
        files.WriteAllText(layout.Assets / "penguins" / ("broken.glb" + SidecarMeta.Suffix), "not toml {{{");
        files.WriteAllText(layout.Assets / "penguins" / "broken.glb", "glb bytes");

        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Count).IsEqualTo(1);
        await Assert.That(index.GuidAt("penguins/broken.glb")).IsNull();
    }

    [Test]
    public async Task an_ignored_file_has_no_identity_and_is_not_given_one()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/scratch.tmp", Jeremy);
        Asset(files, layout, "penguins/draft.glb", guid: null);

        var index = AssetSidecars.Index(files, layout, AssetIgnoreRules.Parse(["*.tmp", "draft.*"]));

        await Assert.That(index.GuidAt("penguins/scratch.tmp")).IsNull();
        await Assert.That(index.IsIgnored("penguins/draft.glb")).IsTrue();
        await Assert.That(index.EnsureIdentity(files, "penguins/draft.glb")).IsNull();
        await Assert.That(files.FileExists(SidecarMeta.PathFor(layout.Assets / "penguins/draft.glb"))).IsFalse();
    }

    // Minting on reference avoids sidecars for unused files.
    [Test]
    public async Task an_identity_is_minted_and_written_on_first_reference()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/jeremy.glb", guid: null);
        var index = AssetSidecars.Index(files, layout);
        await Assert.That(index.Count).IsEqualTo(0);

        var minted = index.EnsureIdentity(files, "penguins/jeremy.glb");

        await Assert.That(minted).IsNotNull();
        await Assert.That(files.FileExists(
            SidecarMeta.PathFor(layout.Assets / "penguins/jeremy.glb"))).IsTrue();
        await Assert.That(index.PathOf(minted!.Value)).IsEqualTo("penguins/jeremy.glb");
        await Assert.That(index.GuidAt("penguins/jeremy.glb")).IsEqualTo(minted);
        await Assert.That(index.Count).IsEqualTo(1);
    }

    [Test]
    public async Task minting_is_idempotent()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/adelie.glb", Adelie);
        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.EnsureIdentity(files, "penguins/adelie.glb")).IsEqualTo(Adelie);
    }

    [Test]
    public async Task nothing_is_minted_for_an_asset_that_does_not_exist()
    {
        var (files, layout) = Project();
        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.EnsureIdentity(files, "penguins/ghost.glb")).IsNull();
        await Assert.That(index.Count).IsEqualTo(0);
    }

    [Test]
    public async Task a_project_with_no_assets_directory_indexes_to_nothing()
    {
        var files = new MemoryFileSystem();
        var index = AssetSidecars.Index(files, new AssetProjectLayout("/nowhere"));

        await Assert.That(index.Count).IsEqualTo(0);
    }
}
