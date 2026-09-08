#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Zio;

namespace ParadiseGodot.Play
{
    /// <summary>Read the project's engine pin to select a compatible CLI.</summary>
    /// <remarks>
    /// The CLI has no version command. Read ParadiseVersion from Directory.Packages.props, or require
    /// all Paradise.* PackageReferences to agree; mixed versions cannot select a safe document writer.
    /// Exclude Paradise.Godot.Editor because it has an independent release line.
    /// </remarks>
    public static class ProjectEngineVersion
    {
        private const string AddonPackageId = "Paradise.Godot.Editor";

        /// <summary>Return the engine pin, or null if unavailable.</summary>
        /// <param name="root">Repository root containing Directory.Packages.props.</param>
        public static string? Of(IFileSystem files, UPath root)
        {
            ArgumentNullException.ThrowIfNull(files);
            return Central(files, root) ?? Agreed(files, root);
        }

        private static string? Central(IFileSystem files, UPath root)
        {
            var version = Read(files, root / "Directory.Packages.props")?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ParadiseVersion")?.Value.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }

        private static string? Agreed(IFileSystem files, UPath root)
        {
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in Projects(files, root))
            {
                if (Read(files, project) is not { } document) continue;
                foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var id = reference.Attribute("Include")?.Value;
                    var version = reference.Attribute("Version")?.Value?.Trim();
                    if (id is null || version is not { Length: > 0 }) continue;
                    if (!id.StartsWith("Paradise.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id.Equals(AddonPackageId, StringComparison.OrdinalIgnoreCase)) continue;
                    versions.Add(version);
                }
            }
            return versions.Count == 1 ? versions.First() : null;
        }

        // Ignore build output and dot directories so stale project copies cannot affect the pin.
        private static IEnumerable<UPath> Projects(IFileSystem files, UPath root)
        {
            IEnumerable<UPath> found;
            try
            {
                found = files.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var project in found)
            {
                var text = project.FullName;
                if (text.Contains("/bin/", StringComparison.Ordinal) ||
                    text.Contains("/obj/", StringComparison.Ordinal) ||
                    text.Contains("/.", StringComparison.Ordinal))
                {
                    continue;
                }
                yield return project;
            }
        }

        private static XDocument? Read(IFileSystem files, UPath path)
        {
            try
            {
                return XDocument.Parse(files.ReadAllText(path));
            }
            catch (Exception failure) when (failure is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                // An unreadable pin falls through to an installed CLI.
                return null;
            }
        }
    }
}
#endif
