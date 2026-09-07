#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Zio;

namespace ParadiseGodot.Play
{
    /// <summary>
    /// The <c>Paradise.*</c> version a game project pins, read out of the project itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CLI has no version verb — <c>paradise --version</c> is an unknown command — so the
    /// addon cannot ask a CLI what it is. It can only decide WHICH one to run, and this is the
    /// number that decides. It matters because a CLI older than the tree writes documents the
    /// runtime cannot read, and one that cannot read the manifest falls back to defaults and
    /// reports a cascade of errors about everything except the version.
    /// </para>
    /// <para>
    /// Two shapes exist in the wild and both are read here: a single
    /// <c>&lt;ParadiseVersion&gt;</c> in <c>Directory.Packages.props</c> (ShiningPie, ParadiseTown),
    /// and bare <c>PackageReference</c> versions spread across the csprojs (Pingu, which has no
    /// central props file). The second only answers when every reference agrees — a project
    /// mid-bump pins nothing coherent, and guessing which half is right is how you install a CLI
    /// that writes documents half the tree cannot read.
    /// </para>
    /// <para>
    /// <c>Paradise.Godot.Editor</c> is excluded from that agreement on purpose: the addon has its
    /// own release line that tracks the engine's minor rather than matching it, so counting it
    /// would make every project that is one addon release behind look like a disagreement.
    /// </para>
    /// </remarks>
    public static class ProjectEngineVersion
    {
        private const string AddonPackageId = "Paradise.Godot.Editor";

        /// <summary>The version this project pins, or null when it pins nothing this can read.</summary>
        /// <param name="root">The repository root — where <c>Directory.Packages.props</c> would be.</param>
        public static string? Of(IFileSystem files, UPath root)
        {
            ArgumentNullException.ThrowIfNull(files);
            return Central(files, root) ?? Agreed(files, root);
        }

        /// <summary><c>&lt;ParadiseVersion&gt;</c> from <c>Directory.Packages.props</c>.</summary>
        private static string? Central(IFileSystem files, UPath root)
        {
            var props = root / "Directory.Packages.props";
            if (!files.FileExists(props)) return null;

            return Read(files, props) is { } document
                ? document.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ParadiseVersion")?.Value.Trim() is { Length: > 0 } version
                    ? version
                    : null
                : null;
        }

        /// <summary>The one version every <c>Paradise.*</c> PackageReference in the tree names, or
        /// null when they disagree or there are none.</summary>
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

        /// <summary>Every csproj in the tree, skipping build output and dot directories — a
        /// published <c>wwwroot</c> holds copies of the project's own files, and reading those
        /// would let stale output outvote the source.</summary>
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
                // A project that does not parse pins nothing readable; the ladder falls through to
                // whatever is installed rather than stopping the author over someone's broken XML.
                return null;
            }
        }
    }
}
#endif
