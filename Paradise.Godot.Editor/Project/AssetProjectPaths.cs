#if TOOLS
using System;
using Paradise.Assets.Project;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>Converts between Godot, physical, mounted and authoring asset paths.</summary>
    /// <remarks>
    /// <c>res://</c> starts at <c>project.godot</c>; the asset project starts at the ancestor holding
    /// <c>assets/project.toml</c>. These roots may differ. Paths outside the required tree return
    /// null so pickers can report the problem. No Godot dependency is needed for path tests.
    /// </remarks>
    public sealed class AssetProjectPaths
    {
        public const string ResourceScheme = "res://";

        /// <param name="godotRoot">Absolute physical directory containing <c>project.godot</c>.</param>
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

        /// <summary>The physical root of <c>res://</c>.</summary>
        public UPath GodotRoot { get; }

        public AssetProjectLayout Layout { get; }

        public static bool IsResourcePath(string? path) =>
            path is not null && path.StartsWith(ResourceScheme, StringComparison.Ordinal);

        /// <summary>Convert a <c>res://</c> path to a physical path.</summary>
        /// <exception cref="ArgumentException">The input is not a res:// path.</exception>
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

        /// <summary>A <c>res://</c> path, or null outside the Godot project.</summary>
        public string? ToResourcePath(UPath path)
        {
            if (path == GodotRoot) return ResourceScheme;
            return path.IsInDirectory(GodotRoot, recursive: true)
                ? ResourceScheme + Relative(path, GodotRoot)
                : null;
        }

        /// <summary>A mounted path, or null outside <c>assets/</c>.</summary>
        /// <remarks>Use an explicit null return: a conditional expression can implicitly convert null
        /// to an empty <see cref="UPath"/>, violating the nullable result contract.</remarks>
        public UPath? ToAssetMountPath(UPath path)
        {
            if (ToAssetReferencePath(path) is not { } relative) return null;
            return (UPath)ProjectMounts.AssetsMountName / relative;
        }

        /// <summary>The source path stored in an AssetReference: relative to <c>assets/</c>,
        /// slash-separated, with no leading slash. Null outside <c>assets/</c>.</summary>
        /// <remarks>Only the build converts authoring references to runtime paths.</remarks>
        public string? ToAssetReferencePath(UPath path) =>
            path.IsInDirectory(Layout.Assets, recursive: true) ? Relative(path, Layout.Assets) : null;

        /// <summary>The workfile under <c>.editor/godot/</c>, replacing the source's
        /// <c>.prefab</c> suffix with <c>.tscn</c>. Null outside <c>assets/</c>.</summary>
        /// <remarks>Workfiles cache documents and hold editor state. Godot hides dot-prefixed directories
        /// from its dock, but <c>EditorInterface.OpenSceneFromPath</c> can open them explicitly.</remarks>
        public UPath? WorkfileFor(UPath documentPath)
        {
            if (ToAssetReferencePath(documentPath) is not { } relative) return null;

            var withoutSuffix = relative.EndsWith(DocumentSuffix, StringComparison.OrdinalIgnoreCase)
                ? relative[..^DocumentSuffix.Length]
                : relative;
            return Layout.Editor / WorkfileDirectoryName / (withoutSuffix + WorkfileSuffix);
        }

        /// <summary>The model mirror at its asset-relative path under <c>.editor/godot/</c>,
        /// or null outside <c>assets/</c>.</summary>
        /// <remarks>Use <c>.scn</c>: Godot does not import GLBs in dot-prefixed directories,
        /// but native scenes load by explicit path without importing.</remarks>
        public UPath? MirrorModelFor(UPath modelPath)
        {
            if (ToAssetReferencePath(modelPath) is not { } relative) return null;

            return Layout.Editor / WorkfileDirectoryName / (relative + MirrorModelSuffix);
        }

        /// <summary>The built document in the editor's play tree, or null outside <c>assets/</c>.</summary>
        /// <remarks>Keep the <c>.prefab</c> extension for runtime dispatch and existing references.
        /// <c>.editor/play/</c> isolates editor output from CLI shipping builds under <c>build/</c>.
        /// Use an explicit null return for the <see cref="UPath"/> conversion pitfall documented by
        /// <see cref="ToAssetMountPath"/>.</remarks>
        public UPath? PlayPathFor(UPath documentPath)
        {
            if (ToAssetReferencePath(documentPath) is not { } relative) return null;
            return Layout.EditorPlay / relative;
        }

        /// <summary>Matches <c>AssetClassifier.PrefabSuffix</c> without adding a dependency on the build pipeline.</summary>
        public const string DocumentSuffix = ".prefab";

        public const string WorkfileDirectoryName = "godot";

        private const string WorkfileSuffix = ".tscn";

        private const string MirrorModelSuffix = ".scn";

        /// <summary>Convert an AssetReference's authoring path to a physical path.</summary>
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
