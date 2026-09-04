#if TOOLS
using System;
using Godot;
using ParadiseGodot.Documents;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// Turns what an author PICKED into the reference a document stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A picker hands back a path — absolute from the OS dialog, or <c>res://</c> from Godot's own.
    /// A document stores <c>{ guid, path }</c>: the identity that survives a rename, and the
    /// authoring path that makes a broken reference fixable by hand.
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

        /// <summary>Build one from an open project, indexing its sidecars.</summary>
        public static AssetReferenceResolver For(ParadiseProject project)
        {
            ArgumentNullException.ThrowIfNull(project);
            var sidecars = AssetSidecars.Index(project.Files, project.Layout);
            foreach (var problem in sidecars.Problems) GD.PushWarning($"[Paradise] {problem}");
            return new AssetReferenceResolver(project.Files, project.Paths, sidecars);
        }

        /// <summary>
        /// The reference for a picked file, minting its identity if it has none, or null with the
        /// reason reported.
        /// </summary>
        /// <param name="picked">An absolute host path, or a <c>res://</c> one.</param>
        public AuthoredValue? Reference(string picked)
        {
            if (string.IsNullOrWhiteSpace(picked)) return null;

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

            if (_paths.ToAssetReferencePath(full) is not { } authoring)
            {
                // Outside assets/ is not a near miss: the build only knows about the source tree, so
                // a reference to anything else names a file no runtime will ever be given.
                GD.PushWarning(
                    $"[Paradise] '{picked}' is outside this project's assets/ directory, so it cannot " +
                    "be referenced. Move it under assets/ and pick it again.");
                return null;
            }

            if (_sidecars.EnsureIdentity(_files, _paths.Layout, authoring) is not { } guid)
            {
                GD.PushWarning($"[Paradise] '{authoring}' does not exist, so it has no identity to reference.");
                return null;
            }

            return AuthoredValue.Reference(guid, authoring);
        }

        /// <summary>Where a stored reference points now, for showing an author what they picked.
        /// By GUID first, so a renamed asset still displays.</summary>
        public string? Display(Guid guid, string? path) => _sidecars.Resolve(guid, path);
    }
}
#endif
