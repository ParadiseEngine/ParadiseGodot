#if TOOLS
using System;
using System.IO;
using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;
using ParadiseGodot.Documents;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// The two hops between a model on disk and what a document references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A GLB ships nothing (engine 0.40): the watcher mints a <c>.mesh</c> or <c>.skinnedmesh</c>
    /// document beside it, and a prefab references THAT — never the GLB. The GLB's sidecar records
    /// which document was minted, so an author who points at a model is pointing, one hop away, at
    /// its document. That hop is <see cref="MeshDocumentOf"/>.
    /// </para>
    /// <para>
    /// The reverse hop is what an editor needs to SHOW the model: the document names the GLB it is
    /// cooked from, and only the GLB has geometry. That is <see cref="SourceGlbOf"/>.
    /// </para>
    /// <para>
    /// Neither hop is invented here. Both read what the engine's own tools wrote, in the engine's
    /// own types, so a reference this addon stores is the one <c>paradise assets build</c> expects.
    /// </para>
    /// </remarks>
    public static class ModelDocuments
    {
        private const string RunTheWatcher =
            "Run `paradise assets watch` (or `paradise assets extract`) and pick it again.";

        /// <summary>Whether a path names a model the engine extracts.</summary>
        public static bool IsGlb(string path) =>
            path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a path names a geometry document — a <c>.mesh</c> or
        /// <c>.skinnedmesh</c>, the two kinds a mesh reference may carry.</summary>
        public static bool IsMeshDocument(string path) =>
            MeshReferenceDocument.SlotOf(path) is { } slot && MeshReferenceDocument.IsGeometry(slot);

        /// <summary>
        /// The mesh document minted for a GLB, as the reference a document stores — or null with
        /// the reason an author can act on.
        /// </summary>
        /// <param name="glbAuthoringPath">The GLB, relative to <c>assets/</c>.</param>
        public static AuthoredValue? MeshDocumentOf(
            IFileSystem files, AssetProjectLayout layout, string glbAuthoringPath, out string? problem)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);

            var sidecar = SidecarMeta.PathFor(layout.Assets / glbAuthoringPath);
            if (!files.FileExists(sidecar))
            {
                problem = $"'{glbAuthoringPath}' has no sidecar, so nothing has extracted it yet. {RunTheWatcher}";
                return null;
            }

            SidecarMeta meta;
            try
            {
                meta = SidecarMeta.Load(files, sidecar);
            }
            catch (SidecarMetaException failure)
            {
                problem = $"'{glbAuthoringPath}{SidecarMeta.Suffix}' does not read: {failure.Message}";
                return null;
            }

            if (GlbImportSettings.ReadExtraction(meta).Mesh is not { } mesh)
            {
                problem = $"'{glbAuthoringPath}' has not been extracted: its sidecar records no mesh document. {RunTheWatcher}";
                return null;
            }

            problem = null;
            return AuthoredValue.Reference(mesh.Guid, mesh.Path);
        }

        /// <summary>
        /// The GLB a mesh document is cooked from, as an authoring path — or null with the reason.
        /// </summary>
        /// <param name="findByIdentity">Where an identity's asset is now, consulted only when the
        /// path the document spells no longer exists. Lazy because answering it means scanning
        /// every sidecar, and a document whose source has not moved never needs that.</param>
        public static string? SourceGlbOf(
            IFileSystem files,
            AssetProjectLayout layout,
            string meshAuthoringPath,
            Func<Guid, string?> findByIdentity,
            out string? problem)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(findByIdentity);

            var path = layout.Assets / meshAuthoringPath;
            if (!files.FileExists(path))
            {
                problem = $"'{meshAuthoringPath}' does not exist under assets/.";
                return null;
            }

            MeshReferenceDocument document;
            try
            {
                document = MeshReferenceDocument.Load(files, path);
            }
            catch (Exception failure) when (failure is FormatException or IOException)
            {
                problem = $"'{meshAuthoringPath}' is not a readable mesh document: {failure.Message}";
                return null;
            }

            var source = document.Source;
            if (files.FileExists(layout.Assets / source.Path))
            {
                problem = null;
                return source.Path;
            }

            if (source.Guid != Guid.Empty && findByIdentity(source.Guid) is { } moved &&
                files.FileExists(layout.Assets / moved))
            {
                problem = null;
                return moved;
            }

            problem = $"'{meshAuthoringPath}' is cooked from '{source.Path}', which is not under assets/ any more.";
            return null;
        }
    }
}
#endif
