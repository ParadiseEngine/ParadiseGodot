#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>Tracks each open scene's source document and last-read file stamp.</summary>
    /// <remarks>A cheap <c>(last write, length)</c> stamp detects changes before saving.
    /// Keep stamps in process memory: Godot undo can restore old node metadata after a save,
    /// causing the next save to reject its own previous write.</remarks>
    public static class DocumentSession
    {
        /// <summary>The source document path, relative to <c>assets/</c>.</summary>
        public const string DocumentMetaKey = "paradise_document";

        /// <summary>Last-read or last-written stamps, keyed by authoring path.</summary>
        private static readonly Dictionary<string, string> Stamps = new(StringComparer.Ordinal);

        public static void Remember(Node root, IFileSystem files, UPath document, string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(root);
            root.SetMeta(DocumentMetaKey, authoringPath);
            Restamp(files, document, authoringPath);
        }

        /// <summary>The scene's source document, or null for an ordinary Godot scene.</summary>
        public static string? DocumentOf(Node? root) =>
            root is not null && root.HasMeta(DocumentMetaKey)
                ? root.GetMeta(DocumentMetaKey).AsString()
                : null;

        /// <summary>Unknown documents count as unchanged; no prior read means no evidence of drift.</summary>
        public static bool IsUnchanged(IFileSystem files, UPath document, string authoringPath) =>
            !Stamps.TryGetValue(authoringPath, out var remembered) ||
            string.Equals(remembered, WorkfileStamp.SourceStamp(files, document), StringComparison.Ordinal);

        /// <summary>Refresh the stamp after a successful write.</summary>
        public static void Restamp(IFileSystem files, UPath document, string authoringPath) =>
            Stamps[authoringPath] = WorkfileStamp.SourceStamp(files, document);
    }
}
#endif
