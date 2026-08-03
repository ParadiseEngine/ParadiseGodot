#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Paradise.Export.Paths;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Stages the project's authored UI source tree into the export data directory, so a runtime
    /// host can load XAML, fonts and images from <c>&lt;data&gt;/ui/</c> alongside the rest of the
    /// engine-neutral contract — the UI half of what SceneDataExporter does for scenes.
    ///
    /// The split matters: <c>res://ui</c> (the <see cref="UiSourceDirSetting"/> project setting)
    /// is COMMITTED authoring source — a Noesis Studio project, its XAML tree, fonts and images —
    /// while the data directory is regenerated build output. Studio's design-time sidecars
    /// (the hidden <c>.noesis/</c> folder and <c>*.noesis</c> project files) are authoring-only
    /// and never ship.
    ///
    /// Staging is additive: files are copied over, and the destination is never wiped. A project
    /// may legitimately keep other generated UI assets under <c>&lt;data&gt;/ui/</c>, and an
    /// export must never be able to destroy authored content that a mis-set path points at. The
    /// cost is that renaming a source file leaves its stale copy behind until the data directory
    /// is regenerated from scratch.
    /// </summary>
    internal static class UiAssetExporter
    {
        /// <summary>Godot ProjectSettings key (committed in project.godot) naming the authored UI
        /// source directory. Absent or empty falls back to <see cref="DefaultUiSourceDir"/>.</summary>
        public const string UiSourceDirSetting = "paradise/export/ui_source_dir";
        public const string DefaultUiSourceDir = "res://ui";

        /// <summary>Subdirectory of the export data directory that staged UI lands in.</summary>
        public const string StagedDirName = "ui";

        /// <summary>The configured UI source directory as a res:// path, without a trailing slash.</summary>
        public static string UiSourceDir
        {
            get
            {
                string value = ProjectSettings.HasSetting(UiSourceDirSetting)
                    ? ProjectSettings.GetSetting(UiSourceDirSetting).AsString().Trim()
                    : "";
                return value.Length == 0 ? DefaultUiSourceDir : value.TrimEnd('/');
            }
        }

        /// <summary>Copy the authored UI tree into <c>&lt;data&gt;/ui/</c> and lint the staged
        /// XAML's references. A project with no UI source directory is the normal case for
        /// scenes that ship no XAML — it skips silently rather than warning on every export.</summary>
        public static void Export(ExportPaths paths)
        {
            string source = ProjectSettings.GlobalizePath(UiSourceDir);
            if (!Directory.Exists(source))
            {
                return;
            }

            string staged = Path.Combine(paths.DataDir, StagedDirName);
            if (IsSameOrNested(source, staged))
            {
                GD.PushWarning(
                    $"[Paradise.Export] UI source '{UiSourceDir}' overlaps its staging target " +
                    $"'{staged}' — skipping UI staging. Author UI outside the export data " +
                    "directory (the data directory is regenerated build output).");
                return;
            }

            var stagedFiles = new List<string>();
            CopyTree(source, staged, relativeDir: "", stagedFiles);
            if (stagedFiles.Count == 0)
            {
                return;
            }

            GD.Print($"[Paradise.Export] Staged {stagedFiles.Count} UI asset(s): {staged}");
            ValidateReferences(staged, stagedFiles);
        }

        /// <summary>Recursive copy of the stageable files under <paramref name="sourceDir"/>,
        /// preserving subfolders. Collects each staged file as a forward-slashed path relative to
        /// the staging root.</summary>
        private static void CopyTree(string sourceDir, string stagedRoot, string relativeDir, List<string> stagedFiles)
        {
            foreach (string file in Directory.EnumerateFiles(sourceDir))
            {
                string name = Path.GetFileName(file);
                if (!UiStagingRules.ShouldStageFile(name))
                {
                    continue;
                }

                string relative = relativeDir.Length == 0 ? name : $"{relativeDir}/{name}";
                string destination = Path.Combine(stagedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                stagedFiles.Add(relative);
            }

            foreach (string directory in Directory.EnumerateDirectories(sourceDir))
            {
                string name = Path.GetFileName(directory);
                if (UiStagingRules.ShouldSkipDirectory(name))
                {
                    continue;
                }

                CopyTree(directory, stagedRoot, relativeDir.Length == 0 ? name : $"{relativeDir}/{name}", stagedFiles);
            }
        }

        /// <summary>Warn about assets a staged XAML references but that did not make it into the
        /// staged tree — typically an image or font left outside the UI source directory, or one
        /// whose extension is not staged. Best-effort: a miss is a warning, never an error.</summary>
        private static void ValidateReferences(string stagedRoot, List<string> stagedFiles)
        {
            foreach (string relative in stagedFiles)
            {
                if (!relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string xamlPath = Path.Combine(stagedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                string xaml;
                try
                {
                    xaml = File.ReadAllText(xamlPath);
                }
                catch (IOException e)
                {
                    GD.PushWarning($"[Paradise.Export] Could not read staged UI file '{relative}': {e.Message}");
                    continue;
                }

                // Noesis roots its resource providers at the loaded XAML's directory, so a
                // reference normally resolves against the staging root; also accept one relative
                // to the referencing file's own folder, which is how authors usually think about
                // nested XAML. Only warn when NEITHER exists.
                string ownDir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                foreach (UiReference reference in UiStagingRules.ScanReferences(xaml))
                {
                    if (Resolves(stagedRoot, reference, "") || (ownDir.Length > 0 && Resolves(stagedRoot, reference, ownDir)))
                    {
                        continue;
                    }

                    string what = reference.Kind == UiReferenceKind.FontFolder
                        ? $"font folder '{reference.Path}' (no .ttf/.otf staged there)"
                        : $"file '{reference.Path}'";
                    GD.PushWarning(
                        $"[Paradise.Export] Staged UI '{relative}' references a missing {what}. " +
                        $"Move it under '{UiSourceDir}' so it is staged with the XAML.");
                }
            }
        }

        private static bool Resolves(string stagedRoot, UiReference reference, string baseDir)
        {
            string relative = baseDir.Length == 0 ? reference.Path : $"{baseDir}/{reference.Path}";
            string full = Path.Combine(stagedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (reference.Kind == UiReferenceKind.File)
            {
                return File.Exists(full);
            }

            if (!Directory.Exists(full))
            {
                return false;
            }

            foreach (string font in Directory.EnumerateFiles(full))
            {
                if (UiStagingRules.IsFontFile(Path.GetFileName(font)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when the two directories are the same or one contains the other — the
        /// footgun case where the UI source has been pointed at (or above) its own staging
        /// target, which would copy the tree onto itself.</summary>
        private static bool IsSameOrNested(string first, string second)
        {
            string a = Normalize(first);
            string b = Normalize(second);
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                   a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
                   b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

            static string Normalize(string path) =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        }
    }
}
#endif
