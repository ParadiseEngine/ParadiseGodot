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
    /// The <c>.tscn</c> under <c>.editor/godot/</c> is DERIVED but not disposable-on-sight: the
    /// document is still the truth, and deleting the working file still costs nothing but a
    /// rebuild — but it is rebuilt only when the document has actually moved
    /// (<see cref="WorkfileStamp"/>), because it also holds what the document has no place for.
    /// The editor camera and the selection are the small half of that; the author's own
    /// <c>CollisionShape3D</c>s, lights and rigs are the half that matters, and rebuilding
    /// unconditionally used to delete them on every open.
    /// </para>
    /// <para>
    /// When it does rebuild, those nodes are carried across (<see cref="WorkfileCarryover"/>)
    /// rather than lost. The freshness stamp taken here protects the SAVE instead
    /// (<see cref="DocumentSession"/>): what it guards against is writing over a document
    /// something else changed meanwhile.
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

            if (Locate(project, documentPath, out var workfile, out var resPath) is not true) return false;

            // A current working file is opened as the author left it — scaffolding, camera and
            // all. Rebuilding here would be right about the entities and wrong about the rest.
            if (!WorkfileStamp.IsCurrent(project.Files, workfile, documentPath))
            {
                if (!Materialize(project, documentPath, workfile, resPath!)) return false;
            }
            else
            {
                GD.Print($"[Paradise] '{documentPath}' is already current in its working file.");
            }

            EditorInterface.Singleton.OpenSceneFromPath(resPath);
            RememberSession(project, documentPath);
            return true;
        }

        /// <summary>
        /// Build one document's working file without opening it — what a whole-project conversion
        /// does, and what <see cref="Open"/> does first when the file is stale.
        /// </summary>
        public static bool Materialize(ParadiseProject project, UPath documentPath)
        {
            ArgumentNullException.ThrowIfNull(project);

            return Locate(project, documentPath, out var workfile, out var resPath) is true &&
                Materialize(project, documentPath, workfile, resPath!);
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

        /// <summary>The working file for a document, as both a physical and a res:// path.</summary>
        private static bool Locate(
            ParadiseProject project, UPath documentPath, out UPath workfile, out string? resPath)
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

        /// <summary>Recorded on the root the EDITOR now holds, not on the detached one that was
        /// packed: the writer reads this off the edited scene, and the packed node is already
        /// freed.</summary>
        private static void RememberSession(ParadiseProject project, UPath documentPath)
        {
            if (project.Paths.ToAssetReferencePath(documentPath) is { } authoringPath &&
                EditorInterface.Singleton.GetEditedSceneRoot() is { } opened)
            {
                DocumentSession.Remember(opened, project.Files, documentPath, authoringPath);
            }
        }

        /// <summary>Move the author's own nodes out of the working file being replaced and into
        /// the tree that replaces it.</summary>
        private static int Carry(string resPath, Node3D root)
        {
            if (!ResourceLoader.Exists(resPath)) return 0;

            Node? previous = null;
            try
            {
                // CacheMode.Ignore: this must read what is ON DISK. Godot hands back the cached
                // PackedScene otherwise — the one from before the author's last save — and the
                // carry-over then silently preserves an older scene than the one they are looking
                // at. Measured: the light added in a probe vanished on every rebuild.
                previous = ResourceLoader
                    .Load<PackedScene>(resPath, cacheMode: ResourceLoader.CacheMode.Ignore)
                    ?.Instantiate();
            }
            catch (Exception failure)
            {
                // A working file that will not load is one the author cannot have edited since it
                // broke; rebuilding without it beats refusing to open the document.
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
        /// <remarks>By GUID first, then by path. The order is the contract: a rename moves the path
        /// and keeps the identity, so trusting the path first would open whatever now sits at the
        /// old name. The path is the recovery route for an identity nothing carries.</remarks>
        private static PrefabDocument? LoadPrefab(
            ParadiseProject project, AssetSidecars sidecars, AssetReference reference)
        {
            if (reference is null) return null;
            if (sidecars.Resolve(reference.Guid, reference.Path) is not { Length: > 0 } path) return null;

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
