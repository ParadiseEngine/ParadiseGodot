#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// Which document an open scene came from, and what it looked like when it was opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stamp is what stops a save from silently overwriting a document something else changed
    /// — a rebuild, another editor, a text edit. It is <c>(last write, length)</c> rather than a
    /// hash because a save has to decide in the time between Ctrl+S and the file being written.
    /// </para>
    /// <para>
    /// <b>Held in this process as well as on the node.</b> Godot's undo tracks node metadata, so an
    /// author who saves and then presses Ctrl+Z resurrects the PRE-save stamp, and the next save is
    /// refused as "changed on disk" by this session's own write. The Blender host hit exactly this
    /// (its issue #31) and answered it the same way: the process-side table is not undone, and it
    /// wins.
    /// </para>
    /// </remarks>
    public static class DocumentSession
    {
        /// <summary>The document a scene was materialized from, relative to <c>assets/</c>.</summary>
        public const string DocumentMetaKey = "paradise_document";

        /// <summary>Stamps of documents this process has read or written, by authoring path.</summary>
        private static readonly Dictionary<string, string> Stamps = new(StringComparer.Ordinal);

        /// <summary>Record where a scene came from, and what the file looked like.</summary>
        public static void Remember(Node root, IFileSystem files, UPath document, string authoringPath)
        {
            ArgumentNullException.ThrowIfNull(root);
            root.SetMeta(DocumentMetaKey, authoringPath);
            Stamps[authoringPath] = Stamp(files, document);
        }

        /// <summary>The document an open scene belongs to, or null when it is an ordinary scene.</summary>
        public static string? DocumentOf(Node? root) =>
            root is not null && root.HasMeta(DocumentMetaKey)
                ? root.GetMeta(DocumentMetaKey).AsString()
                : null;

        /// <summary>Whether the file still looks the way it did when this session last read it.
        /// An unknown document is NOT drift: never having read it is not evidence it changed.</summary>
        public static bool IsUnchanged(IFileSystem files, UPath document, string authoringPath) =>
            !Stamps.TryGetValue(authoringPath, out var remembered) ||
            string.Equals(remembered, Stamp(files, document), StringComparison.Ordinal);

        /// <summary>Take the stamp again, after this session has written the file.</summary>
        public static void Restamp(IFileSystem files, UPath document, string authoringPath) =>
            Stamps[authoringPath] = Stamp(files, document);

        private static string Stamp(IFileSystem files, UPath document)
        {
            try
            {
                return files.FileExists(document)
                    ? $"{files.GetLastWriteTime(document).ToUniversalTime().Ticks}:{files.GetFileLength(document)}"
                    : "";
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                // An unreadable stamp must not stop a scene opening. It compares unequal to every
                // real one, so the next save asks the author rather than guessing.
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
#endif
