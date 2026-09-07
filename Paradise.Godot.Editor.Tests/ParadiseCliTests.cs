using ParadiseGodot.Play;

namespace Paradise.Godot.Editor.Tests;

/// <summary>
/// What Play hands to <c>paradise host play</c>. The document, never its built twin: the CLI
/// owns the play tree's layout, and a Play that spelled it would break the day it changed.
/// </summary>
public class ParadiseCliTests
{
    [Test]
    public async Task play_names_the_project_and_the_document()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", "/repo/Pingu/assets/scenes/pool.prefab", []);

        await Assert.That(arguments).IsEquivalentTo(
            new[] { "host", "play", "--project", "/repo/Pingu", "--scene", "/repo/Pingu/assets/scenes/pool.prefab" });
    }

    /// <summary>No document means the manifest's <c>[host] scene</c> decides, which is the
    /// CLI's rule and not one to duplicate here.</summary>
    [Test]
    public async Task play_without_a_document_leaves_the_scene_to_the_manifest()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", null, []);

        await Assert.That(arguments).IsEquivalentTo(new[] { "host", "play", "--project", "/repo/Pingu" });
    }

    /// <summary>The author's own arguments reach the LAUNCHER, so they travel after the
    /// separator the CLI stops parsing at.</summary>
    [Test]
    public async Task the_authors_arguments_pass_through_after_the_separator()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", "/repo/Pingu/assets/scenes/pool.prefab", ["--fov", "60"]);

        await Assert.That(arguments.TakeLast(3)).IsEquivalentTo(new[] { "--", "--fov", "60" });
    }

    /// <summary>The whole POSIX launch is one shell line, so its quoting is what keeps a path
    /// with a space or a quote in it as one word. <c>exec</c> is what makes the shell's pid the
    /// child's, which is what Stop signals.</summary>
    [Test]
    public async Task the_posix_wrapper_quotes_every_word_and_execs_into_the_child()
    {
        var line = ParadiseCli.Wrap("/Users/me/.dotnet/tools/paradise", ["host", "play", "--scene", "/repo/it's here.prefab"], "/repo/My Game", "/tmp/play.log");

        await Assert.That(line).IsEqualTo(
            "cd '/repo/My Game' && export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1; " +
            "exec '/Users/me/.dotnet/tools/paradise' 'host' 'play' '--scene' '/repo/it'\\''s here.prefab' > '/tmp/play.log' 2>&1");
    }

    [Test]
    public async Task a_verb_run_to_completion_keeps_its_output_on_the_pipe()
    {
        var line = ParadiseCli.Wrap("/usr/local/bin/paradise", ["assets", "extract", "--all"], "/repo", logPath: null);

        await Assert.That(line).DoesNotContain(">");
        await Assert.That(line).EndsWith("exec '/usr/local/bin/paradise' 'assets' 'extract' '--all'");
    }

    [Test]
    public async Task play_leaves_the_asset_build_to_a_live_watcher()
    {
        // host play otherwise runs a full --editor build into the very tree the watch rebuilds,
        // and two builders writing one tree is what the single-drainer rule exists to prevent.
        var arguments = ParadiseCli.PlayArguments("/repo", "/repo/assets/scenes/pool.prefab", [], noAssets: true);

        await Assert.That(arguments).Contains("--no-assets");
    }

    [Test]
    public async Task play_builds_the_assets_when_nothing_is_watching()
    {
        var arguments = ParadiseCli.PlayArguments("/repo", "/repo/assets/scenes/pool.prefab", []);

        await Assert.That(arguments).DoesNotContain("--no-assets");
    }

    [Test]
    public async Task the_watcher_is_told_which_project_and_profile()
    {
        var arguments = WatchSession.WatchArguments("/repo", "dev");

        await Assert.That(arguments).IsEquivalentTo(["assets", "watch", "--project", "/repo", "--profile", "dev"]);
    }

    [Test]
    public async Task an_unset_profile_leaves_the_cli_its_own_default()
    {
        await Assert.That(WatchSession.WatchArguments("/repo", "")).IsEquivalentTo(["assets", "watch", "--project", "/repo"]);
    }

    [Test]
    public async Task the_watch_log_is_per_project()
    {
        // One log per root, or a second project's watcher overwrites the log this one's errors
        // are read from.
        var one = WatchSession.LogPathFor("/repo/Pingu");
        var other = WatchSession.LogPathFor("/repo/ShiningPie");

        await Assert.That(one).IsNotEqualTo(other);
        await Assert.That(one).Contains("Pingu");
    }

    [Test]
    public async Task the_watch_log_is_stable_across_sessions()
    {
        // Hashed with SHA-1 rather than GetHashCode, which is randomized per process: a later
        // session would otherwise read a path nothing ever wrote to.
        await Assert.That(WatchSession.LogPathFor("/repo/Pingu"))
            .IsEqualTo(WatchSession.LogPathFor("/repo/Pingu/"));
    }
}
