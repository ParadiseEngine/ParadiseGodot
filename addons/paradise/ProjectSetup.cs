#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace ParadiseGodot
{
    /// <summary>"Paradise/Project Setup": one-click wiring of a Godot .NET project for the
    /// Paradise addon. Installing an addon zip cannot touch the user's csproj, so the C# addon
    /// sources will not compile until the <c>Paradise.Export</c> package is referenced — this
    /// action closes that gap, creates the data-directory layout, and persists the default
    /// settings. Idempotent: safe to run repeatedly.</summary>
    public static class ProjectSetup
    {
        /// <summary>The Paradise.Export version this addon release is developed against. Kept in
        /// lockstep with plugin.cfg's version (addon minor tracks the engine/data-contract
        /// minor). Project Setup pins new references to it; the load-time compatibility check
        /// warns when the resolved assembly diverges on major.minor.</summary>
        public const string SupportedExportVersion = "0.3.0";

        public static void Run()
        {
            bool ok = true;
            ok &= EnsurePackageReference();
            ok &= EnsureDataLayout();
            EnsureProjectSettings();
            GD.Print(ok
                ? "[Paradise] Project Setup complete. If the package reference was just added, build the project (or let the editor rebuild) before using the export tools."
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

        private static bool EnsurePackageReference()
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
                bool present = doc.Descendants(ns + "PackageReference")
                    .Any(e => string.Equals((string?)e.Attribute("Include"), "Paradise.Export", StringComparison.OrdinalIgnoreCase));
                if (present)
                {
                    GD.Print($"[Paradise] {Path.GetFileName(csproj)}: Paradise.Export reference already present.");
                    return true;
                }

                var reference = new XElement(ns + "PackageReference",
                    new XAttribute("Include", "Paradise.Export"),
                    new XAttribute("Version", SupportedExportVersion));
                // Reuse an existing PackageReference group when there is one; otherwise append a
                // fresh ItemGroup at the end of the project element.
                XElement? group = doc.Descendants(ns + "PackageReference").FirstOrDefault()?.Parent;
                if (group is null)
                {
                    group = new XElement(ns + "ItemGroup");
                    doc.Root!.Add(group);
                }
                group.Add(reference);
                doc.Save(csproj);
                GD.Print($"[Paradise] {Path.GetFileName(csproj)}: added Paradise.Export {SupportedExportVersion} package reference.");
                return true;
            }
            catch (Exception ex)
            {
                GD.PushError($"[Paradise] Could not update '{csproj}': {ex.Message}");
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
