#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Godot;
using Paradise.Assets.Project;
using Zio;
using Zio.FileSystems;

namespace ParadiseGodot.Project
{
    /// <summary>The asset project edited by the open Godot project.</summary>
    /// <remarks>Read source documents, sidecars and schema through <see cref="Mounts"/>. Its
    /// <c>/assets</c> mount is read-only to protect build inputs; document saves use <see cref="Files"/>
    /// and physical <see cref="Paths"/> explicitly. Only <see cref="TryOpen"/> globalizes Godot paths;
    /// downstream filesystem access uses Zio.</remarks>
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

        public AssetProjectPaths Paths { get; }

        public AssetProjectLayout Layout => Paths.Layout;

        /// <summary>The physical filesystem for absolute paths returned by <see cref="Paths"/>.</summary>
        public IFileSystem Files => _physical;

        /// <summary>Read-only <c>/assets</c> and writable <c>/cache</c> and <c>/play</c> mounts.</summary>
        /// <remarks>The editor launches its own play output; shipping builds belong to the CLI.</remarks>
        public IFileSystem Mounts => _mounts;

        /// <summary>Find the asset project at or above the open Godot project.</summary>
        /// <param name="project">The opened project, or null.</param>
        /// <param name="problem">An author-facing failure reason.</param>
        /// <remarks>Require <c>assets/project.toml</c>; an unrelated assets folder is not a project.</remarks>
        public static bool TryOpen([NotNullWhen(true)] out ParadiseProject? project, out string? problem)
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

        /// <summary>Add <c>.gdignore</c> to asset, build and editor trees inside the Godot project.</summary>
        /// <remarks>Engine <c>.mesh</c>/<c>.material</c> documents collide with Godot resource extensions.
        /// Create derived trees early so ignores exist before the first build. Trees outside
        /// <c>res://</c> need no marker because Godot does not scan them.</remarks>
        /// <returns>Paths written, for the caller to report.</returns>
        public IReadOnlyList<string> EnsureGodotIgnores()
        {
            var written = new List<string>();
            foreach (var directory in new[] { Layout.Assets, Layout.Build, Layout.Editor })
            {
                var marker = directory / ".gdignore";
                if (Paths.ToResourcePath(marker) is null || _physical.FileExists(marker)) continue;
                _physical.CreateDirectory(directory);
                _physical.WriteAllText(marker, "");
                written.Add(_physical.ConvertPathToInternal(marker));
            }
            return written;
        }

        public void Dispose()
        {
            _mounts.Dispose();
            _physical.Dispose();
        }
    }
}
#endif
