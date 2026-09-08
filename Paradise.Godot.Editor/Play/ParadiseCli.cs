#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace ParadiseGodot.Play
{
    /// <summary>Find and run the CLI; it owns asset builds, launcher builds, and Play.</summary>
    /// <remarks>GUI-launched editors may lack a shell PATH. Keep the environment unchanged;
    /// the CLI locates its own SDK.</remarks>
    public sealed class ParadiseCli
    {
        /// <summary>Machine-level EditorSettings override for the CLI path.</summary>
        public const string CliPathSetting = "paradise/tools/cli_path";

        /// <summary>Machine-level EditorSettings key for exported NAME=VALUE pairs.</summary>
        /// <remarks>MSBuild reads these as properties. Set ParadiseUseEngineSource=false when a workspace's
        /// engine source override differs from the game's pinned version.</remarks>
        public const string CliEnvironmentSetting = "paradise/tools/cli_env";

        internal const string MissingCliMessage =
            "No `paradise` CLI found. Install it (`dotnet tool install --global Paradise.Cli`) " +
            "or set its path in Paradise/Settings….";

        /// <summary>CLI exit code for a user stop.</summary>
        public const int Interrupted = 130;

        // Detached Play needs a log because a GUI-launched editor has no console.
        public static string LogPath => Path.Combine(Path.GetTempPath(), "paradise_godot_play.log");

        private long _playPid;

        public bool IsPlaying => _playPid > 0 && OS.IsProcessRunning((int)_playPid);

        /// <summary>Shared CLI cache, with one directory per version.</summary>
        /// <remarks>Installing into the single global-tool slot would change other projects' CLI versions.</remarks>
        public static string ToolCache => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".paradise", "cli");

        private static string ExecutableName => OperatingSystem.IsWindows() ? "paradise.exe" : "paradise";

        /// <summary>Find the CLI by setting, project pin, PATH, then global tool directory; null if absent.</summary>
        /// <param name="projectRoot">Game repository root, or null to skip its pin.</param>
        /// <remarks>The pin precedes PATH to avoid incompatible document formats. A failed fetch warns
        /// and falls through so offline work remains possible; an explicit setting still wins.</remarks>
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

        /// <summary>Fetch the pinned CLI on first use; null for an unreadable pin or failed fetch.</summary>
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

        // Installation blocks once per version per machine; announce it so the editor does not appear hung.
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

        /// <summary>Find dotnet for tool installation without modifying PATH.</summary>
        /// <remarks>Prefer an absolute path: prepending a dotnet directory can make the CLI shim exit silently.
        /// See <see cref="Wrap"/>.</remarks>
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

        /// <summary>Build Play arguments from the source document path; the CLI resolves its built copy.</summary>
        /// <param name="noAssets">Skip the asset build when a watcher owns the play tree, avoiding concurrent writes.</param>
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

        /// <summary>Start detached Play, stopping the previous game to avoid competing builds.</summary>
        /// <param name="documentHostPath">Prefab to play, or null for the manifest's scene or launcher default.</param>
        public bool Play(string projectRoot, string? documentHostPath, IReadOnlyList<string> passthrough, out string? problem)
        {
            Stop();
            if (Find(projectRoot) is not { } cli)
            {
                problem = MissingCliMessage;
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

        public void Stop()
        {
            if (IsPlaying) Terminate(_playPid);
            _playPid = 0;
        }

        // SIGTERM lets the CLI stop its whole process tree and exit Interrupted; SIGKILL orphans children.
        internal static void Terminate(long pid)
        {
            if (OperatingSystem.IsWindows())
            {
                OS.Kill((int)pid);
            }
            else
            {
                OS.Execute("kill", ["-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
        }

        /// <summary>Run a CLI verb from the project root, returning its exit code and output.</summary>
        public static int Run(IReadOnlyList<string> arguments, string projectRoot, out string output, out string? problem)
        {
            if (Find(projectRoot) is not { } cli)
            {
                output = "";
                problem = MissingCliMessage;
                return -1;
            }

            var lines = new global::Godot.Collections.Array();
            var command = PrepareCommand(cli, arguments, projectRoot, logPath: null);
            int code = OS.Execute(command.Executable, command.Arguments, lines, readStderr: true);

            output = string.Join("", lines.Select(line => line.AsString()));
            problem = null;
            return code;
        }

        /// <summary>Start detached and return the CLI pid, or 0. POSIX exec ensures SIGTERM reaches the CLI.</summary>
        public static long LaunchDetached(
            string executable, IReadOnlyList<string> arguments, string workingDirectory, string logPath)
        {
            var command = PrepareCommand(executable, arguments, workingDirectory, logPath);
            return OS.CreateProcess(command.Executable, command.Arguments);
        }

        private static (string Executable, string[] Arguments) PrepareCommand(
            string executable, IReadOnlyList<string> arguments, string workingDirectory, string? logPath)
        {
            if (OperatingSystem.IsWindows())
            {
                return (executable, [.. arguments]);
            }

            return ("/bin/sh", ["-c", Wrap(executable, arguments, workingDirectory, logPath, Environment())]);
        }

        /// <summary>Wrap a POSIX launch with exec so Stop signals the CLI directly.</summary>
        /// <remarks>
        /// <para>Keep PATH unchanged: prepending /usr/local/share/dotnet made the arm64 tool shim
        /// exit 0 without output in the editor. The CLI locates its own SDK.</para>
        /// <para>Disable the MSBuild server to avoid MSB0001 when Play and assets watch build concurrently.</para>
        /// </remarks>
        public static string Wrap(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            string? logPath,
            IReadOnlyList<string>? environment = null)
        {
            string redirect = logPath is null ? "" : $" > {ShellQuote(logPath)} 2>&1";
            string exports = string.Concat(
                (environment ?? []).Where(IsAssignment).Select(pair => $"export {ShellQuote(pair)}; "));
            return
                $"cd {ShellQuote(workingDirectory)} && export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1; {exports}" +
                $"exec {ShellQuote(executable)}{string.Concat(arguments.Select(a => " " + ShellQuote(a)))}{redirect}";
        }

        public static IReadOnlyList<string> Environment() =>
            ParadiseSettingsDialog.TokenizeArguments(ParadiseSettingsDialog.ReadSetting(CliEnvironmentSetting));

        // Exporting a non-assignment would fail the shell launch.
        private static bool IsAssignment(string pair) => pair.IndexOf('=') > 0;

        // Single quotes preserve each token verbatim; splice embedded quotes outside them.
        private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";
    }
}
#endif
