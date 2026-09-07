using Paradise.Assets.Project;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;
using Zio;
using Zio.FileSystems;

namespace ParadiseGodot.Tests;

/// <summary>
/// What a whole-project conversion walks, and what it decides is stale.
/// </summary>
public class ProjectMirrorTests
{
    private const string Root = "/repo/Pingu";

    private static AssetProjectPaths Paths() =>
        new(Root, new AssetProjectLayout(Root));

    private static MemoryFileSystem Project(params string[] assetPaths)
    {
        var fs = new MemoryFileSystem();
        foreach (var path in assetPaths)
        {
            var full = (UPath)(Root + "/assets/" + path);
            var directory = full.GetDirectory();
            if (!directory.IsNull && !fs.DirectoryExists(directory)) fs.CreateDirectory(directory);
            fs.WriteAllText(full, "x");
        }
        return fs;
    }

    [Test]
    public async Task documents_map_to_working_files_under_editor_godot()
    {
        using var fs = Project("scenes/pool.prefab", "models/cube.prefab");

        var documents = ProjectMirror.Documents(fs, new AssetProjectLayout(Root), Paths());

        await Assert.That(documents.Select(d => d.Mirror.FullName)).IsEquivalentTo(
        [
            $"{Root}/.editor/godot/scenes/pool.tscn",
            $"{Root}/.editor/godot/models/cube.tscn",
        ]);
    }

    [Test]
    public async Task models_map_to_scenes_keeping_their_place_in_the_tree()
    {
        using var fs = Project("penguins/adelie.glb", "models/primitives/cube.glb");

        var models = ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths());

        await Assert.That(models.Select(m => m.Mirror.FullName)).IsEquivalentTo(
        [
            $"{Root}/.editor/godot/penguins/adelie.glb.scn",
            $"{Root}/.editor/godot/models/primitives/cube.glb.scn",
        ]);
    }

    [Test]
    public async Task only_glb_is_mirrored()
    {
        // The engine refuses .gltf by name, so a mirror of one would be a scene for a model no
        // build will ever ship. The documents and sidecars beside it are not models at all.
        using var fs = Project("models/cube.glb", "models/cube.gltf", "models/cube.mesh", "models/cube.glb.meta");

        var models = ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths());

        await Assert.That(models.Select(m => m.Source.GetName())).IsEquivalentTo(["cube.glb"]);
    }

    [Test]
    public async Task an_unbuilt_mirror_is_stale()
    {
        using var fs = Project("models/cube.glb");

        var models = ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths());

        await Assert.That(models.Single().Stale).IsTrue();
    }

    [Test]
    public async Task a_stamped_mirror_is_current_until_its_source_moves()
    {
        using var fs = Project("models/cube.glb");
        var source = (UPath)$"{Root}/assets/models/cube.glb";
        var mirror = Paths().MirrorModelFor(source)!.Value;
        fs.CreateDirectory(mirror.GetDirectory());
        fs.WriteAllText(mirror, "scene");
        WorkfileStamp.Write(fs, mirror, source);

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths()).Single().Stale)
            .IsFalse();

        // Rewriting the GLB moves its length, which is half the stamp.
        fs.WriteAllText(source, "a longer model than before");

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths()).Single().Stale)
            .IsTrue();
    }

    [Test]
    public async Task a_missing_output_is_stale_however_recent_its_stamp()
    {
        // The stamp says what the source looked like, not that the output survived: someone
        // deleting .editor/ must get a rebuild, not a claim that everything is current.
        using var fs = Project("models/cube.glb");
        var source = (UPath)$"{Root}/assets/models/cube.glb";
        var mirror = Paths().MirrorModelFor(source)!.Value;
        WorkfileStamp.Write(fs, mirror, source);

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths()).Single().Stale)
            .IsTrue();
    }

    [Test]
    public async Task a_project_with_no_assets_directory_walks_to_nothing()
    {
        using var fs = new MemoryFileSystem();

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths())).IsEmpty();
    }
}
