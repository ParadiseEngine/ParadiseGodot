#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace ParadiseGodot
{
    /// <summary>Check the asset project, Godot ignore markers, and package references.</summary>
    /// <remarks>The addon supplies Paradise.Export; warn about redundant pins without editing the project.</remarks>
    public static class ProjectSetup
    {
        /// <summary>Supported Paradise.Export version. Keep aligned with AddonVersion.props and plugin.cfg;
        /// the data contract follows major.minor.</summary>
        public const string SupportedExportVersion = "0.40.0";

        public static void Run()
        {
            bool ok = WarnOnRedundantExportReference() & EnsureAssetProject();
            GD.Print(ok
                ? "[Paradise] Project Setup complete."
                : "[Paradise] Project Setup finished with warnings — see errors above.");
        }

        /// <summary>Warn when the resolved Paradise.Export major.minor differs from the supported contract.</summary>
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

        // A direct Paradise.Export pin can diverge from the addon's dependency and data contract.
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
                var project = XElement.Load(csproj);
                XElement? pinned = project.Descendants(project.Name.Namespace + "PackageReference")
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

        // Godot must ignore the engine's source and derived trees; see EnsureGodotIgnores.
        private static bool EnsureAssetProject()
        {
            if (!Project.ParadiseProject.TryOpen(out var project, out var problem))
            {
                GD.PushError(
                    $"[Paradise] {problem} Create one with `paradise new <name>` beside project.godot, " +
                    "or point the Godot project at a checkout that has one.");
                return false;
            }

            using (project)
            {
                try
                {
                    foreach (var marker in project.EnsureGodotIgnores())
                    {
                        GD.Print($"[Paradise] Wrote {marker} so Godot never scans that tree.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    GD.PushError($"[Paradise] Could not write a .gdignore: {ex.Message}");
                    return false;
                }

                GD.Print($"[Paradise] Asset project at {project.Files.ConvertPathToInternal(project.Layout.Root)}.");
                return true;
            }
        }
    }
}
#endif
