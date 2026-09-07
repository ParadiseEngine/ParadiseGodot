#if TOOLS
using System;
using System.Globalization;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// What the document looked like when a working file was last built from it, kept beside the
    /// working file so the answer survives the editor closing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the <c>.tscn</c> worth keeping. It is still derived — delete it and the
    /// next open rebuilds it — but it is no longer rebuilt UNCONDITIONALLY, and that is the whole
    /// difference between a Godot node an author added surviving the next open and being silently
    /// replaced by a fresh materialization of the document.
    /// </para>
    /// <para>
    /// Separate from <see cref="DocumentSession"/>'s stamp, which answers a different question in
    /// a different lifetime: that one is per-process and guards the SAVE against a document
    /// something else changed. This one is on disk and guards the OPEN against rebuilding a
    /// working file that is already current. They are refreshed at the same moments and must not
    /// be merged — the session stamp is deliberately not persisted, because Godot's undo would
    /// resurrect an old one and make a save refuse itself.
    /// </para>
    /// </remarks>
    public static class WorkfileStamp
    {
        private const string Suffix = ".stamp";

        /// <summary>Whether the working file exists and was built from the document as it now is.</summary>
        public static bool IsCurrent(IFileSystem files, UPath workfile, UPath document)
        {
            ArgumentNullException.ThrowIfNull(files);

            if (!files.FileExists(workfile)) return false;

            var stamp = Read(files, workfile);
            return stamp is not null && string.Equals(stamp, Of(files, document), StringComparison.Ordinal);
        }

        /// <summary>Record that the working file now matches the document.</summary>
        public static void Write(IFileSystem files, UPath workfile, UPath document)
        {
            ArgumentNullException.ThrowIfNull(files);

            try
            {
                var path = PathFor(workfile);
                var directory = path.GetDirectory();
                if (!directory.IsNull && !files.DirectoryExists(directory)) files.CreateDirectory(directory);
                files.WriteAllText(path, Of(files, document));
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                // A stamp that cannot be written costs a rebuild on the next open, which is what
                // used to happen every time. Never worth failing an open or a save over.
            }
        }

        /// <summary>Forget the record, so the next open rebuilds.</summary>
        public static void Clear(IFileSystem files, UPath workfile)
        {
            ArgumentNullException.ThrowIfNull(files);

            try
            {
                var path = PathFor(workfile);
                if (files.FileExists(path)) files.DeleteFile(path);
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }

        private static UPath PathFor(UPath workfile) => workfile.FullName + Suffix;

        private static string? Read(IFileSystem files, UPath workfile)
        {
            try
            {
                var path = PathFor(workfile);
                return files.FileExists(path) ? files.ReadAllText(path).Trim() : null;
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The document's <c>(last write, length)</c>, spelled as
        /// <see cref="DocumentSession"/> spells it so the two stay comparable by eye in a log.</summary>
        private static string Of(IFileSystem files, UPath document)
        {
            try
            {
                return files.FileExists(document)
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{files.GetLastWriteTime(document).ToUniversalTime().Ticks}:{files.GetFileLength(document)}")
                    : "";
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                // Unequal to every real stamp, so an unreadable document rebuilds rather than
                // being trusted.
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
#endif
