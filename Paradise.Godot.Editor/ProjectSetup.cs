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
        /// <summary>The <c>Paradise.Export</c> version this addon was COMPILED against, read out
        /// of the addon assembly's own reference table.
        ///
        /// It used to be a hand-typed constant kept "in lockstep" with the PackageReference by a
        /// line in docs/publishing.md, which is the same shape of drift packaging the addon was
        /// meant to end — and it drifted: 0.15.0 shipped declaring 0.14.0 while depending on
        /// 0.17.0, so every consuming editor warned about a divergence that did not exist. The
        /// compiler already records this number and cannot forget to; nothing is gained by
        /// restating it. Null only if the reference were absent, which cannot happen while
        /// <see cref="CheckExportVersion"/> below names a type from that assembly.</summary>
        public static Version? TargetedExportVersion =>
            typeof(ProjectSetup).Assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(a => a.Name == "Paradise.Export")?.Version;

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

        /// <summary>Warn at plugin load when the Paradise.Export the project actually LOADED
        /// diverges on major.minor from the one this addon was built against — the data contract
        /// tracks that version.
        ///
        /// Both numbers are now observed rather than declared, so the warning means exactly one
        /// thing: the game forced a different Paradise.Export than the addon brought, and the
        /// contract the addon writes may not be the contract the game reads. In particular it no
        /// longer fires under an engine-SOURCE override, where both halves build from the same
        /// tree and agree at whatever version that tree carries.</summary>
        public static void CheckExportVersion()
        {
            Version? actual = typeof(Paradise.Export.ParadiseExportInfo).Assembly.GetName().Version;
            Version? targeted = TargetedExportVersion;
            if (actual is null || targeted is null)
            {
                return;
            }
            if (actual.Major != targeted.Major || actual.Minor != targeted.Minor)
            {
                GD.PushWarning(
                    $"[Paradise] This addon was built against Paradise.Export {targeted.Major}.{targeted.Minor}.x " +
                    $"but the project loaded {actual.ToString(3)}. The export contract follows " +
                    "major.minor — drop the game's own Paradise.Export reference so the addon's " +
                    "wins, or move to an addon built against that contract.");
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
                        $"already brings {TargetedExportVersion?.ToString(3)}; remove the hand-written reference so " +
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
