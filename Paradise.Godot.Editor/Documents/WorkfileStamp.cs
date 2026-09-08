#if TOOLS
using System;
using System.Globalization;
using Zio;

namespace ParadiseGodot.Documents
{
    /// <summary>Persists the source stamp beside a working file to avoid unnecessary rebuilds.</summary>
    /// <remarks>Keep this separate from <see cref="DocumentSession"/>: the persisted stamp guards
    /// opening a current workfile, while process-local stamps guard saves against external edits.
    /// Persisting save guards in node metadata would let Godot undo restore stale stamps.</remarks>
    public static class WorkfileStamp
    {
        private const string Suffix = ".stamp";

        /// <summary>Whether the existing workfile matches the current source document.</summary>
        public static bool IsCurrent(IFileSystem files, UPath workfile, UPath document)
        {
            ArgumentNullException.ThrowIfNull(files);

            if (!files.FileExists(workfile)) return false;

            var stamp = Read(files, workfile);
            return stamp is not null && string.Equals(stamp, SourceStamp(files, document), StringComparison.Ordinal);
        }

        /// <summary>Record that the workfile matches the document.</summary>
        public static void Write(IFileSystem files, UPath workfile, UPath document)
        {
            ArgumentNullException.ThrowIfNull(files);

            try
            {
                var path = PathFor(workfile);
                var directory = path.GetDirectory();
                if (!directory.IsNull) files.CreateDirectory(directory);
                files.WriteAllText(path, SourceStamp(files, document));
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                // Stamp failure only requires a future rebuild; it must not fail the open or save.
            }
        }

        /// <summary>Invalidate the stamp to force a rebuild on the next open.</summary>
        public static void Clear(IFileSystem files, UPath workfile)
        {
            ArgumentNullException.ThrowIfNull(files);

            try
            {
                files.DeleteFile(PathFor(workfile));
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
                return files.ReadAllText(PathFor(workfile)).Trim();
            }
            catch (Exception failure) when (failure is System.IO.IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Source state shared by session save guards and persisted workfile freshness checks.</summary>
        internal static string SourceStamp(IFileSystem files, UPath document)
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
                // An unreadable document must fail the freshness check.
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
#endif
