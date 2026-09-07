#if TOOLS
using System;
using System.Collections.Generic;
using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// What a whole-project conversion has to touch: every document that becomes a working scene,
    /// and every model that becomes a Godot scene beside it.
    /// </summary>
    /// <remarks>
    /// The decision half only — which files, and whether each output is stale. Doing the work
    /// needs Godot, and this stays Godot-free so the walking and the staleness rules can be tested
    /// against a memory filesystem.
    /// </remarks>
    public static class ProjectMirror
    {
        /// <summary>One source under <c>assets/</c> and where it is mirrored.</summary>
        public readonly record struct Entry(UPath Source, UPath Mirror, bool Stale);

        /// <summary>Every <c>*.prefab</c> in the project, with the working file it builds into.</summary>
        public static IReadOnlyList<Entry> Documents(
            IFileSystem files, AssetProjectLayout layout, AssetProjectPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            return Walk(files, layout, AssetProjectPaths.DocumentSuffix, paths.WorkfileFor);
        }

        /// <summary>Every model in the project, with the Godot scene it mirrors into.</summary>
        /// <remarks>Only <c>.glb</c>: the engine refuses <c>.gltf</c> by name, so a mirror of one
        /// would be a scene for a model no build will ever ship.</remarks>
        public static IReadOnlyList<Entry> Models(
            IFileSystem files, AssetProjectLayout layout, AssetProjectPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            return Walk(files, layout, ".glb", paths.MirrorModelFor);
        }

        private static IReadOnlyList<Entry> Walk(
            IFileSystem files, AssetProjectLayout layout, string suffix, Func<UPath, UPath?> mirrorOf)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);

            var found = new List<Entry>();
            if (!files.DirectoryExists(layout.Assets)) return found;

            foreach (var source in files.EnumerateFiles(layout.Assets, "*", SearchOption.AllDirectories))
            {
                if (!source.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (mirrorOf(source) is not { } mirror) continue;

                found.Add(new Entry(source, mirror, !WorkfileStamp.IsCurrent(files, mirror, source)));
            }
            return found;
        }
    }
}
#endif
