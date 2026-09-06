using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;
using Zio.FileSystems;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// Resolving a reference to the asset it names, by the engine's own rule. The case that matters
/// is a RENAME: the GUID survives it and the path does not, which is the whole reason a reference
/// carries both.
/// </summary>
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

    /// <summary>THE case. After a rename the path in an old document names nothing, and only the
    /// GUID still finds the asset — so the GUID has to be tried first.</summary>
    [Test]
    public async Task a_renamed_asset_still_resolves_by_its_identity()
    {
        var (files, layout) = Project();
        Asset(files, layout, "penguins/renamed.glb", Adelie);

        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Resolve(Adelie, "penguins/adelie.glb")).IsEqualTo("penguins/renamed.glb");
    }

    /// <summary>The recovery route: a sidecar lost, or a file from a branch that never had one.
    /// The path degrades to something a person can fix rather than to nothing.</summary>
    [Test]
    public async Task an_unknown_identity_falls_back_to_the_path()
    {
        var (files, layout) = Project();
        var index = AssetSidecars.Index(files, layout);

        await Assert.That(index.Resolve(Jeremy, "penguins/jeremy.glb")).IsEqualTo("penguins/jeremy.glb");
        await Assert.That(index.Resolve(Guid.Empty, "penguins/jeremy.glb")).IsEqualTo("penguins/jeremy.glb");
        await Assert.That(index.Resolve(Guid.Empty, null)).IsNull();
    }

    /// <summary>Two assets claiming one identity: the first in scan order wins, here exactly as at
    /// build, and <c>paradise assets verify</c> is what names the duplicate.</summary>
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

    /// <summary>A file the manifest ignores is one the build never ships, so a reference to it
    /// would name nothing. It carries no identity and is not given one.</summary>
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

    /// <summary>Minted on REFERENCE, not on import: an asset nobody points at needs no identity,
    /// and minting per file gives a sidecar to every stray image an author dropped in.</summary>
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
        // And it is now resolvable without re-indexing, which is what makes a pick usable at once.
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

    /// <summary>An identity for a file that is not there is a reference nothing can ever
    /// resolve.</summary>
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
