using Paradise.Assets.Gltf;
using ParadiseExport.Data;
using ParadiseExport.Serialization;
using ParadiseGame.Navigation;
using ParadiseGame.Navigation.Detour;

namespace ParadiseRuntime;

/// <summary>Everything loaded from <c>data/</c> for one scene: the level document, resolved
/// material overrides, decoded GLB assets (keyed by contract field), the navmesh, and the
/// project render settings.</summary>
public sealed record RuntimeLevel(
    string DataDir,
    LevelData Level,
    Dictionary<string, LevelMaterialData> Materials,
    Dictionary<string, GltfAsset> MeshAssets,
    INavigationMesh NavigationMesh,
    RenderSettingsData RenderSettings);

/// <summary>Reads the engine-neutral export (scene JSON + materials + meshes + navmesh) into
/// memory. Pure I/O + parsing — no GPU, no simulation; fully unit-testable.</summary>
public static class LevelLoader
{
    public static RuntimeLevel Load(string scenePath)
    {
        var sceneFullPath = Path.GetFullPath(scenePath);
        if (!File.Exists(sceneFullPath))
            throw new FileNotFoundException($"Scene document not found: {sceneFullPath}", sceneFullPath);
        // data/scenes/<scene>.json → data/
        var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(sceneFullPath))
            ?? throw new InvalidOperationException($"Cannot resolve the data directory from '{sceneFullPath}'.");

        var level = ExportJsonReader.ReadLevel(File.ReadAllText(sceneFullPath));

        var materials = new Dictionary<string, LevelMaterialData>(StringComparer.Ordinal);
        var meshAssets = new Dictionary<string, GltfAsset>(StringComparer.Ordinal);
        foreach (var entity in level.Entities)
        {
            foreach (var slot in entity.Materials)
            {
                if (slot is not null) LoadMaterial(dataDir, slot, materials);
            }
            if (entity.Components.Renderable?.Mesh is { } meshField)
            {
                LoadMesh(dataDir, meshField, meshAssets);
            }
        }
        if (level.EnvironmentMesh is { } environmentField)
        {
            LoadMesh(dataDir, environmentField, meshAssets);
        }

        var navMeshFile = level.NavMeshFile
            ?? throw new InvalidDataException("Level document has no NavMeshFile — the runtime needs a navmesh.");
        var navMesh = DetourNavMeshLoader.LoadFromBytes(
            File.ReadAllBytes(Path.Combine(dataDir, "scenes", navMeshFile)));

        var settingsPath = Path.Combine(dataDir, "ProjectSettings.json");
        var renderSettings = File.Exists(settingsPath)
            ? ExportJsonReader.ReadProjectSettings(File.ReadAllText(settingsPath)).Rendering
            : new RenderSettingsData();

        return new RuntimeLevel(dataDir, level, materials, meshAssets, navMesh, renderSettings);
    }

    private static void LoadMaterial(string dataDir, string field, Dictionary<string, LevelMaterialData> materials)
    {
        if (materials.ContainsKey(field)) return;
        var path = Path.Combine(dataDir, field.Replace('/', Path.DirectorySeparatorChar));
        materials[field] = ExportJsonReader.ReadMaterial(File.ReadAllText(path));
    }

    private static void LoadMesh(string dataDir, string field, Dictionary<string, GltfAsset> meshAssets)
    {
        if (meshAssets.ContainsKey(field)) return;
        var path = Path.Combine(dataDir, field.Replace('/', Path.DirectorySeparatorChar));
        meshAssets[field] = GltfSceneReader.Read(File.ReadAllBytes(path));
    }
}
