#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Authoring;
using ParadiseGodot.Project;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// Opens an authoring document as a Godot scene, through a disposable working file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.tscn</c> under <c>.editor/tscn/</c> is a CACHE, not a source. It is rewritten from
    /// the document on every open, which is why nothing here checks whether it is up to date: the
    /// document is the truth, and re-materializing costs an import Godot was going to do anyway.
    /// The freshness stamp taken here protects the SAVE instead (<see cref="DocumentSession"/>):
    /// what it guards against is writing over a document something else changed meanwhile.
    /// </para>
    /// <para>
    /// Instances are expanded before the scene is built, so what an author sees is the whole scene
    /// rather than an opaque reference. The expanded children are marked
    /// <see cref="DocumentLoader.DerivedMetaKey"/>: they are the resolver's, not the document's,
    /// and saving one back would flatten the instance.
    /// </para>
    /// </remarks>
    public static class DocumentWorkfile
    {
        /// <summary>Materialize <paramref name="documentPath"/> and open it in the editor.</summary>
        /// <param name="project">The open asset project.</param>
        /// <param name="documentPath">Absolute physical path of the <c>.prefab</c>.</param>
        /// <returns>Whether the scene was opened.</returns>
        public static bool Open(ParadiseProject project, UPath documentPath)
        {
            ArgumentNullException.ThrowIfNull(project);

            if (project.Paths.WorkfileFor(documentPath) is not { } workfile)
            {
                GD.PushError(
                    $"[Paradise] '{documentPath}' is not under this project's assets/ directory, so it " +
                    "is not a document this project can open.");
                return false;
            }

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

            var resolved = PrefabResolver.Resolve(document, reference => LoadPrefab(project, reference));
            foreach (var error in resolved.Errors) GD.PushWarning($"[Paradise] {error.Message}");

            var name = documentPath.GetNameWithoutExtension() ?? "Document";
            var built = DocumentLoader.Build(resolved.Document, name);
            foreach (var problem in built.Problems) GD.PushWarning($"[Paradise] {problem}");
            if (built.Root is not { } root)
            {
                GD.PushError($"[Paradise] '{documentPath}' produced no scene.");
                return false;
            }

            var resPath = project.Paths.ToResourcePath(workfile);
            if (resPath is null)
            {
                GD.PushError(
                    $"[Paradise] The working file for '{documentPath}' would be at '{workfile}', outside " +
                    "the Godot project, so the editor cannot open it. The asset project and the Godot " +
                    "project must share a root for now.");
                root.QueueFree();
                return false;
            }

            if (!Save(root, resPath)) return false;

            EditorInterface.Singleton.OpenSceneFromPath(resPath);
            // Recorded on the root the EDITOR now holds, not on the detached one that was packed:
            // the writer reads this off the edited scene, and the packed node is already freed.
            if (project.Paths.ToAssetReferencePath(documentPath) is { } authoringPath &&
                EditorInterface.Singleton.GetEditedSceneRoot() is { } opened)
            {
                DocumentSession.Remember(opened, project.Files, documentPath, authoringPath);
            }
            GD.Print(
                $"[Paradise] Opened '{documentPath}': {built.Objects} object(s), " +
                $"{built.Components} component payload(s), {resolved.Expanded} instance(s) expanded.");
            return true;
        }

        private static bool Save(Node3D root, string resPath)
        {
            var packed = new PackedScene();
            // Pack BEFORE freeing: the scene is built detached from the tree, and PackedScene copies
            // out of the node rather than holding it.
            var packError = packed.Pack(root);
            root.QueueFree();
            if (packError != Error.Ok)
            {
                GD.PushError($"[Paradise] Could not pack '{resPath}': {packError}.");
                return false;
            }

            var directory = resPath[..resPath.LastIndexOf('/')];
            if (DirAccess.MakeDirRecursiveAbsolute(directory) is var dirError && dirError != Error.Ok)
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

        /// <summary>
        /// Resolve a prefab reference to its document.
        /// </summary>
        /// <remarks>By PATH. A reference carries a GUID as well, and the GUID is authoritative —
        /// but following it needs an index of every sidecar under <c>assets/</c>, which arrives
        /// with the asset pickers that mint them. Until then the path is the whole answer, which is
        /// exactly the recovery route the reference carries a path FOR.</remarks>
        private static PrefabDocument? LoadPrefab(ParadiseProject project, AssetReference reference)
        {
            if (reference?.Path is not { Length: > 0 } path) return null;

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
