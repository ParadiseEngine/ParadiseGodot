#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace ParadiseGodot
{
    /// <summary>"Paradise/Project Setup": one-click wiring of a Godot .NET project for the
    /// Paradise addon — creates the data-directory layout and persists the default settings.
    /// Idempotent: safe to run repeatedly.
    ///
    /// It used to WRITE a pinned <c>Paradise.Export</c> PackageReference into the user's csproj,
    /// because an addon installed from a zip could not reference anything itself. The addon is a
    /// package now and states its own dependencies, so that write would put a second, hand-pinned
    /// version next to the one the addon actually compiled against — reintroducing exactly the
    /// drift packaging removed. It now only warns about such a reference if it finds one.</summary>
    public static class ProjectSetup
    {
        /// <summary>The Paradise.Export version this addon release is developed against. Kept in
        /// lockstep with AddonVersion.props and plugin.cfg (addon minor tracks the
        /// engine/data-contract minor). The load-time compatibility check warns when the resolved
        /// assembly diverges from it on major.minor.</summary>
        public const string SupportedExportVersion = "0.25.0";

        public static void Run()
        {
            bool ok = true;
            ok &= WarnOnRedundantExportReference();
            ok &= EnsureDataLayout();
            EnsureProjectSettings();
            GD.Print(ok
                ? "[Paradise] Project Setup complete."
                : "[Paradise] Project Setup finished with warnings — see errors above.");
        }

        /// <summary>Warn at plugin load when the compiled-in Paradise.Export diverges from the
        /// addon's supported major.minor — the data contract tracks that version.</summary>
        public static void CheckExportVersion()
        {
            Version? actual = typeof(Paradise.Export.ParadiseExportInfo).Assembly.GetName().Version;
            var supported = Version.Parse(SupportedExportVersion);
            if (actual is null)
            {
                return;
            }
            if (actual.Major != supported.Major || actual.Minor != supported.Minor)
            {
                GD.PushWarning(
                    $"[Paradise] This addon targets Paradise.Export {supported.Major}.{supported.Minor}.x " +
                    $"but the project references {actual.ToString(3)}. The export contract follows " +
                    "major.minor — align the package version (Project Setup pins the supported one) " +
                    "or update the addon.");
            }
        }

        /// <summary>
        /// Paradise.Export arrives with this addon, at the version it was compiled against. A
        /// hand-written reference to it in the game's csproj can only agree with that by luck,
        /// and when it does not, the export contract the addon writes and the one the game reads
        /// silently differ. Say so; do not edit the file.
        /// </summary>
        private static bool WarnOnRedundantExportReference()
        {
            string projectDir = ProjectSettings.GlobalizePath("res://");
            string? csproj = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (csproj is null)
            {
                GD.PushError(
                    "[Paradise] No .csproj found next to project.godot. Create the C# project first " +
                    "(Project > Tools > C# > Create C# solution), then re-run Project Setup.");
                return false;
            }

            try
            {
                var doc = XDocument.Load(csproj, LoadOptions.PreserveWhitespace);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                XElement? pinned = doc.Descendants(ns + "PackageReference")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), "Paradise.Export", StringComparison.OrdinalIgnoreCase));
                if (pinned is not null)
                {
                    GD.PushWarning(
                        $"[Paradise] {Path.GetFileName(csproj)} pins Paradise.Export " +
                        $"{(string?)pinned.Attribute("Version") ?? "?"} by hand. Paradise.Godot.Editor " +
                        $"already brings {SupportedExportVersion}; remove the hand-written reference so " +
                        "there is only one version to keep aligned.");
                }
                return true;
            }
            catch (Exception ex)
            {
                GD.PushError($"[Paradise] Could not read '{csproj}': {ex.Message}");
                return false;
            }
        }

        private static bool EnsureDataLayout()
        {
            try
            {
                string root = ParadisePaths.DataDirGlobal;
                foreach (string sub in new[] { "", "scenes", "materials", "Models", "primitives", "sprites" })
                {
                    Directory.CreateDirectory(Path.Combine(root, sub));
                }
                GD.Print($"[Paradise] Data layout ready under {ParadisePaths.DataDir}/.");
                return true;
            }
            catch (Exception ex)
            {
                GD.PushError($"[Paradise] Could not create the data layout: {ex.Message}");
                return false;
            }
        }

        private static void EnsureProjectSettings()
        {
            if (!ProjectSettings.HasSetting(ParadisePaths.DataDirSetting))
            {
                ProjectSettings.SetSetting(ParadisePaths.DataDirSetting, ParadisePaths.DefaultDataDir);
            }
            ProjectSettings.SetInitialValue(ParadisePaths.DataDirSetting, ParadisePaths.DefaultDataDir);

            // The authored UI tree the export pipeline stages into <data>/ui/. Registered here so
            // it is editable in Project Settings; the default is right for most projects.
            if (!ProjectSettings.HasSetting(Export.UiAssetExporter.UiSourceDirSetting))
            {
                ProjectSettings.SetSetting(
                    Export.UiAssetExporter.UiSourceDirSetting, Export.UiAssetExporter.DefaultUiSourceDir);
            }
            ProjectSettings.SetInitialValue(
                Export.UiAssetExporter.UiSourceDirSetting, Export.UiAssetExporter.DefaultUiSourceDir);
            ProjectSettings.Save();
        }
    }
}
#endif
