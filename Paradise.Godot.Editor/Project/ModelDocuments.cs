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
    /// <summary>Maps between source GLBs and the mesh documents prefabs reference.</summary>
    /// <remarks>The build ships <c>.mesh</c>/<c>.skinnedmesh</c> documents, not GLBs. Read the
    /// GLB's engine-written sidecar to find its mesh document; read that document's source to find
    /// geometry for previews. Both directions use the engine's document types.</remarks>
    public static class ModelDocuments
    {
        private const string RunTheWatcher =
            "Run `paradise assets watch` (or `paradise assets extract`) and pick it again.";

        public static bool IsGlb(string path) =>
            path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the path is a <c>.mesh</c> or <c>.skinnedmesh</c> document.</summary>
        public static bool IsMeshDocument(string path) =>
            MeshReferenceDocument.SlotOf(path) is { } slot && MeshReferenceDocument.IsGeometry(slot);

        /// <summary>The GLB's generated mesh reference, or null with a reason.</summary>
        /// <param name="glbAuthoringPath">The GLB path relative to <c>assets/</c>.</param>
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

        /// <summary>The mesh document's source GLB as an authoring path, or null with a reason.</summary>
        /// <param name="findByIdentity">Resolve the source identity only if its path no longer exists,
        /// avoiding a full sidecar scan when the source has not moved.</param>
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
