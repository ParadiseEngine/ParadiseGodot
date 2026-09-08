#if TOOLS
using System;
using Godot;
using Paradise.Assets.Project;
using ParadiseGodot.Documents;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>Converts picker paths to document references.</summary>
    /// <remarks>Accepts absolute, <c>res://</c> and authoring paths. Stores <c>{ guid, path }</c>,
    /// with the path relative to <c>assets/</c>: the GUID survives renames and the path allows repair.
    /// References use source assets, never play or build output.</remarks>
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

        /// <summary>Index an open project's sidecars using its manifest ignore rules.</summary>
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
                // Keep resolving without ignores; assets verify reports the unreadable manifest.
                GD.PushWarning($"[Paradise] {failure.Message}");
                ignore = AssetIgnoreRules.None;
            }

            var sidecars = AssetSidecars.Index(project.Files, project.Layout, ignore);
            return new AssetReferenceResolver(project.Files, project.Paths, sidecars);
        }

        /// <summary>Resolve a picked file, minting its identity if needed; return null with a reason on failure.</summary>
        /// <param name="picked">An absolute, <c>res://</c> or authoring path.</param>
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

        /// <summary>Resolve a mesh document directly or through a picked GLB's sidecar.</summary>
        /// <remarks>Store the <c>.mesh</c> or <c>.skinnedmesh</c> reference; the build does not ship GLBs.</remarks>
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

        /// <summary>The source GLB's physical path, or null with a reason.</summary>
        public string? SourceGlbOf(string meshAuthoringPath)
        {
            var glb = ModelDocuments.SourceGlbOf(
                _files, _paths.Layout, meshAuthoringPath, _sidecars.PathOf, out var problem);
            if (problem is not null) GD.PushWarning($"[Paradise] {problem}");
            return glb is null ? null : _files.ConvertPathToInternal(_paths.Layout.Assets / glb);
        }

        /// <summary>Resolve the current display path by GUID first, preserving renamed references.</summary>
        public string? Display(Guid guid, string? path) => _sidecars.Resolve(guid, path);

        /// <summary>An <c>assets/</c>-relative path, or null with a reason. Reject outside paths
        /// because the build cannot ship them.</summary>
        private string? AuthoringPathOf(string picked)
        {
            if (string.IsNullOrWhiteSpace(picked)) return null;

            // Loaded authoring paths are relative to assets, not the process working directory.
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
