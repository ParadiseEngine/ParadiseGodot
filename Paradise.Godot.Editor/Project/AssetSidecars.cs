#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Paradise.Assets.Documents;
using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;
using Paradise.Authoring;
using Zio;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// Every asset's durable identity, read from the <c>&lt;asset&gt;.meta</c> sidecars beside it —
    /// plus the identities this session minted since.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolution rule is the engine's <see cref="AssetIndex"/>, not a copy of it: the GUID
    /// decides and the path is a hint, a file the manifest's <c>[assets] ignore</c> excludes carries
    /// no identity, and a path matches only when spelled as the file is. A reference this addon
    /// writes therefore resolves here exactly as it will under <c>paradise assets build</c> — which
    /// is the point of not having a second rule.
    /// </para>
    /// <para>
    /// The one thing the index does not do is mint. It is one scan, held; an identity minted after
    /// it is remembered beside it so a pick is usable at once, and the next operation's scan reads
    /// the sidecar off disk like any other. Duplicate and unreadable sidecars are
    /// <c>paradise assets verify</c>'s to report — here the first asset in scan order wins, as it
    /// does at build.
    /// </para>
    /// </remarks>
    public sealed class AssetSidecars
    {
        private readonly AssetIndex _index;
        private readonly AssetProjectLayout _layout;
        private readonly Dictionary<Guid, string> _mintedByGuid = [];
        private readonly Dictionary<string, Guid> _mintedByPath = new(StringComparer.Ordinal);

        private AssetSidecars(AssetIndex index, AssetProjectLayout layout)
        {
            _index = index;
            _layout = layout;
        }

        /// <summary>How many assets carry an identity.</summary>
        public int Count => _index.Files.Count(file => _index.IdentityOf(file) is not null) + _mintedByGuid.Count;

        /// <summary>Walk <c>assets/</c> and read every sidecar.</summary>
        /// <param name="ignore">The manifest's <c>[assets] ignore</c>; an ignored file cannot be
        /// referenced, because the build will never ship it.</param>
        public static AssetSidecars Index(IFileSystem files, AssetProjectLayout layout, AssetIgnoreRules? ignore = null)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);
            return new AssetSidecars(AssetIndex.Scan(files, layout.Assets, ignore), layout);
        }

        /// <summary>The authoring path of an identity, or null when nothing carries it.</summary>
        public string? PathOf(Guid guid)
        {
            if (_mintedByGuid.TryGetValue(guid, out var minted)) return minted;
            return _index.Find(guid) is { } path ? _index.Relative(path) : null;
        }

        /// <summary>The identity at an authoring path, or null when it has no sidecar yet.</summary>
        public Guid? GuidAt(string authoringPath) =>
            _mintedByPath.TryGetValue(authoringPath, out var minted)
                ? minted
                : _index.IdentityOf(_layout.Assets / authoringPath);

        /// <summary>Whether the manifest excludes this file from the build.</summary>
        public bool IsIgnored(string authoringPath) => _index.IsIgnored(_layout.Assets / authoringPath);

        /// <summary>
        /// Resolve a reference: by GUID first, then by path.
        /// </summary>
        /// <remarks>The order IS the contract — a rename moves the path and keeps the GUID, so
        /// trusting the path first would resolve to whatever now sits at the old name. The path
        /// comes back when the GUID names nothing: it is what a person can fix.</remarks>
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
        /// author dropped in to look at. Null when the asset does not exist or is ignored — either
        /// way an identity for it would be a reference the build can never honour.</remarks>
        public Guid? EnsureIdentity(IFileSystem files, string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(files);
            if (GuidAt(authoringPath) is { } existing) return existing;

            var asset = _layout.Assets / authoringPath;
            if (!files.FileExists(asset) || _index.IsIgnored(asset)) return null;

            var meta = SidecarMeta.Mint();
            meta.Save(files, SidecarMeta.PathFor(asset));
            _mintedByGuid[meta.Guid] = authoringPath;
            _mintedByPath[authoringPath] = meta.Guid;
            return meta.Guid;
        }
    }
}
#endif
