#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Godot;
using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Authoring;
using ParadiseGodot.Project;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>Opens an authoring document through a cached Godot working scene.</summary>
    /// <remarks>
    /// Rebuild only when <see cref="WorkfileStamp"/> is stale: workfiles also hold author-added
    /// colliders, lights, rigs and editor state. <see cref="WorkfileCarryover"/> preserves author
    /// nodes across rebuilds; <see cref="DocumentSession"/> separately guards saves against drift.
    /// Expanded prefab children are marked derived so saving cannot flatten instances.
    /// </remarks>
    public static class DocumentWorkfile
    {
        /// <param name="documentPath">Absolute physical path of the <c>.prefab</c>.</param>
        /// <returns>Whether the scene was opened.</returns>
        public static bool Open(ParadiseProject project, UPath documentPath)
        {
            ArgumentNullException.ThrowIfNull(project);

            if (!Locate(project, documentPath, out var workfile, out var resPath)) return false;

            // Reuse current workfiles to preserve author-added nodes and editor state.
            if (!WorkfileStamp.IsCurrent(project.Files, workfile, documentPath))
            {
                if (!Materialize(project, documentPath, workfile, resPath)) return false;
            }
            else
            {
                GD.Print($"[Paradise] '{documentPath}' is already current in its working file.");
            }

            EditorInterface.Singleton.OpenSceneFromPath(resPath);
            RememberSession(project, documentPath);
            return true;
        }

        /// <summary>Build a working file without opening it, for stale files or project conversion.</summary>
        public static bool Materialize(ParadiseProject project, UPath documentPath)
        {
            ArgumentNullException.ThrowIfNull(project);

            return Locate(project, documentPath, out var workfile, out var resPath) &&
                Materialize(project, documentPath, workfile, resPath);
        }

        private static bool Materialize(
            ParadiseProject project, UPath documentPath, UPath workfile, string resPath)
        {
            PrefabDocument document;
            try
            {
                document = PrefabDocumentSerializer.Load(project.Files, documentPath);
            }
            catch (PrefabDocumentException failure)
            {
                GD.PushError($"[Paradise] {failure.Message}");
                return false;
            }

            var sidecars = AssetSidecars.Index(project.Files, project.Layout);

            var resolved = PrefabResolver.Resolve(
                document, reference => LoadPrefab(project, sidecars, reference));
            foreach (var error in resolved.Errors) GD.PushWarning($"[Paradise] {error.Message}");

            var name = documentPath.GetNameWithoutExtension() ?? "Document";
            var built = DocumentLoader.Build(resolved.Document, name);
            foreach (var problem in built.Problems) GD.PushWarning($"[Paradise] {problem}");
            if (built.Root is not { } root)
            {
                GD.PushError($"[Paradise] '{documentPath}' produced no scene.");
                return false;
            }

            var carried = Carry(resPath, root);

            if (!Save(root, resPath)) return false;
            WorkfileStamp.Write(project.Files, workfile, documentPath);

            GD.Print(
                $"[Paradise] Built '{documentPath}': {built.Objects} object(s), " +
                $"{built.Components} component payload(s), {resolved.Expanded} instance(s) expanded" +
                (carried > 0 ? $", {carried} authored node(s) carried over." : "."));
            return true;
        }

        /// <summary>The document's working file as physical and res:// paths.</summary>
        private static bool Locate(
            ParadiseProject project, UPath documentPath, out UPath workfile,
            [NotNullWhen(true)] out string? resPath)
        {
            workfile = default;
            resPath = null;

            if (project.Paths.WorkfileFor(documentPath) is not { } found)
            {
                GD.PushError(
                    $"[Paradise] '{documentPath}' is not under this project's assets/ directory, so it " +
                    "is not a document this project can open.");
                return false;
            }
            workfile = found;

            resPath = project.Paths.ToResourcePath(workfile);
            if (resPath is null)
            {
                GD.PushError(
                    $"[Paradise] The working file for '{documentPath}' would be at '{workfile}', outside " +
                    "the Godot project, so the editor cannot open it. The asset project and the Godot " +
                    "project must share a root for now.");
                return false;
            }
            return true;
        }

        /// <summary>Stamp the editor's live root; the detached root used for packing is already freed.</summary>
        private static void RememberSession(ParadiseProject project, UPath documentPath)
        {
            if (project.Paths.ToAssetReferencePath(documentPath) is { } authoringPath &&
                EditorInterface.Singleton.GetEditedSceneRoot() is { } opened)
            {
                DocumentSession.Remember(opened, project.Files, documentPath, authoringPath);
            }
        }

        private static int Carry(string resPath, Node3D root)
        {
            if (!ResourceLoader.Exists(resPath)) return 0;

            Node? previous = null;
            try
            {
                // Ignore Godot's cached scene or carryover can restore a version older than the last save.
                previous = ResourceLoader
                    .Load<PackedScene>(resPath, cacheMode: ResourceLoader.CacheMode.Ignore)
                    ?.Instantiate();
            }
            catch (Exception failure)
            {
                // An unreadable workfile must not prevent opening its source document.
                GD.PushWarning($"[Paradise] Could not read the previous working file: {failure.Message}");
            }
            if (previous is null) return 0;

            var adopted = WorkfileCarryover.Take(previous);
            previous.QueueFree();
            return WorkfileCarryover.Restore(root, adopted);
        }

        private static bool Save(Node3D root, string resPath)
        {
            var packed = new PackedScene();
            // Pack before freeing: PackedScene copies the detached nodes.
            var packError = packed.Pack(root);
            root.QueueFree();
            if (packError != Error.Ok)
            {
                GD.PushError($"[Paradise] Could not pack '{resPath}': {packError}.");
                return false;
            }

            var directory = resPath[..resPath.LastIndexOf('/')];
            var dirError = DirAccess.MakeDirRecursiveAbsolute(directory);
            if (dirError != Error.Ok)
            {
                GD.PushError($"[Paradise] Could not create '{directory}': {dirError}.");
                return false;
            }

            var saveError = ResourceSaver.Save(packed, resPath);
            if (saveError != Error.Ok)
            {
                GD.PushError($"[Paradise] Could not write the working file '{resPath}': {saveError}.");
                return false;
            }

            return true;
        }

        /// <summary>Resolve by GUID first so renames preserve identity; use the path as fallback.</summary>
        private static PrefabDocument? LoadPrefab(
            ParadiseProject project, AssetSidecars sidecars, AssetReference reference)
        {
            if (sidecars.Resolve(reference.Guid, reference.Path) is not { } path) return null;

            var full = project.Paths.FromAssetReferencePath(path);
            try
            {
                return project.Files.FileExists(full)
                    ? PrefabDocumentSerializer.Load(project.Files, full)
                    : null;
            }
            catch (PrefabDocumentException failure)
            {
                GD.PushWarning($"[Paradise] Instance references '{path}', which does not read: {failure.Message}");
                return null;
            }
        }
    }
}
#endif
