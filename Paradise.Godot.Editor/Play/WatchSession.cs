#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace ParadiseGodot.Play
{
    /// <summary>Supervise one assets watcher per project, including its build-status tray.</summary>
    /// <remarks>
    /// <para>Concurrent watchers race in AssetWatcher.Drain's unsynchronized sidecar quarantine,
    /// losing rename identities, and write the same play tree.</para>
    /// <para>The registry is in-process. <see cref="Foreign"/> detects other hosts' watchers through
    /// the OS, but cannot provide a cross-process lock.</para>
    /// </remarks>
    public static class WatchSession
    {
        /// <summary>Machine-level EditorSettings key for automatic watching on document open.</summary>
        public const string AutoWatchSetting = "paradise/assets/auto_watch";

        /// <summary>Machine-level EditorSettings key for the watcher's build profile.</summary>
        public const string ProfileSetting = "paradise/assets/build_profile";

        // Match filesystem case rules so alternate path spellings cannot start duplicate watchers.
        private static readonly Dictionary<string, long> Watchers =
            new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        // Keep the tray enabled to report rebuild failures without a console.
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

        /// <summary>A separate log for each project root.</summary>
        /// <remarks>SHA-1 stays stable across sessions; randomized string.GetHashCode() would lose old logs.</remarks>
        public static string LogPathFor(string projectRoot)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            string normalized = Normalize(projectRoot);
            string digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))[..6].ToLowerInvariant();
            string name = new DirectoryInfo(normalized).Name;
            return Path.Combine(Path.GetTempPath(), $"paradise_godot_watch_{name}_{digest}.log");
        }

        public static bool IsWatching(string projectRoot) => Pid(projectRoot) > 0;

        /// <summary>Start this project's watcher if needed.</summary>
        /// <returns>True if this session already had or successfully started a watcher.</returns>
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
                problem = ParadiseCli.MissingCliMessage;
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

        public static void StopFor(string projectRoot)
        {
            ArgumentNullException.ThrowIfNull(projectRoot);

            long pid = Pid(projectRoot);
            Watchers.Remove(Normalize(projectRoot));
            if (pid > 0) ParadiseCli.Terminate(pid);
        }

        // Called at plugin teardown so watchers do not outlive the editor.
        public static void StopAll()
        {
            foreach (long pid in Watchers.Values) ParadiseCli.Terminate(pid);
            Watchers.Clear();
        }

        /// <summary>Return the latest rebuild error, or null.</summary>
        /// <remarks>Search backwards and skip failure counts to find an actionable cause.</remarks>
        public static string? LastError(string projectRoot)
        {
            string log = LogPathFor(projectRoot);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(log);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Contains("FAILED with", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return line;
                // A successful rebuild supersedes older errors.
                if (line.StartsWith("watch: rebuilt", StringComparison.OrdinalIgnoreCase)) return null;
            }
            return null;
        }

        private static long Pid(string projectRoot)
        {
            string key = Normalize(projectRoot);
            if (!Watchers.TryGetValue(key, out long pid)) return 0;

            // Remove crashed watchers so EnsureFor can restart them.
            if (OS.IsProcessRunning((int)pid)) return pid;
            Watchers.Remove(key);
            return 0;
        }

        /// <summary>Detect a watcher started outside this session.</summary>
        private static bool Foreign(string projectRoot)
        {
            if (OperatingSystem.IsWindows()) return false; // No cheap argv query; the in-process registry is all there is.

            var lines = new global::Godot.Collections.Array();
            // -f includes --project in the match.
            int code = OS.Execute("/bin/sh", ["-c", $"pgrep -f 'assets watch.*{Normalize(projectRoot)}'"], lines);
            return code == 0 && lines.Count > 0;
        }

        private static string Normalize(string projectRoot) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    }
}
#endif
