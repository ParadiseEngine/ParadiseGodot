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
    /// it. The CLI is found by a ladder — the setting, PATH, the global tool directory — and is
    /// handed the environment as it is: it locates the SDK itself.
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

        /// <summary>The CLI executable: the setting, else PATH, else the global dotnet tool. Null
        /// when none is found.</summary>
        public static string? Find()
        {
            string configured = ParadiseSettingsDialog.ReadSetting(CliPathSetting);
            if (configured.Length > 0) return File.Exists(configured) ? configured : null;

            string name = OperatingSystem.IsWindows() ? "paradise.exe" : "paradise";
            foreach (string directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (directory.Length == 0) continue;
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }

            string tool = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".dotnet", "tools", name);
            return File.Exists(tool) ? tool : null;
        }

        /// <summary>The <c>host play</c> argument list for one document. Pure, so it can be
        /// tested: the DOCUMENT's path, never its built twin — the CLI knows the play tree's
        /// layout and this addon need not.</summary>
        public static IReadOnlyList<string> PlayArguments(
            string projectRoot, string? documentHostPath, IReadOnlyList<string> passthrough)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);
            ArgumentNullException.ThrowIfNull(passthrough);

            var arguments = new List<string> { "host", "play", "--project", projectRoot };
            if (documentHostPath is not null)
            {
                arguments.Add("--scene");
                arguments.Add(documentHostPath);
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
            if (Find() is not { } cli)
            {
                problem =
                    "No `paradise` CLI found. Install it (`dotnet tool install --global Paradise.Cli`) " +
                    "or set its path in Paradise/Settings….";
                return false;
            }

            _playPid = Launch(cli, PlayArguments(projectRoot, documentHostPath, passthrough), projectRoot);
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
            if (Find() is not { } cli)
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

        private static long Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            if (OperatingSystem.IsWindows())
            {
                return OS.CreateProcess(executable, [.. arguments]);
            }

            return OS.CreateProcess("/bin/sh", ["-c", Wrap(executable, arguments, workingDirectory, LogPath)]);
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
