using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;

namespace Paradise.Godot.Editor.Tests;

public class AssetProjectPathsTests
{
    private const string Root = "/repo/Pingu";

    private static AssetProjectPaths Coincident() =>
        new(Root, new AssetProjectLayout(Root));

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

    [Test]
    public async Task the_bare_resource_scheme_is_the_godot_root()
    {
        await Assert.That(Coincident().FromResourcePath("res://")).IsEqualTo((UPath)Root);
        await Assert.That(Coincident().ToResourcePath(Root)).IsEqualTo("res://");
    }

    [Test]
    public async Task a_physical_path_inside_the_godot_project_gets_a_resource_path()
    {
        await Assert.That(Coincident().ToResourcePath("/repo/Pingu/.editor/godot/scenes/pool.tscn"))
            .IsEqualTo("res://.editor/godot/scenes/pool.tscn");
    }

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

    // The build only accepts references to sources under assets/.
    [Test]
    public async Task a_file_outside_assets_has_no_authoring_path()
    {
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/scenes/pool.tscn")).IsNull();
        await Assert.That(Coincident().ToAssetReferencePath("/repo/Pingu/.editor/godot/pool.tscn")).IsNull();
        await Assert.That(Coincident().ToAssetMountPath("/repo/Pingu/scenes/pool.tscn")).IsNull();
    }

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

    [Test]
    public async Task a_relative_godot_root_is_refused()
    {
        await Assert.That(() => new AssetProjectPaths("Pingu", new AssetProjectLayout(Root)))
            .Throws<ArgumentException>();
    }

    // Preserve subdirectories so documents with the same basename have separate caches.
    [Test]
    public async Task a_document_gets_a_workfile_mirroring_its_path()
    {
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/assets/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/godot/scenes/pool.tscn");
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/assets/props/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/godot/props/scenes/pool.tscn");
    }

    [Test]
    public async Task the_workfile_lives_under_the_asset_projects_editor_directory()
    {
        await Assert.That(Nested().WorkfileFor("/repo/Pingu/assets/scenes/pool.prefab"))
            .IsEqualTo((UPath)"/repo/Pingu/.editor/godot/scenes/pool.tscn");
    }

    [Test]
    public async Task a_file_outside_assets_gets_no_workfile()
    {
        await Assert.That(Coincident().WorkfileFor("/repo/Pingu/scenes/pool.prefab")).IsNull();
        await Assert.That(Coincident().WorkfileFor("/repo/Elsewhere/pool.prefab")).IsNull();
    }

    // The runtime dispatches on extension, so built documents retain .prefab.
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
