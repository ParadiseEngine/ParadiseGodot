using Paradise.Assets.Project;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;
using Zio;
using Zio.FileSystems;

namespace ParadiseGodot.Tests;

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
            fs.CreateDirectory(full.GetDirectory());
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
        // The engine rejects .gltf; documents and sidecars are not source models.
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

        // Length is part of the source stamp.
        fs.WriteAllText(source, "a longer model than before");

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths()).Single().Stale)
            .IsTrue();
    }

    [Test]
    public async Task a_missing_output_is_stale_however_recent_its_stamp()
    {
        using var fs = Project("models/cube.glb");
        var source = (UPath)$"{Root}/assets/models/cube.glb";
        var mirror = Paths().MirrorModelFor(source)!.Value;
        WorkfileStamp.Write(fs, mirror, source);

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths()).Single().Stale)
            .IsTrue();
    }

    [Test]
    public async Task a_workfile_without_a_stamp_is_stale()
    {
        using var fs = Project("models/cube.glb");
        var source = (UPath)$"{Root}/assets/models/cube.glb";
        var mirror = Paths().MirrorModelFor(source)!.Value;
        fs.CreateDirectory(mirror.GetDirectory());
        fs.WriteAllText(mirror, "scene");

        await Assert.That(WorkfileStamp.IsCurrent(fs, mirror, source)).IsFalse();
    }

    [Test]
    public async Task clearing_a_stamp_twice_leaves_the_workfile_intact_and_stale()
    {
        using var fs = Project("models/cube.glb");
        var source = (UPath)$"{Root}/assets/models/cube.glb";
        var mirror = Paths().MirrorModelFor(source)!.Value;
        fs.CreateDirectory(mirror.GetDirectory());
        fs.WriteAllText(mirror, "scene");
        WorkfileStamp.Write(fs, mirror, source);
        await Assert.That(WorkfileStamp.IsCurrent(fs, mirror, source)).IsTrue();

        WorkfileStamp.Clear(fs, mirror);
        WorkfileStamp.Clear(fs, mirror);

        await Assert.That(WorkfileStamp.IsCurrent(fs, mirror, source)).IsFalse();
        await Assert.That(fs.ReadAllText(mirror)).IsEqualTo("scene");
        await Assert.That(fs.ReadAllText(source)).IsEqualTo("x");
    }

    [Test]
    public async Task session_and_workfile_stamps_have_independent_lifetimes()
    {
        using var fs = Project("scenes/main.prefab");
        var source = (UPath)$"{Root}/assets/scenes/main.prefab";
        var workfile = Paths().WorkfileFor(source)!.Value;
        string session = Guid.NewGuid().ToString();
        fs.CreateDirectory(workfile.GetDirectory());
        fs.WriteAllText(workfile, "scene");

        DocumentSession.Restamp(fs, source, session);
        WorkfileStamp.Write(fs, workfile, source);
        await Assert.That(DocumentSession.IsUnchanged(fs, source, session)).IsTrue();
        await Assert.That(WorkfileStamp.IsCurrent(fs, workfile, source)).IsTrue();

        fs.WriteAllText(source, "changed document");
        await Assert.That(DocumentSession.IsUnchanged(fs, source, session)).IsFalse();
        await Assert.That(WorkfileStamp.IsCurrent(fs, workfile, source)).IsFalse();

        DocumentSession.Restamp(fs, source, session);
        await Assert.That(DocumentSession.IsUnchanged(fs, source, session)).IsTrue();
        await Assert.That(WorkfileStamp.IsCurrent(fs, workfile, source)).IsFalse();

        WorkfileStamp.Write(fs, workfile, source);
        WorkfileStamp.Clear(fs, workfile);
        await Assert.That(DocumentSession.IsUnchanged(fs, source, session)).IsTrue();
        await Assert.That(WorkfileStamp.IsCurrent(fs, workfile, source)).IsFalse();
    }

    [Test]
    public async Task a_project_with_no_assets_directory_walks_to_nothing()
    {
        using var fs = new MemoryFileSystem();

        await Assert.That(ProjectMirror.Models(fs, new AssetProjectLayout(Root), Paths())).IsEmpty();
    }
}
