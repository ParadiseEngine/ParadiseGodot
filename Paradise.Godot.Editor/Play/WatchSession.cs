#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace ParadiseGodot.Play
{
    /// <summary>
    /// <c>paradise assets watch</c>, supervised by the editor while a document is open: it mints
    /// the sidecars a reference needs, rebuilds the play tree on every change, and carries the
    /// tray the author reads status from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One watcher per project root, as a correctness rule</b> — not a tidiness one. The
    /// engine's <c>AssetWatcher.Drain</c> drives the sidecar maintainer outside its lock with an
    /// unsynchronized quarantine, so two drainers lose the identity a move depends on. Two
    /// watchers also write one play tree at once.
    /// </para>
    /// <para>
    /// The registry is in-process and there is no lock file in the engine, so this cannot see a
    /// watcher another editor started — a Blender host on the same project is the real case.
    /// <see cref="Foreign"/> asks the operating system before starting, which closes the common
    /// case without pretending to be a lock.
    /// </para>
    /// </remarks>
    public static class WatchSession
    {
        /// <summary>Machine-level EditorSettings key: start a watcher when a document is opened.</summary>
        public const string AutoWatchSetting = "paradise/assets/auto_watch";

        /// <summary>Machine-level EditorSettings key: the build profile the watcher rebuilds with.</summary>
        public const string ProfileSetting = "paradise/assets/build_profile";

        // Keyed on the normalized root. Windows and macOS compare paths case-insensitively, and a
        // key that disagreed with the filesystem would let one project hold two watchers.
        private static readonly Dictionary<string, long> Watchers =
            new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        /// <summary>The <c>assets watch</c> argument list. Pure, so it can be tested.</summary>
        /// <remarks>The tray is left ON — it is the whole point of running the watcher from here,
        /// and it is where a failed rebuild surfaces for an author with no console.</remarks>
        public static IReadOnlyList<string> WatchArguments(string projectRoot, string? profile)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            var arguments = new List<string> { "assets", "watch", "--project", projectRoot };
            if (profile is { Length: > 0 })
            {
                arguments.Add("--profile");
                arguments.Add(profile);
            }
            return arguments;
        }

        /// <summary>
        /// Where one project's watcher writes. Per root, because a second project's watcher would
        /// otherwise overwrite the log this one's errors are read from.
        /// </summary>
        /// <remarks>Hashed with SHA-1 rather than <see cref="string.GetHashCode()"/>: string
        /// hashing is randomized per process, so a later session would read a path nothing ever
        /// wrote to and report no errors for a watcher reporting plenty.</remarks>
        public static string LogPathFor(string projectRoot)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            string normalized = Normalize(projectRoot);
            string digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))[..6].ToLowerInvariant();
            string name = new DirectoryInfo(normalized).Name;
            return Path.Combine(Path.GetTempPath(), $"paradise_godot_watch_{name}_{digest}.log");
        }

        /// <summary>Whether this session has a live watcher for that project.</summary>
        public static bool IsWatching(string projectRoot) => Pid(projectRoot) > 0;

        /// <summary>
        /// Start a watcher for the project unless one is already running.
        /// </summary>
        /// <returns>Whether a watcher is running for this project when the call returns — true
        /// includes "one was already up", which is the normal case on the second document.</returns>
        public static bool EnsureFor(string projectRoot, out string? problem)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            if (IsWatching(projectRoot))
            {
                problem = null;
                return true;
            }

            if (Foreign(projectRoot))
            {
                problem =
                    "Another `paradise assets watch` is already running on this project — a Blender host, or a " +
                    "terminal. Leaving it alone: two watchers lose the identities a rename depends on.";
                return false;
            }

            if (ParadiseCli.Find(projectRoot) is not { } cli)
            {
                problem =
                    "No `paradise` CLI found. Install it (`dotnet tool install --global Paradise.Cli`) " +
                    "or set its path in Paradise/Settings….";
                return false;
            }

            string profile = ParadiseSettingsDialog.ReadSetting(ProfileSetting);
            string log = LogPathFor(projectRoot);
            long pid = ParadiseCli.LaunchDetached(cli, WatchArguments(projectRoot, profile), projectRoot, log);
            if (pid <= 0)
            {
                problem = $"'{cli} assets watch' did not start — see {log}.";
                return false;
            }

            Watchers[Normalize(projectRoot)] = pid;
            problem = null;
            return true;
        }

        /// <summary>Stop this session's watcher for one project.</summary>
        public static void StopFor(string projectRoot)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            long pid = Pid(projectRoot);
            Watchers.Remove(Normalize(projectRoot));
            if (pid > 0) Terminate(pid);
        }

        /// <summary>Stop every watcher this session started. Called when the plugin leaves the
        /// tree, or a closing editor leaves one running for the life of the machine.</summary>
        public static void StopAll()
        {
            foreach (long pid in Watchers.Values.ToArray()) Terminate(pid);
            Watchers.Clear();
        }

        /// <summary>
        /// The last rebuild error the watcher reported, or null.
        /// </summary>
        /// <remarks>Read backwards: this log grows all session and the interesting rebuild is the
        /// latest. The <c>build FAILED with N error(s)</c> tally is skipped because it is a count
        /// and not a cause — the line that names a file is the one an author can act on.</remarks>
        public static string? LastError(string projectRoot)
        {
            string log = LogPathFor(projectRoot);
            string[] lines;
            try
            {
                if (!File.Exists(log)) return null;
                lines = File.ReadAllLines(log);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.Contains("FAILED with", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return line;
                // A successful rebuild ends the search: anything older has been superseded.
                if (line.StartsWith("watch: rebuilt", StringComparison.OrdinalIgnoreCase)) return null;
            }
            return null;
        }

        private static long Pid(string projectRoot)
        {
            string key = Normalize(projectRoot);
            if (!Watchers.TryGetValue(key, out long pid)) return 0;

            // Reap a dead entry, or a crashed watcher would make EnsureFor a no-op forever after.
            if (pid > 0 && OS.IsProcessRunning((int)pid)) return pid;
            Watchers.Remove(key);
            return 0;
        }

        /// <summary>Whether a watcher this session did not start is already on that project.</summary>
        private static bool Foreign(string projectRoot)
        {
            if (OperatingSystem.IsWindows()) return false; // No cheap argv query; the in-process registry is all there is.

            var lines = new global::Godot.Collections.Array();
            // -f matches the whole command line, which is where --project <root> appears.
            int code = OS.Execute("/bin/sh", ["-c", $"pgrep -f 'assets watch.*{Normalize(projectRoot)}'"], lines);
            return code == 0 && lines.Count > 0;
        }

        private static void Terminate(long pid)
        {
            // SIGTERM, not SIGKILL: the CLI takes its whole tree down on a TERM and exits
            // ParadiseCli.Interrupted; a KILL would orphan the dotnet children it started.
            if (OperatingSystem.IsWindows())
            {
                OS.Kill((int)pid);
            }
            else
            {
                OS.Execute("kill", ["-TERM", pid.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        private static string Normalize(string projectRoot) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    }
}
#endif
