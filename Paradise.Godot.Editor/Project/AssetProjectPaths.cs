#if TOOLS
using System;
using Paradise.Assets.Project;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// The four names one file answers to while the addon edits an asset project, and the
    /// conversions between them.
    ///
    /// <code>
    /// res://scenes/pool.tscn            what Godot calls it
    /// /Users/…/Pingu/assets/scenes/…    where it is, in Zio's physical space
    /// /assets/scenes/pool.scene.toml    reached through the project mounts
    /// scenes/pool.scene.toml            what an AssetReference carries
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately free of Godot, so the arithmetic is testable. The off-by-one-slash bugs this
    /// prevents are the kind that survive review and then resolve a picked asset to a path that
    /// exists on the author's machine and nowhere else.
    /// </para>
    /// <para>
    /// <b>Two roots, not one.</b> <c>res://</c> is the GODOT project root — the directory holding
    /// <c>project.godot</c> — while the asset project is rooted at whichever ancestor holds
    /// <c>assets/project.toml</c>. They coincide in every game today and there is nothing that
    /// makes them coincide, so nothing here assumes it.
    /// </para>
    /// <para>
    /// A path outside the tree it is being converted for comes back <see langword="null"/> rather
    /// than throwing: an author pointing at a file outside the project is a mistake to be reported
    /// with a warning naming the file, not an exception out of a picker.
    /// </para>
    /// </remarks>
    public sealed class AssetProjectPaths
    {
        /// <summary>The scheme Godot names project-relative resources with.</summary>
        public const string ResourceScheme = "res://";

        /// <param name="godotRoot">Absolute physical path of the directory holding <c>project.godot</c>.</param>
        /// <param name="layout">The located asset project.</param>
        public AssetProjectPaths(UPath godotRoot, AssetProjectLayout layout)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (!godotRoot.IsAbsolute)
            {
                throw new ArgumentException(
                    $"'{godotRoot}' must be absolute: a relative root names nothing in the file " +
                    "system it is resolved against.",
                    nameof(godotRoot));
            }

            GodotRoot = godotRoot;
            Layout = layout;
        }

        /// <summary>Where <c>res://</c> points.</summary>
        public UPath GodotRoot { get; }

        /// <summary>The asset project this Godot project is editing.</summary>
        public AssetProjectLayout Layout { get; }

        /// <summary>Whether <paramref name="path"/> is spelled as a Godot resource.</summary>
        public static bool IsResourcePath(string? path) =>
            path is not null && path.StartsWith(ResourceScheme, StringComparison.Ordinal);

        /// <summary>A <c>res://</c> path as a physical one.</summary>
        /// <exception cref="ArgumentException"><paramref name="resourcePath"/> is not a res:// path.</exception>
        public UPath FromResourcePath(string resourcePath)
        {
            if (!IsResourcePath(resourcePath))
            {
                throw new ArgumentException(
                    $"'{resourcePath}' is not a Godot resource path; it must start with '{ResourceScheme}'.",
                    nameof(resourcePath));
            }

            var relative = resourcePath[ResourceScheme.Length..].Trim('/');
            return relative.Length == 0 ? GodotRoot : GodotRoot / relative;
        }

        /// <summary>A physical path as a <c>res://</c> one, or null when it lies outside the Godot
        /// project and so cannot be named to Godot at all.</summary>
        public string? ToResourcePath(UPath path)
        {
            if (path == GodotRoot) return ResourceScheme;
            return path.IsInDirectory(GodotRoot, recursive: true)
                ? ResourceScheme + Relative(path, GodotRoot)
                : null;
        }

        /// <summary>A physical path as it is reached through <see cref="ProjectMounts"/>, or null
        /// when it is not under <c>assets/</c>.</summary>
        /// <remarks>Two statements rather than a conditional, and not for taste: <see cref="UPath"/>
        /// converts implicitly from string, so <c>… ? somePath : null</c> types as
        /// <see cref="UPath"/> with the null arm converted, and the method returns a path spelled
        /// "" where it promised nothing. An early return cannot express that.</remarks>
        public UPath? ToAssetMountPath(UPath path)
        {
            if (ToAssetReferencePath(path) is not { } relative) return null;
            return (UPath)ProjectMounts.AssetsMountName / relative;
        }

        /// <summary>
        /// A physical path as the AUTHORING path an <c>AssetReference</c> carries — relative to
        /// <c>assets/</c>, '/'-separated, no leading slash — or null when it is not under
        /// <c>assets/</c>.
        /// </summary>
        /// <remarks>Never the built path. A reference is authored against the source tree and
        /// flattened to whatever the runtime resolves only by the build.</remarks>
        public string? ToAssetReferencePath(UPath path) =>
            path.IsInDirectory(Layout.Assets, recursive: true) ? Relative(path, Layout.Assets) : null;

        /// <summary>The inverse: an <c>AssetReference</c>'s authoring path, back to a physical one.</summary>
        public UPath FromAssetReferencePath(string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(authoringPath);
            var trimmed = authoringPath.Trim('/');
            return trimmed.Length == 0 ? Layout.Assets : Layout.Assets / trimmed;
        }

        private static string Relative(UPath path, UPath directory) =>
            path.FullName[(directory.FullName.TrimEnd('/').Length + 1)..];
    }
}
#endif
