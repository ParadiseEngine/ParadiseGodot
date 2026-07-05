#nullable enable
using System;
using System.IO;

namespace ParadiseExport.Paths
{
    /// <summary>
    /// Resolves export output paths under a repository-root <c>data/</c> directory: scenes to
    /// <c>data/scenes/</c>, materials to <c>data/materials/</c>, prefabs to <c>data/prefabs/</c>.
    ///
    /// Ported from ParadiseUnityEditor's SceneExportPaths, but made engine-neutral: instead of
    /// resolving the root from <c>Application.dataPath</c>, the data directory is supplied by the
    /// engine adapter (the Godot plugin passes the globalized project root + "/data").
    /// </summary>
    public sealed class ExportPaths
    {
        private readonly string _dataDir;
        private readonly string _scenesDir;

        public ExportPaths(string dataDir)
        {
            _dataDir = Path.GetFullPath(dataDir);
            _scenesDir = Path.Combine(_dataDir, "scenes");
        }

        public string DataDir => _dataDir;
        public string ScenesDir => _scenesDir;

        public string GetLevelDataOutputPath(string sceneName) =>
            Path.Combine(_scenesDir, $"{sceneName}.json");

        public string GetNavMeshOutputPath(string sceneName) =>
            Path.Combine(_scenesDir, $"{sceneName}.navmesh.bin");

        public string GetNavMeshFileField(string sceneName) =>
            Path.GetFileName(GetNavMeshOutputPath(sceneName));

        public string GetProjectSettingsOutputPath() =>
            Path.Combine(_dataDir, "ProjectSettings.json");

        /// <summary>Absolute output path for a material/texture field path like
        /// <c>materials/foo.json</c> (resolved under the data directory).</summary>
        public string GetMaterialDataOutputPath(string materialField) =>
            Path.Combine(_dataDir, materialField.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Absolute output path for a prefab field like <c>prefabs/foo.json</c>.</summary>
        public string GetPrefabDataOutputPath(string prefabField) =>
            Path.Combine(_dataDir, prefabField.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Maps a prefab's project path (or name) to its <c>prefabs/&lt;relative&gt;.json</c> contract
        /// field, mirroring the Unity tool's <c>prefabs/</c> layout. A leading <c>res://</c> and a
        /// redundant leading <c>prefabs/</c> are stripped so the field is not double-nested.
        /// </summary>
        public static string PrefabFileField(string prefabPathOrName)
        {
            string normalized = prefabPathOrName.Replace('\\', '/');
            if (normalized.StartsWith("res://", StringComparison.Ordinal))
            {
                normalized = normalized["res://".Length..];
            }

            normalized = normalized.Trim('/');
            if (normalized.Length == 0)
            {
                return "prefabs/prefab.json";
            }

            // Ordinal (not IgnoreCase) to match Godot's lowercase path convention and a
            // case-sensitive VFS: "Prefabs/" is a different directory than "prefabs/".
            if (normalized.StartsWith("prefabs/", StringComparison.Ordinal))
            {
                normalized = normalized["prefabs/".Length..];
            }

            return $"prefabs/{Path.ChangeExtension(normalized, ".json")}";
        }

        /// <summary>
        /// Maps a material's name (or project-relative source path) to its
        /// <c>materials/&lt;name&gt;.json</c> contract field, mirroring the Unity tool's
        /// <c>materials/</c> layout. The field is the stable id stored in entity material slots.
        /// </summary>
        public static string MaterialFileField(string materialNameOrPath)
        {
            string normalized = materialNameOrPath.Replace('\\', '/').Trim('/');
            string name = normalized.Length == 0 ? "material" : Path.GetFileNameWithoutExtension(normalized);
            return $"materials/{name}.json";
        }

        public void EnsureOutputDirectory()
        {
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_scenesDir);
        }
    }
}
