using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// The conversions between the four names one file answers to. Every case here is one a picker or
/// a document write actually produces, and the two that matter most are the ones that must return
/// null: a file outside the project, and a file inside the Godot project but outside
/// <c>assets/</c>.
/// </summary>
public class AssetProjectPathsTests
{
    private const string Root = "/repo/Pingu";

    /// <summary>The common shape: the Godot project IS the asset project.</summary>
    private static AssetProjectPaths Coincident() =>
        new(Root, new AssetProjectLayout(Root));

    /// <summary>Godot nested inside the asset project — nothing makes the two roots equal, so the
    /// conversions are tested where they differ.</summary>
    private static AssetProjectPaths Nested() =>
        new(Root + "/godot", new AssetProjectLayout(Root));

    [Test]
    public async Task a_resource_path_resolves_under_the_godot_root()
    {
        await Assert.That(Coincident().FromResourcePath("res://scenes/pool.tscn"))
            .IsEqualTo((UPath)"/repo/Pingu/scenes/pool.tscn");
        await Assert.That(Nested().FromResourcePath("res://scenes/pool.tscn"))
            .IsEqualTo((UPath)"/repo/Pingu/godot/scenes/pool.tscn");
    }

    /// <summary>Godot spells its own root <c>res://</c>, and the addon opens documents by handing
    /// that back — so the degenerate case has to round-trip rather than produce "res://".</summary>
    [Test]
    public async Task the_bare_resource_scheme_is_the_godot_root()
    {
        await Assert.That(Coincident().FromResourcePath("res://")).IsEqualTo((UPath)Root);
        await Assert.That(Coincident().ToResourcePath(Root)).IsEqualTo("res://");
    }

    [Test]
    public async Task a_physical_path_inside_the_godot_project_gets_a_resource_path()
    {
        await Assert.That(Coincident().ToResourcePath("/repo/Pingu/.editor/tscn/scenes/pool.tscn"))
            .IsEqualTo("res://.editor/tscn/scenes/pool.tscn");
    }

    /// <summary>Outside the Godot project there is no res:// name at all, and inventing one would
    /// hand Godot a path that resolves somewhere else entirely.</summary>
    [Test]
    public async Task a_physical_path_outside_the_godot_project_has_no_resource_path()
    {
        await Assert.That(Coincident().ToResourcePath("/repo/Elsewhere/thing.glb")).IsNull();
        await Assert.That(Nested().ToResourcePath("/repo/Pingu/assets/penguins/adelie.glb")).IsNull();
    }

    [Test]
    public async Task an_asset_gets_the_authoring_path_a_reference_carries()
    {
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/assets/penguins/adelie.glb"))
            .IsEqualTo("penguins/adelie.glb");
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/assets/project.toml"))
            .IsEqualTo("project.toml");
    }

    /// <summary>A file in the Godot project but not under <c>assets/</c> cannot be referenced by a
    /// document: it is not a source the build knows about.</summary>
    [Test]
    public async Task a_file_outside_assets_has_no_authoring_path()
    {
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/scenes/pool.tscn")).IsNull();
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/.editor/tscn/pool.tscn")).IsNull();
        await Assert.That(Coincident().ToAssetMountPath("/repo/Pingu/scenes/pool.tscn")).IsNull();
    }

    /// <summary>The mount path is what everything downstream reads through, so it must be rooted
    /// at the mount name rather than at the host's directory.</summary>
    [Test]
    public async Task an_asset_gets_a_path_under_the_assets_mount()
    {
        await Assert.That(Coincident().ToAssetMountPath("/repo/Pingu/assets/scenes/pool.scene.toml"))
            .IsEqualTo((UPath)"/assets/scenes/pool.scene.toml");
    }

    [Test]
    public async Task an_authoring_path_resolves_back_to_where_the_asset_is()
    {
        var paths = Coincident();
        await Assert.That(paths.FromAssetReferencePath("penguins/adelie.glb"))
            .IsEqualTo((UPath)"/repo/Pingu/assets/penguins/adelie.glb");
        await Assert.That(paths.ToAssetReferencePath(paths.FromAssetReferencePath("materials/water.toml")))
            .IsEqualTo("materials/water.toml");
    }

    /// <summary>What a picker hands over is a res:// path and what a document needs is an
    /// authoring one; this composition is the whole journey.</summary>
    [Test]
    public async Task a_picked_resource_becomes_the_reference_a_document_stores()
    {
        var paths = Coincident();
        await Assert
            .That(paths.ToAssetReferencePath(paths.FromResourcePath("res://assets/penguins/adelie.glb")))
            .IsEqualTo("penguins/adelie.glb");
    }

    [Test]
    public async Task a_path_that_is_not_a_resource_path_is_refused()
    {
        await Assert.That(() => Coincident().FromResourcePath("/repo/Pingu/scenes/pool.tscn"))
            .Throws<ArgumentException>();
        await Assert.That(() => Coincident().FromResourcePath("user://save.dat"))
            .Throws<ArgumentException>();
    }

    /// <summary>A relative root names nothing in the file system it is resolved against, and would
    /// fail later and further away.</summary>
    [Test]
    public async Task a_relative_godot_root_is_refused()
    {
        await Assert.That(() => new AssetProjectPaths("Pingu", new AssetProjectLayout(Root)))
            .Throws<ArgumentException>();
    }

    /// <summary>The workfile mirrors the document's place under assets/, so two documents with the
    /// same basename in different folders cannot collide on one cache file.</summary>
    [Test]
    public async Task a_document_gets_a_workfile_mirroring_its_path()
    {
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/assets/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/tscn/scenes/pool.tscn");
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/assets/props/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/tscn/props/scenes/pool.tscn");
    }

    /// <summary>The workfile follows the ASSET project, not the Godot one — they are allowed to
    /// differ, and the cache belongs to the thing being edited.</summary>
    [Test]
    public async Task the_workfile_lives_under_the_asset_projects_editor_directory()
    {
        await Assert.That(Nested().WorkfileFor("/repo/Pingu/assets/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/tscn/scenes/pool.tscn");
    }

    [Test]
    public async Task a_file_outside_assets_gets_no_workfile()
    {
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/scenes/pool.prefab")).IsNull();
        await Assert.That(Coincident().WorkfileFor("/repo/Elsewhere/pool.prefab")).IsNull();
    }

    /// <summary>The play tree mirrors assets/, and a document keeps its .prefab name there — the
    /// runtime dispatches on extension, so a Play button still names a file that exists.</summary>
    [Test]
    public async Task a_document_maps_to_its_built_form_in_the_play_tree()
    {
        await Assert.That(Coincident().PlayPathFor("/repo/Pingu/assets/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/play/scenes/pool.prefab");
    }

    [Test]
    public async Task a_file_outside_assets_has_no_built_form()
    {
        await Assert.That(Coincident().PlayPathFor("/repo/Pingu/scenes/pool.prefab")).IsNull();
    }

    [Test]
    public async Task the_scheme_is_recognised_by_spelling_alone()
    {
        await Assert.That(AssetProjectPaths.IsResourcePath("res://x")).IsTrue();
        await Assert.That(AssetProjectPaths.IsResourcePath("user://x")).IsFalse();
        await Assert.That(AssetProjectPaths.IsResourcePath(null)).IsFalse();
    }
}
