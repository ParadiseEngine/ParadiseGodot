#if TOOLS
using System;
using Godot;
using Paradise.Assets.Project;
using Zio;
using Zio.FileSystems;

namespace ParadiseGodot.Project
{
    /// <summary>
    /// The asset project the open Godot project is editing: where it is, and the mounted file
    /// system every other part of the addon reads and writes it through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>assets/</c> is the source of truth and the addon never reads the build output.</b>
    /// Documents, sidecars and the schema are reached through <see cref="Mounts"/> — <c>/assets</c>
    /// read-only, <c>/cache</c> and <c>/play</c> writable — rather than through res:// or a host
    /// path, so the same code runs against a real project here and against a MemoryFileSystem in a
    /// test.
    /// </para>
    /// <para>
    /// <c>/assets</c> is mounted READ-ONLY by <see cref="ProjectMounts"/>, which is a guard rather
    /// than an inconvenience: a consumer that writes sources through the build's own mount has made
    /// the build unreproducible. Saving a document writes through <see cref="Files"/> at the path
    /// <see cref="Paths"/> resolves, which is the one place that is allowed to.
    /// </para>
    /// <para>
    /// The Godot edge lives here and nowhere else — <see cref="ProjectSettings.GlobalizePath"/> is
    /// called once, in <see cref="TryOpen"/>. Everything downstream is Zio.
    /// </para>
    /// </remarks>
    public sealed class ParadiseProject : IDisposable
    {
        private readonly PhysicalFileSystem _physical;
        private readonly MountFileSystem _mounts;

        private ParadiseProject(PhysicalFileSystem physical, MountFileSystem mounts, AssetProjectPaths paths)
        {
            _physical = physical;
            _mounts = mounts;
            Paths = paths;
        }

        /// <summary>Path arithmetic for this project.</summary>
        public AssetProjectPaths Paths { get; }

        /// <summary>The project's directory layout.</summary>
        public AssetProjectLayout Layout => Paths.Layout;

        /// <summary>The physical file system the project lives in. Absolute
        /// <see cref="UPath"/>s from <see cref="Paths"/> are resolved against this.</summary>
        public IFileSystem Files => _physical;

        /// <summary>
        /// <c>/assets</c>, <c>/cache</c> and <c>/play</c>.
        /// </summary>
        /// <remarks><c>/play</c> rather than <c>/build</c>: the editor's own output is what it
        /// launches, and a shipping build is the CLI's to write.</remarks>
        public IFileSystem Mounts => _mounts;

        /// <summary>
        /// Locate the asset project at or above the open Godot project.
        /// </summary>
        /// <param name="project">The opened project, or null.</param>
        /// <param name="problem">Why it could not be opened, phrased for an author.</param>
        /// <remarks>The marker walked for is <c>assets/project.toml</c> — the FILE, because a game
        /// repo can easily hold some other <c>assets</c> folder.</remarks>
        public static bool TryOpen(out ParadiseProject? project, out string? problem)
        {
            var physical = new PhysicalFileSystem();
            try
            {
                var godotRoot = physical.ConvertPathFromInternal(
                    System.IO.Path.GetFullPath(ProjectSettings.GlobalizePath(AssetProjectPaths.ResourceScheme)));

                if (!AssetProjectLayout.TryLocate(physical, godotRoot, out var layout))
                {
                    project = null;
                    problem =
                        $"No Paradise asset project at or above '{physical.ConvertPathToInternal(godotRoot)}'. " +
                        $"Expected an '{AssetProjectLayout.AssetsDirectoryName}/" +
                        $"{AssetProjectLayout.ManifestFileName}' here or in a parent directory; " +
                        "create one with `paradise new`.";
                    physical.Dispose();
                    return false;
                }

                var mounts = ProjectMounts.Create(physical, layout!, ProjectOutputTarget.Play);
                project = new ParadiseProject(physical, mounts, new AssetProjectPaths(godotRoot, layout!));
                problem = null;
                return true;
            }
            catch (Exception failure) when (
                failure is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
                physical.Dispose();
                project = null;
                problem = $"Could not open the asset project: {failure.Message}";
                return false;
            }
        }

        public void Dispose()
        {
            _mounts.Dispose();
            _physical.Dispose();
        }
    }
}
#endif
