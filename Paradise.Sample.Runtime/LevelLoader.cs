using Paradise.Assets.Gltf;
using ParadiseExport.Data;
using ParadiseExport.Serialization;
using Paradise.Sample.Game.Navigation;
using Paradise.Sample.Game.Navigation.Detour;

namespace Paradise.Sample.Runtime;

/// <summary>Everything loaded from <c>data/</c> for one scene: the level document, resolved
/// material overrides, decoded GLB assets (keyed by contract field), the navmesh, and the
/// project render + physics settings.</summary>
public sealed record RuntimeLevel(
    string DataDir,
    LevelData Level,
    Dictionary<string, LevelMaterialData> Materials,
    Dictionary<string, GltfAsset> MeshAssets,
    INavigationMesh NavigationMesh,
    RenderSettingsData RenderSettings,
    PhysicsDynamicsSettingsData PhysicsDynamics)
{
    /// <summary>Spritesheet KTX2 sidecars keyed by contract sheet field
    /// (e.g. <c>sprites/torch.ktx2</c>); a referenced sheet whose sidecar is missing on disk
    /// is absent here (the sprite renders untextured, with a load-time warning).</summary>
    public Dictionary<string, byte[]> SpriteSheets { get; init; } = new(StringComparer.Ordinal);
}

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
        var spriteSheets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
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
            LoadSpriteSheet(dataDir, entity.Components.SpriteAnimation?.Sheet, spriteSheets);
            LoadSpriteSheet(dataDir, entity.Components.ParticleEmitter?.Sheet, spriteSheets);
        }

        var navMeshFile = level.NavMeshFile
            ?? throw new InvalidDataException("Level document has no NavMeshFile — the runtime needs a navmesh.");
        var navMesh = DetourNavMeshLoader.LoadFromBytes(
            File.ReadAllBytes(Path.Combine(dataDir, "scenes", navMeshFile)));

        var settingsPath = Path.Combine(dataDir, "ProjectSettings.json");
        var projectSettings = File.Exists(settingsPath)
            ? ExportJsonReader.ReadProjectSettings(File.ReadAllText(settingsPath))
            : new ProjectSettingsData();
        var physicsDynamics = projectSettings.Physics.Dynamics;
        physicsDynamics.ValidateAndNormalize();

        return new RuntimeLevel(
            dataDir, level, materials, meshAssets, navMesh, projectSettings.Rendering, physicsDynamics)
        {
            SpriteSheets = spriteSheets,
        };
    }

    private static void LoadMaterial(string dataDir, string field, Dictionary<string, LevelMaterialData> materials)
    {
        if (materials.ContainsKey(field)) return;
        var path = Path.Combine(dataDir, field.Replace('/', Path.DirectorySeparatorChar));
        materials[field] = ExportJsonReader.ReadMaterial(File.ReadAllText(path));
    }

    private static void LoadSpriteSheet(string dataDir, string? field, Dictionary<string, byte[]> spriteSheets)
    {
        if (field is null || spriteSheets.ContainsKey(field)) return;
        var path = Path.Combine(dataDir, field.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine(
                $"[LevelLoader] Spritesheet '{field}' has no KTX2 sidecar under data/ — run the " +
                "editor's Paradise/Convert data GLBs → KTX2 pass (or PARADISE_CONVERT_DATA_GLBS=1). " +
                "Rendering the sprite untextured.");
            return;
        }
        spriteSheets[field] = File.ReadAllBytes(path);
    }

    private static void LoadMesh(string dataDir, string field, Dictionary<string, GltfAsset> meshAssets)
    {
        if (meshAssets.ContainsKey(field)) return;
        var path = Path.Combine(dataDir, field.Replace('/', Path.DirectorySeparatorChar));
        // External-KTX2 textures are sidecar .ktx2 files next to the GLB; resolve image URIs
        // relative to the GLB's directory.
        var glbDir = Path.GetDirectoryName(path)!;
        meshAssets[field] = GltfSceneReader.Read(
            File.ReadAllBytes(path),
            uri => File.ReadAllBytes(Path.Combine(glbDir, uri.Replace('/', Path.DirectorySeparatorChar))));
    }
}
