#if TOOLS
using System;
using System.Collections.Generic;
using Paradise.Assets.Project;
using ParadiseGodot.Project;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>Lists document/model mirrors and their staleness without requiring Godot.</summary>
    /// <remarks>Separates filesystem decisions from scene creation so memory-filesystem tests can cover them.</remarks>
    public static class ProjectMirror
    {
        /// <summary>A source under <c>assets/</c> and its mirror path.</summary>
        public readonly record struct Entry(UPath Source, UPath Mirror, bool Stale);

        /// <summary>Project prefabs and their working files.</summary>
        public static IReadOnlyList<Entry> Documents(
            IFileSystem files, AssetProjectLayout layout, AssetProjectPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            return Walk(files, layout, AssetProjectPaths.DocumentSuffix, paths.WorkfileFor);
        }

        /// <summary>Project models and their Godot mirrors.</summary>
        /// <remarks>Only <c>.glb</c>: the engine build rejects <c>.gltf</c>.</remarks>
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
