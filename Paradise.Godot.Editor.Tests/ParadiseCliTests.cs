using ParadiseGodot.Play;

namespace Paradise.Godot.Editor.Tests;

public class ParadiseCliTests
{
    [Test]
    [Arguments("", new string[0])]
    [Arguments("  \t ", new string[0])]
    [Arguments("\"\"", new[] { "" })]
    [Arguments("--name \"ice ledge\"", new[] { "--name", "ice ledge" })]
    [Arguments("\" \" \"\" tail", new[] { " ", "", "tail" })]
    [Arguments("prefix\" with space\"", new[] { "prefix with space" })]
    public async Task launcher_arguments_preserve_quoted_tokens(string text, string[] expected)
    {
        await Assert.That(ParadiseGodot.ParadiseSettingsDialog.TokenizeArguments(text))
            .IsEquivalentTo(expected);
    }

    [Test]
    public async Task play_names_the_project_and_the_document()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", "/repo/Pingu/assets/scenes/pool.prefab", []);

        await Assert.That(arguments).IsEquivalentTo(
            new[] { "host", "play", "--project", "/repo/Pingu", "--scene", "/repo/Pingu/assets/scenes/pool.prefab" });
    }

    [Test]
    public async Task play_without_a_document_leaves_the_scene_to_the_manifest()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", null, []);

        await Assert.That(arguments).IsEquivalentTo(new[] { "host", "play", "--project", "/repo/Pingu" });
    }

    [Test]
    public async Task the_authors_arguments_pass_through_after_the_separator()
    {
        var arguments = ParadiseCli.PlayArguments("/repo/Pingu", "/repo/Pingu/assets/scenes/pool.prefab", ["--fov", "60"]);

        await Assert.That(arguments.TakeLast(3)).IsEquivalentTo(new[] { "--", "--fov", "60" });
    }

    // exec gives the CLI the shell's pid so Stop reaches it.
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
        // A live watcher owns the play tree; host play must not build into it concurrently.
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
        var one = WatchSession.LogPathFor("/repo/Pingu");
        var other = WatchSession.LogPathFor("/repo/ShiningPie");

        await Assert.That(one).IsNotEqualTo(other);
        await Assert.That(one).Contains("Pingu");
    }

    [Test]
    public async Task the_watch_log_is_stable_across_sessions()
    {
        // GetHashCode is randomized per process and would change log paths across sessions.
        await Assert.That(WatchSession.LogPathFor("/repo/Pingu"))
            .IsEqualTo(WatchSession.LogPathFor("/repo/Pingu/"));
    }

    [Test]
    public async Task a_missing_watch_log_has_no_error()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        await Assert.That(WatchSession.LastError(projectRoot)).IsNull();
    }

    [Test]
    [Arguments("", null)]
    [Arguments("error: old failure\nwatch: rebuilt 1 asset\n\n", null)]
    [Arguments("watch: rebuilt 1 asset\nerror: invalid mesh\nbuild FAILED with 1 error(s)\n\n", "error: invalid mesh")]
    public async Task watch_errors_follow_the_latest_rebuild(string contents, string? expected)
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string log = WatchSession.LogPathFor(projectRoot);
        try
        {
            File.WriteAllText(log, contents);

            await Assert.That(WatchSession.LastError(projectRoot)).IsEqualTo(expected);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Test]
    public async Task the_configured_environment_is_exported_before_the_cli_runs()
    {
        // MSBuild reads environment variables as properties, including in desktop-launched editors.
        var line = ParadiseCli.Wrap("/bin/paradise", ["host", "build"], "/repo", logPath: null,
            ["ParadiseUseEngineSource=false"]);

        await Assert.That(line).Contains("export 'ParadiseUseEngineSource=false';");
        await Assert.That(line.IndexOf("export 'Paradise", System.StringComparison.Ordinal))
            .IsLessThan(line.IndexOf("exec ", System.StringComparison.Ordinal));
    }

    [Test]
    public async Task a_token_that_is_not_an_assignment_is_left_out()
    {
        // Exporting a non-assignment would fail the shell launch.
        var line = ParadiseCli.Wrap("/bin/paradise", ["host", "build"], "/repo", logPath: null,
            ["nonsense", "=novalue", "GOOD=1"]);

        await Assert.That(line).Contains("export 'GOOD=1';");
        await Assert.That(line).DoesNotContain("nonsense");
        await Assert.That(line).DoesNotContain("=novalue");
    }

    [Test]
    public async Task an_environment_value_may_contain_spaces()
    {
        var line = ParadiseCli.Wrap("/bin/paradise", ["host", "build"], "/repo", logPath: null,
            ["PARADISE_KTX_PATH=/opt/my tools/ktx"]);

        await Assert.That(line).Contains("export 'PARADISE_KTX_PATH=/opt/my tools/ktx';");
    }
}
