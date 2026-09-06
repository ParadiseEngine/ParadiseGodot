#if TOOLS
using System;
using Godot;
using Paradise.Assets.Project;
using ParadiseGodot.Documents;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// Turns what an author PICKED into the reference a document stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A picker hands back a path — absolute from the OS dialog, <c>res://</c> from Godot's own, or
    /// the <c>assets/</c>-relative spelling a loaded document displayed. A document stores
    /// <c>{ guid, path }</c>: the identity that survives a rename, and the authoring path that makes
    /// a broken reference fixable by hand.
    /// </para>
    /// <para>
    /// The path is relative to <c>assets/</c> and is never the built one. That asymmetry is the
    /// contract's — authored as a reference, exported as a value — and it is why nothing here looks
    /// at <c>.editor/play/</c> or <c>build/</c>.
    /// </para>
    /// </remarks>
    public sealed class AssetReferenceResolver
    {
        private readonly IFileSystem _files;
        private readonly AssetProjectPaths _paths;
        private readonly AssetSidecars _sidecars;

        public AssetReferenceResolver(IFileSystem files, AssetProjectPaths paths, AssetSidecars sidecars)
        {
            _files = files;
            _paths = paths;
            _sidecars = sidecars;
        }

        /// <summary>Build one from an open project, indexing its sidecars under the manifest's
        /// ignore rules.</summary>
        public static AssetReferenceResolver For(ParadiseProject project)
        {
            ArgumentNullException.ThrowIfNull(project);

            AssetIgnoreRules ignore;
            try
            {
                ignore = ProjectManifest.Load(project.Files, project.Layout.Manifest).Ignore;
            }
            catch (ProjectManifestException failure)
            {
                // A manifest that does not read is verify's to report; references still resolve,
                // they just cannot honour an ignore list nobody could parse.
                GD.PushWarning($"[Paradise] {failure.Message}");
                ignore = AssetIgnoreRules.None;
            }

            var sidecars = AssetSidecars.Index(project.Files, project.Layout, ignore);
            return new AssetReferenceResolver(project.Files, project.Paths, sidecars);
        }

        /// <summary>
        /// The reference for a picked file, minting its identity if it has none, or null with the
        /// reason reported.
        /// </summary>
        /// <param name="picked">An absolute host path, a <c>res://</c> one, or an authoring path.</param>
        public AuthoredValue? Reference(string picked)
        {
            if (AuthoringPathOf(picked) is not { } authoring) return null;

            if (_sidecars.EnsureIdentity(_files, authoring) is not { } guid)
            {
                GD.PushWarning(
                    $"[Paradise] '{authoring}' does not exist, or the project's [assets] ignore excludes " +
                    "it, so it has no identity to reference.");
                return null;
            }

            return AuthoredValue.Reference(guid, authoring);
        }

        /// <summary>
        /// The reference a MESH field stores for a picked model: the <c>.mesh</c> or
        /// <c>.skinnedmesh</c> document itself, or the one minted beside a picked GLB.
        /// </summary>
        /// <remarks>A GLB is accepted so an author can point at the model they see, but what is
        /// written is never the GLB — the build ships nothing for it (engine #245/#246), and a
        /// reference to it would name a file no runtime is ever given.</remarks>
        public AuthoredValue? MeshDocument(string picked)
        {
            if (ModelDocuments.IsMeshDocument(picked)) return Reference(picked);

            if (!ModelDocuments.IsGlb(picked))
            {
                GD.PushWarning(
                    $"[Paradise] '{picked}' is neither a mesh document nor a GLB, so a mesh field " +
                    "cannot reference it. Pick a .mesh or .skinnedmesh under assets/, or the GLB it " +
                    "was extracted from.");
                return null;
            }

            if (AuthoringPathOf(picked) is not { } glb) return null;
            var reference = ModelDocuments.MeshDocumentOf(_files, _paths.Layout, glb, out var problem);
            if (problem is not null) GD.PushWarning($"[Paradise] {problem}");
            return reference;
        }

        /// <summary>The GLB a mesh document is cooked from, as a host path Godot can load — or
        /// null with the reason reported.</summary>
        public string? SourceGlbOf(string meshAuthoringPath)
        {
            var glb = ModelDocuments.SourceGlbOf(
                _files, _paths.Layout, meshAuthoringPath, _sidecars.PathOf, out var problem);
            if (problem is not null) GD.PushWarning($"[Paradise] {problem}");
            return glb is null ? null : _files.ConvertPathToInternal(_paths.Layout.Assets / glb);
        }

        /// <summary>Where a stored reference points now, for showing an author what they picked.
        /// By GUID first, so a renamed asset still displays.</summary>
        public string? Display(Guid guid, string? path) => _sidecars.Resolve(guid, path);

        /// <summary>A picked path as the <c>assets/</c>-relative one, or null with the reason
        /// reported. Refuses anything outside <c>assets/</c>: the build only knows about the source
        /// tree, so a reference to anything else names a file no runtime will ever be given.</summary>
        private string? AuthoringPathOf(string picked)
        {
            if (string.IsNullOrWhiteSpace(picked)) return null;

            // What a loaded document displays is already the authoring path; round-tripping it
            // through the OS would make "penguins/adelie.glb" relative to the process, not the tree.
            if (!AssetProjectPaths.IsResourcePath(picked) && !System.IO.Path.IsPathRooted(picked) &&
                _files.FileExists(_paths.Layout.Assets / picked))
            {
                return picked;
            }

            UPath full;
            try
            {
                full = AssetProjectPaths.IsResourcePath(picked)
                    ? _paths.FromResourcePath(picked)
                    : _files.ConvertPathFromInternal(System.IO.Path.GetFullPath(picked));
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
            {
                GD.PushWarning($"[Paradise] '{picked}' is not a path this project can resolve: {failure.Message}.");
                return null;
            }

            if (_paths.ToAssetReferencePath(full) is { } authoring) return authoring;

            GD.PushWarning(
                $"[Paradise] '{picked}' is outside this project's assets/ directory, so it cannot " +
                "be referenced. Move it under assets/ and pick it again.");
            return null;
        }
    }
}
#endif
