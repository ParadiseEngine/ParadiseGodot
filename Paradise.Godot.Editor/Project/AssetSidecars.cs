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
    /// <summary>Asset identities from <c>&lt;asset&gt;.meta</c> sidecars and identities minted this session.</summary>
    /// <remarks>
    /// Uses the engine's <see cref="AssetIndex"/> rules: GUID before path, exact path spelling,
    /// manifest ignores, and first match for duplicate sidecars. Assets verify reports invalid
    /// sidecars. Newly minted identities supplement the held index until the next scan.
    /// </remarks>
    public sealed class AssetSidecars
    {
        private readonly AssetIndex _index;
        private readonly Dictionary<Guid, string> _mintedByGuid = [];
        private readonly Dictionary<string, Guid> _mintedByPath = new(StringComparer.Ordinal);

        private AssetSidecars(AssetIndex index) => _index = index;

        public int Count => _index.Files.Count(file => _index.IdentityOf(file) is not null) + _mintedByGuid.Count;

        /// <param name="ignore">Manifest ignore rules; ignored assets cannot be referenced or shipped.</param>
        public static AssetSidecars Index(IFileSystem files, AssetProjectLayout layout, AssetIgnoreRules? ignore = null)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(layout);
            return new AssetSidecars(AssetIndex.Scan(files, layout.Assets, ignore));
        }

        /// <summary>The identity's authoring path, or null if none is found.</summary>
        public string? PathOf(Guid guid)
        {
            if (_mintedByGuid.TryGetValue(guid, out var minted)) return minted;
            return _index.Find(guid) is { } path ? _index.Relative(path) : null;
        }

        /// <summary>The authoring path's identity, or null if it has no sidecar.</summary>
        public Guid? GuidAt(string authoringPath) =>
            _mintedByPath.TryGetValue(authoringPath, out var minted)
                ? minted
                : _index.IdentityOf(_index.Root / authoringPath);

        public bool IsIgnored(string authoringPath) => _index.IsIgnored(_index.Root / authoringPath);

        /// <summary>Resolve by GUID first to survive renames, then by path for manual recovery.</summary>
        public string? Resolve(Guid guid, string? path)
        {
            if (guid != Guid.Empty && PathOf(guid) is { } found) return found;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Return an asset's identity, writing a sidecar if needed.</summary>
        /// <remarks>Mint only when referenced to avoid sidecars for unused files. Missing or ignored
        /// assets return null because the build cannot ship them.</remarks>
        public Guid? EnsureIdentity(IFileSystem files, string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(files);
            if (GuidAt(authoringPath) is { } existing) return existing;

            var asset = _index.Root / authoringPath;
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
