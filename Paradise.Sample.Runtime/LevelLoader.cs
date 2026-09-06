using System.Linq;
using Paradise.Assets.Mesh;
using Paradise.Authoring;
using Paradise.Export.Data;
using Zio;
using Zio.FileSystems;

namespace Paradise.Sample.Runtime;

/// <summary>What the build cooked for one mesh: its blob, and the material document each draw
/// slot had in the GLB (null for a slot the GLB left unmaterialed) — the slots a scene's own
/// override list stands in for, position by position.</summary>
public sealed record CookedMesh(byte[] Blob, IReadOnlyList<string?> Materials);

/// <summary>Everything loaded from a built tree for one scene: the document read into entities,
/// the material documents, the textures they name, the cooked meshes — the last three keyed by
/// the BUILT path the scene spells — and the project settings.</summary>
/// <param name="Content">The mount everything was read out of, kept so a later reader resolves a
/// field against the same tree rather than against the host's current directory.</param>
/// <param name="DataDir">The root the scene's fields are relative to — the document's
/// grandparent, since a scene lives at <c>&lt;root&gt;/scenes/&lt;name&gt;</c>.</param>
public sealed record RuntimeLevel(
    IFileSystem Content,
    UPath DataDir,
    AuthoredScene Scene,
    Dictionary<string, LevelMaterialData> Materials,
    Dictionary<string, byte[]> Textures,
    Dictionary<string, CookedMesh> Meshes,
    RenderSettingsData RenderSettings,
    PhysicsDynamicsSettingsData PhysicsDynamics)
{
    /// <summary>Spritesheet KTX2 bytes keyed by the built path a sheet field spells; a sheet the
    /// build did not write is absent here (the sprite renders untextured, with a load-time
    /// warning).</summary>
    public Dictionary<string, byte[]> SpriteSheets { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads a built scene and everything it names into memory. Pure I/O + parsing — no GPU, no
/// simulation; fully unit-testable against a memory mount.
/// </summary>
/// <remarks>
/// <b>The runtime never sees glTF, and never derives a path.</b> `paradise assets build` bakes
/// every reference in the scene to the path it wrote — a mesh document to the blob cooked from
/// it, a material to its built form, a texture to its KTX2 — so every string here is opened as
/// spelled. The one path the runtime derives is the seed prefab the build makes of a GLB, found
/// beside the mesh by name (<see cref="PrefabOf"/>): the scene references the mesh, and the GLB's
/// own per-slot materials are recorded on that prefab, not on the mesh.
/// </remarks>
public static class LevelLoader
{
    /// <summary>The component the build's seed prefab records a GLB's per-slot materials in. The
    /// same id as this game's <see cref="MaterialsComponentData"/>, which is how the registry
    /// reads it back regardless of the type name the engine wrote.</summary>
    private static readonly Guid MaterialsComponentId = typeof(MaterialsComponentData).GUID;

    public static RuntimeLevel Load(string scenePath)
    {
        var content = new PhysicalFileSystem();
        return Load(content, content.ConvertPathFromInternal(Path.GetFullPath(scenePath)));
    }

    /// <param name="scenePath">Absolute in <paramref name="content"/>: a UPath is rooted at the
    /// mount, and a relative one would resolve against whoever is asking.</param>
    public static RuntimeLevel Load(IFileSystem content, UPath scenePath)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!scenePath.IsAbsolute)
        {
            throw new ArgumentException($"'{scenePath}' must be absolute in the mount.", nameof(scenePath));
        }

        var sceneDirectory = scenePath.GetDirectory();
        var dataDir = sceneDirectory.GetDirectory();
        if (dataDir.IsNull || dataDir.IsEmpty || dataDir == sceneDirectory)
        {
            throw new InvalidDataException(
                $"Cannot resolve the data root from '{scenePath}': a scene lives at scenes/<name> " +
                "under the root, so the root is its grandparent and there is no room for one here.");
        }

        var document = BuiltDocument.ReadPrefab(content, scenePath);
        // Materialized ONCE, here, through this assembly's generated registry — since v6 there is
        // no engine tier to fall back on, so a game that passes no registry gets nothing back.
        var unresolved = new List<AuthoredComponentData>();
        var scene = AuthoredScene.Read(document, AuthoredComponents.Default, unresolved);
        // The format's own two are read by AuthoredEntity, not by a registry; listing them here
        // would report every object twice for nothing.
        unresolved.RemoveAll(c => c.Id == WellKnownEntityComponents.MetaId || c.Id == WellKnownEntityComponents.TransformId);
        if (unresolved.Count > 0)
        {
            // Loud, because the symptom of a silently dropped payload is "my prop has no
            // collider", which gives nobody anything to go on.
            var names = string.Join(", ", unresolved.Select(c => c.Type ?? c.Id.ToString()).Distinct());
            Console.Error.WriteLine(
                $"[Paradise.Sample.Runtime] {scenePath}: {unresolved.Count} payload(s) no registry could read ({names}).");
        }

        var materials = new Dictionary<string, LevelMaterialData>(StringComparer.Ordinal);
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var meshes = new Dictionary<string, CookedMesh>(StringComparer.Ordinal);
        var spriteSheets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entity in scene.Entities)
        {
            foreach (var slot in entity.Get<MaterialsComponentData>()?.Slots ?? [])
            {
                if (slot is not null) LoadMaterial(content, dataDir, slot, materials, textures);
            }
            if (entity.Get<RenderableComponentData>()?.Mesh is { } meshField)
            {
                var mesh = LoadMesh(content, dataDir, meshField, meshes);
                foreach (var material in mesh.Materials)
                {
                    if (material is not null) LoadMaterial(content, dataDir, material, materials, textures);
                }
            }
            LoadSpriteSheet(content, dataDir, entity.Get<SpriteAnimationComponentData>()?.Sheet, spriteSheets);
            LoadSpriteSheet(content, dataDir, entity.Get<ParticleEmitterComponentData>()?.Sheet, spriteSheets);
        }

        // Absent is normal — a tree without one runs on the contract defaults, which is what every
        // hand-built test layout does. Either form, because which is there is the profile's choice.
        var settingsPath = BuiltDocument.Find(content, dataDir, "ProjectSettings");
        var settings = settingsPath is { } found
            ? BuiltDocument.ReadProjectSettings(content, found)
            : new ProjectSettingsData();
        var physicsDynamics = settings.Physics.Dynamics;
        physicsDynamics.ValidateAndNormalize();

        return new RuntimeLevel(content, dataDir, scene, materials, textures, meshes, settings.Rendering, physicsDynamics)
        {
            SpriteSheets = spriteSheets,
        };
    }

    /// <summary>The seed prefab the build made of the GLB a mesh was cooked from, by name:
    /// <c>models/knight.skinnedmesh</c> → <c>models/knight</c>, in whichever form the profile
    /// writes. The one path the runtime derives.</summary>
    internal static string PrefabOf(string meshPath)
    {
        var dot = meshPath.LastIndexOf('.');
        var slash = meshPath.LastIndexOf('/');
        return dot > slash ? meshPath[..dot] : meshPath;
    }

    private static void LoadMaterial(
        IFileSystem content, UPath dataDir, string field,
        Dictionary<string, LevelMaterialData> materials, Dictionary<string, byte[]> textures)
    {
        if (materials.ContainsKey(field)) return;

        var material = BuiltDocument.ReadMaterial(content, Resolve(content, dataDir, field, "material document"));
        materials[field] = material;

        foreach (var texture in new[]
        {
            material.BaseColorTexture, material.MetallicRoughnessTexture, material.NormalTexture,
            material.OcclusionTexture, material.EmissiveTexture,
        })
        {
            if (texture is { Length: > 0 } && !textures.ContainsKey(texture))
            {
                textures[texture] = content.ReadAllBytes(Resolve(content, dataDir, texture, $"texture of '{field}'"));
            }
        }
    }

    private static void LoadSpriteSheet(IFileSystem content, UPath dataDir, string? field, Dictionary<string, byte[]> spriteSheets)
    {
        if (field is null || spriteSheets.ContainsKey(field)) return;
        var path = dataDir / field;
        if (!content.FileExists(path))
        {
            Console.Error.WriteLine(
                $"[LevelLoader] Spritesheet '{field}' is not in the built tree — run `paradise assets build`. " +
                "Rendering the sprite untextured.");
            return;
        }
        spriteSheets[field] = content.ReadAllBytes(path);
    }

    private static CookedMesh LoadMesh(IFileSystem content, UPath dataDir, string field, Dictionary<string, CookedMesh> meshes)
    {
        if (meshes.TryGetValue(field, out var cached)) return cached;

        var blob = content.ReadAllBytes(Resolve(content, dataDir, field, "mesh"));
        if (!MeshBlobFormat.IsMeshBlob(blob))
        {
            throw new InvalidDataException(
                $"The scene names mesh '{field}', which does not read as a mesh blob. " +
                "Run `paradise assets build` with the engine version this game is built against.");
        }

        return meshes[field] = new CookedMesh(blob, MaterialsOf(content, dataDir, field));
    }

    /// <summary>What material each draw slot of a GLB had, as the build's seed prefab records
    /// them — the documents `paradise assets extract` made from the GLB's own materials. A GLB
    /// never extracted has no prefab and every slot falls to the renderer's fallback; a slot the
    /// GLB left unmaterialed is null.</summary>
    private static IReadOnlyList<string?> MaterialsOf(IFileSystem content, UPath dataDir, string meshPath)
    {
        if (BuiltDocument.Find(content, dataDir, PrefabOf(meshPath)) is not { } prefab) return [];

        foreach (var entity in BuiltDocument.ReadPrefab(content, prefab).Entities)
        {
            foreach (var component in entity)
            {
                if (component.Id != MaterialsComponentId) continue;
                if (component.Data.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !component.Data.TryGetProperty("Slots", out var slots) ||
                    slots.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return [];
                }

                return slots.EnumerateArray()
                    .Select(slot => slot.ValueKind == System.Text.Json.JsonValueKind.String ? slot.GetString() : null)
                    .ToList();
            }
        }

        return [];
    }

    /// <summary>A field as a path in the mount, refusing one that leaves the data root or names
    /// nothing. Both containment checks, because the two ways out fail differently: a field that
    /// CLIMBS is refused by the combine, an ABSOLUTE one is simply taken and lands anywhere in the
    /// mount — and the launcher mounts the whole host file system.</summary>
    private static UPath Resolve(IFileSystem content, UPath root, string field, string kind)
    {
        UPath path;
        try
        {
            path = root / field;
        }
        catch (ArgumentException failure)
        {
            throw new FileNotFoundException(
                $"The scene names {kind} '{field}', which climbs out of the data root at '{root}'.", field, failure);
        }

        if (!path.IsInDirectory(root, recursive: true))
        {
            throw new FileNotFoundException(
                $"The scene names {kind} '{field}', which resolves to '{path}' — outside the data root at '{root}'.", field);
        }

        if (!content.FileExists(path))
        {
            throw new FileNotFoundException(
                $"The scene names {kind} '{field}', which does not exist at '{path}'. Run `paradise assets build`, or restore the missing file.",
                path.FullName);
        }

        return path;
    }
}
