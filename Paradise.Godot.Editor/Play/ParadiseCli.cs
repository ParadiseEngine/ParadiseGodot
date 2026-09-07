#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace ParadiseGodot.Play
{
    /// <summary>
    /// The <c>paradise</c> CLI as the editor drives it: found, run detached for Play, run to
    /// completion for a verb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The addon builds nothing itself — not assets, not the launcher, not the game.
    /// <c>paradise host play</c> does all three behind a freshness gate (engine #259) and the
    /// Blender addon already goes through it; a second launch path here would be a second answer
    /// to "what does Play run". The addon's part is to say WHICH document, hand the process to the
    /// author with a Stop and a log, and get out of the way.
    /// </para>
    /// <para>
    /// A GUI-launched editor inherits no shell PATH on macOS, so <c>~/.dotnet/tools</c> is not on
    /// it. The CLI is found by a ladder — the setting, the version the project PINS, PATH, the
    /// global tool directory — and is handed the environment as it is: it locates the SDK itself.
    /// </para>
    /// </remarks>
    public sealed class ParadiseCli
    {
        /// <summary>Machine-level EditorSettings key: an explicit CLI path, for a machine where
        /// it is neither on PATH nor installed as a global tool.</summary>
        public const string CliPathSetting = "paradise/tools/cli_path";

        /// <summary>What <c>paradise host play</c> exits with when it was stopped — not a failure.</summary>
        public const int Interrupted = 130;

        /// <summary>Where a detached Play's output goes: a GUI-launched process has no console,
        /// and a build error that vanished would be the failure an author sees.</summary>
        public static string LogPath => Path.Combine(Path.GetTempPath(), "paradise_godot_play.log");

        private long _playPid;

        /// <summary>Whether the game this session launched is still running.</summary>
        public bool IsPlaying => _playPid > 0 && OS.IsProcessRunning((int)_playPid);

        /// <summary>Where a version-pinned CLI is kept: one directory per version, shared by every
        /// project on this machine.</summary>
        /// <remarks>Deliberately NOT the global tool (<c>~/.dotnet/tools</c>). That is a single
        /// slot, so installing into it for one project silently changes which CLI every other
        /// project on the machine gets.</remarks>
        public static string ToolCache => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".paradise", "cli");

        private static string ExecutableName => OperatingSystem.IsWindows() ? "paradise.exe" : "paradise";

        /// <summary>
        /// The CLI executable: the setting, else the version this project pins, else PATH, else
        /// the global dotnet tool. Null when none is found.
        /// </summary>
        /// <param name="projectRoot">The game's repository root, or null to skip the pinned rung
        /// (a caller that has no project cannot know which version it would want).</param>
        /// <remarks>
        /// The pinned rung sits ABOVE PATH because a CLI older than the tree writes documents the
        /// runtime cannot read, and the one on PATH is whatever was installed last for whatever
        /// project. The configured setting still wins, so pointing the addon at a source build
        /// stays possible; a pinned version that cannot be fetched warns and falls through rather
        /// than stopping work offline.
        /// </remarks>
        public static string? Find(string? projectRoot = null)
        {
            string configured = ParadiseSettingsDialog.ReadSetting(CliPathSetting);
            if (configured.Length > 0) return File.Exists(configured) ? configured : null;

            if (projectRoot is not null && PinnedCli(projectRoot) is { } pinned) return pinned;

            foreach (string directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (directory.Length == 0) continue;
                string candidate = Path.Combine(directory, ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }

            string tool = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".dotnet", "tools", ExecutableName);
            return File.Exists(tool) ? tool : null;
        }

        /// <summary>The CLI at exactly the version the project pins, fetching it on first use.
        /// Null when the project pins nothing readable, or the fetch failed.</summary>
        private static string? PinnedCli(string projectRoot)
        {
            string? version;
            try
            {
                using var files = new Zio.FileSystems.PhysicalFileSystem();
                version = ProjectEngineVersion.Of(files, files.ConvertPathFromInternal(projectRoot));
            }
            catch (Exception failure) when (failure is IOException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            if (version is null) return null;

            string executable = Path.Combine(ToolCache, version, ExecutableName);
            if (File.Exists(executable)) return executable;

            return Install(version, executable) ? executable : null;
        }

        /// <summary>
        /// Fetch one version into its own directory under <see cref="ToolCache"/>.
        /// </summary>
        /// <remarks>This blocks the editor, once per version per machine, which is the price of
        /// running the CLI the project actually pins. It is announced before and after, because a
        /// silent multi-second stall on opening a document reads as a hang.</remarks>
        private static bool Install(string version, string executable)
        {
            GD.Print($"[Paradise] Fetching the CLI this project pins (Paradise.Cli {version}) — this happens once.");

            var lines = new global::Godot.Collections.Array();
            int code = OS.Execute(
                FindDotnet(),
                ["tool", "install", "Paradise.Cli", "--version", version, "--tool-path", Path.Combine(ToolCache, version)],
                lines,
                readStderr: true);

            if (code == 0 && File.Exists(executable))
            {
                GD.Print($"[Paradise] Paradise.Cli {version} is at {executable}.");
                return true;
            }

            GD.PushWarning(
                $"[Paradise] Could not fetch Paradise.Cli {version} — falling back to whatever is installed, which " +
                $"may write documents this project cannot read. Install it by hand with " +
                $"`dotnet tool install Paradise.Cli --version {version} --tool-path {Path.Combine(ToolCache, version)}`. " +
                string.Join("", lines.Select(line => line.AsString())));
            return false;
        }

        /// <summary>
        /// A <c>dotnet</c> to run the fetch with.
        /// </summary>
        /// <remarks>By absolute path wherever possible, and PATH is never modified: a
        /// GUI-launched editor inherits no shell PATH on macOS, and prepending a dotnet directory
        /// is what made the <c>paradise</c> tool shim exit 0 with no output (see
        /// <see cref="Wrap"/>). Only the fetch needs dotnet at all — the CLI itself locates its
        /// own SDK.</remarks>
        public static string FindDotnet()
        {
            if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            {
                string rooted = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(rooted)) return rooted;
            }

            foreach (string candidate in new[]
            {
                Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet"),
                "/usr/local/share/dotnet/dotnet",
                "/usr/local/bin/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/bin/dotnet",
                "/usr/share/dotnet/dotnet",
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            return "dotnet";
        }

        /// <summary>The <c>host play</c> argument list for one document. Pure, so it can be
        /// tested: the DOCUMENT's path, never its built twin — the CLI knows the play tree's
        /// layout and this addon need not.</summary>
        /// <param name="noAssets">Skip the CLI's own asset build. Passed when a watcher is live
        /// for this project: <c>host play</c> otherwise runs a full <c>--editor</c> build into
        /// the very tree the watch is rebuilding, and two builders writing one tree at once is
        /// what the watch's single-drainer rule exists to prevent. The engine's own tray passes
        /// it for the same reason.</param>
        public static IReadOnlyList<string> PlayArguments(
            string projectRoot, string? documentHostPath, IReadOnlyList<string> passthrough, bool noAssets = false)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);
            ArgumentNullException.ThrowIfNull(passthrough);

            var arguments = new List<string> { "host", "play", "--project", projectRoot };
            if (documentHostPath is not null)
            {
                arguments.Add("--scene");
                arguments.Add(documentHostPath);
            }
            if (noAssets)
            {
                arguments.Add("--no-assets");
            }
            if (passthrough.Count > 0)
            {
                arguments.Add("--");
                arguments.AddRange(passthrough);
            }
            return arguments;
        }

        /// <summary>
        /// Run the document's game through <c>paradise host play</c>, detached. One at a time:
        /// a running game is stopped first, as the Blender addon does, because two launchers on one
        /// play tree fight over the build.
        /// </summary>
        /// <param name="documentHostPath">The <c>.prefab</c> to play, or null for the manifest's
        /// <c>[host] scene</c> (or the launcher's own default).</param>
        public bool Play(string projectRoot, string? documentHostPath, IReadOnlyList<string> passthrough, out string? problem)
        {
            Stop();
            if (Find(projectRoot) is not { } cli)
            {
                problem =
                    "No `paradise` CLI found. Install it (`dotnet tool install --global Paradise.Cli`) " +
                    "or set its path in Paradise/Settings….";
                return false;
            }

            _playPid = LaunchDetached(
                cli,
                PlayArguments(projectRoot, documentHostPath, passthrough, WatchSession.IsWatching(projectRoot)),
                projectRoot,
                LogPath);
            problem = _playPid > 0 ? null : $"'{cli}' did not start — see {LogPath}.";
            return _playPid > 0;
        }

        /// <summary>Stop the running game. SIGTERM rather than SIGKILL where there is a choice:
        /// the CLI turns a TERM into a kill of the whole tree it started — a dotnet watch, the
        /// game — and exits <see cref="Interrupted"/>; a KILL would orphan them.</summary>
        public void Stop()
        {
            if (!IsPlaying)
            {
                _playPid = 0;
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                OS.Kill((int)_playPid);
            }
            else
            {
                OS.Execute("kill", ["-TERM", _playPid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            _playPid = 0;
        }

        /// <summary>Run a CLI verb to completion, from the project root. The exit code, and every
        /// line it printed.</summary>
        public static int Run(IReadOnlyList<string> arguments, string projectRoot, out string output, out string? problem)
        {
            if (Find(projectRoot) is not { } cli)
            {
                output = "";
                problem =
                    "No `paradise` CLI found. Install it (`dotnet tool install --global Paradise.Cli`) " +
                    "or set its path in Paradise/Settings….";
                return -1;
            }

            var lines = new global::Godot.Collections.Array();
            int code;
            if (OperatingSystem.IsWindows())
            {
                code = OS.Execute(cli, [.. arguments], lines, readStderr: true);
            }
            else
            {
                code = OS.Execute("/bin/sh", ["-c", Wrap(cli, arguments, projectRoot, logPath: null)], lines, readStderr: true);
            }

            output = string.Join("", lines.Select(line => line.AsString()));
            problem = null;
            return code;
        }

        /// <summary>Start a process detached and return its pid, or 0. The pid is the CLI's own
        /// (POSIX <c>exec</c>s the shell away), so a later SIGTERM reaches it.</summary>
        public static long LaunchDetached(
            string executable, IReadOnlyList<string> arguments, string workingDirectory, string logPath)
        {
            if (OperatingSystem.IsWindows())
            {
                return OS.CreateProcess(executable, [.. arguments]);
            }

            return OS.CreateProcess("/bin/sh", ["-c", Wrap(executable, arguments, workingDirectory, logPath)]);
        }

        /// <summary>
        /// The POSIX launch as one shell line: the working directory, the MSBuild server off,
        /// output to the log, then <c>exec</c> so the shell's pid IS the child's and a Stop
        /// reaches it. Pure, and tested.
        /// </summary>
        /// <remarks>
        /// <para>
        /// PATH is left alone. Prepending a dotnet directory — what the previous Play did for
        /// <c>dotnet run</c> — makes the <c>paradise</c> tool shim exit 0 with no output under the
        /// editor's environment (measured: <c>/usr/local/share/dotnet</c> first on PATH, and the
        /// arm64 tool says nothing). The CLI finds its own SDK by PATH, DOTNET_ROOT or the
        /// installer directories, so it needs nothing from here.
        /// </para>
        /// <para>The MSBuild server is switched off for the reason the Blender addon found: two
        /// <c>dotnet</c> builds sharing one die on MSB0001, which is what Play looked like beside a
        /// live <c>paradise assets watch</c>.</para>
        /// </remarks>
        public static string Wrap(string executable, IReadOnlyList<string> arguments, string workingDirectory, string? logPath)
        {
            string redirect = logPath is null ? "" : $" > {ShellQuote(logPath)} 2>&1";
            return
                $"cd {ShellQuote(workingDirectory)} && export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1; " +
                $"exec {ShellQuote(executable)}{string.Concat(arguments.Select(a => " " + ShellQuote(a)))}{redirect}";
        }

        // POSIX single-quote wrapping: every token becomes one word verbatim, whatever it
        // contains ('...' with embedded quotes spliced as '\'' ).
        private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";
    }
}
#endif
