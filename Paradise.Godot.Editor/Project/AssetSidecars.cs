#if TOOLS
using System;
using System.Collections.Generic;
using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// Every asset's durable identity, read from the <c>&lt;asset&gt;.meta</c> sidecars beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reference carries both a GUID and a path, and <b>the GUID is authoritative</b>: resolving
    /// by it is what lets an asset be renamed or moved without touching a document that points at
    /// it. Following one needs this index, because a GUID says nothing about where its asset is.
    /// </para>
    /// <para>
    /// The path is the recovery route, not a second identity. When a GUID resolves to nothing — a
    /// sidecar lost, a file arriving from a branch that never had one — falling back to the path
    /// degrades to something a person can fix, which is the whole reason a reference carries both.
    /// </para>
    /// <para>
    /// Built by one walk and then held. It is a snapshot: a sidecar minted by something else after
    /// this was built is not in it, which is why the addon builds one per operation rather than
    /// caching it across an editing session.
    /// </para>
    /// </remarks>
    public sealed class AssetSidecars
    {
        private readonly Dictionary<Guid, string> _byGuid = [];
        private readonly Dictionary<string, Guid> _byPath = new(StringComparer.Ordinal);

        private AssetSidecars() { }

        /// <summary>How many assets carry an identity.</summary>
        public int Count => _byGuid.Count;

        /// <summary>Problems found while indexing, phrased for an author.</summary>
        public List<string> Problems { get; } = [];

        /// <summary>Walk <c>assets/</c> and read every sidecar.</summary>
        public static AssetSidecars Index(IFileSystem files, AssetProjectLayout layout)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);

            var index = new AssetSidecars();
            if (!files.DirectoryExists(layout.Assets)) return index;

            foreach (var sidecar in files.EnumerateFiles(layout.Assets, "*" + SidecarMeta.Suffix, SearchOption.AllDirectories))
            {
                var asset = SidecarMeta.AssetPathFor(sidecar);
                var relative = asset.FullName[(layout.Assets.FullName.TrimEnd('/').Length + 1)..];

                SidecarMeta meta;
                try
                {
                    meta = SidecarMeta.Load(files, sidecar);
                }
                catch (SidecarMetaException failure)
                {
                    index.Problems.Add($"'{relative}{SidecarMeta.Suffix}' does not read: {failure.Message}");
                    continue;
                }

                // Two assets claiming one identity makes every reference to it ambiguous, and the
                // one that loses is decided by directory order — so it is named rather than
                // resolved.
                if (index._byGuid.TryGetValue(meta.Guid, out var existing))
                {
                    index.Problems.Add(
                        $"'{relative}' and '{existing}' both claim the identity {meta.Guid:D}; " +
                        "references to it resolve to the first. Delete one sidecar and let it be re-minted.");
                    continue;
                }

                index._byGuid[meta.Guid] = relative;
                index._byPath[relative] = meta.Guid;
            }

            return index;
        }

        /// <summary>The authoring path of an identity, or null when nothing carries it.</summary>
        public string? PathOf(Guid guid) => _byGuid.GetValueOrDefault(guid);

        /// <summary>The identity at an authoring path, or null when it has no sidecar yet.</summary>
        public Guid? GuidAt(string authoringPath) =>
            _byPath.TryGetValue(authoringPath, out var guid) ? guid : null;

        /// <summary>
        /// Resolve a reference: by GUID first, then by path.
        /// </summary>
        /// <remarks>The order IS the contract — a rename moves the path and keeps the GUID, so
        /// trusting the path first would resolve to whatever now sits at the old name.</remarks>
        public string? Resolve(Guid guid, string? path)
        {
            if (guid != Guid.Empty && PathOf(guid) is { } found) return found;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// The identity of an asset, minting and writing a sidecar when it has none.
        /// </summary>
        /// <remarks>Minting on REFERENCE rather than on import: an asset nobody points at needs no
        /// identity, and a project that mints one per file has a sidecar for every stray image an
        /// author dropped in to look at. Null when the asset itself does not exist — an identity for
        /// a file that is not there would be a reference nothing can ever resolve.</remarks>
        public Guid? EnsureIdentity(IFileSystem files, AssetProjectLayout layout, string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);
            if (GuidAt(authoringPath) is { } existing) return existing;

            var asset = layout.Assets / authoringPath;
            if (!files.FileExists(asset)) return null;

            var meta = SidecarMeta.Mint();
            meta.Save(files, SidecarMeta.PathFor(asset));
            _byGuid[meta.Guid] = authoringPath;
            _byPath[authoringPath] = meta.Guid;
            return meta.Guid;
        }
    }
}
#endif
