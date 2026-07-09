using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using ParadiseExport.Data;
using ParadiseExport.Geometry;
using ParadiseGame;
using ParadiseGame.Physics;

namespace ParadiseRuntime;

/// <summary>One simulated, rendered entity: the sim handle paired with its render instance.
/// Static scenery has no sim entity (null) — its transform never changes.</summary>
public sealed record RuntimeInstance(
    Entity? SimEntity,
    PbrInstance Render);

/// <summary>Builds the runtime world from a loaded level: the static CollisionWorld (from data,
/// not Godot nodes — the JSON-sourced analog of EcsSceneBridge.BuildCollisionWorld), the
/// simulation spawns (Agent → SpawnAgent, Rigidbody.Dynamic + sphere → SpawnBall), and the PBR
/// render instances with slot-wise material overrides.</summary>
public static class SceneAssembler
{
    private const float DefaultArriveRadius = 0.25f; // AgentComponentData carries no arrive radius (schema v3 candidate)

    /// <summary>Contract matrices are column-vector layout; transpose yields the
    /// System.Numerics row-vector model matrix everything downstream uses.</summary>
    public static Matrix4x4 ToModelMatrix(Matrix4x4? contractMatrix) =>
        contractMatrix is { } m ? Matrix4x4.Transpose(m) : Matrix4x4.Identity;

    // -------- collision --------

    public static CollisionWorld? BuildCollisionWorld(LevelData level)
    {
        var colliders = new List<Collider>();
        var transforms = new List<RigidTransform>();

        foreach (var entity in level.Entities)
        {
            // Only truly static bodies join the static world — kinematic agents and dynamic
            // balls are simulated, exactly like the Godot bridge's navigation_source harvest.
            if (entity.Components.Rigidbody?.BodyType != PhysicsBodyType.Static) continue;
            var model = ToModelMatrix(entity.WorldMatrix);
            foreach (var shape in entity.Components.Collider?.Colliders ?? [])
            {
                AppendCollider(shape, model, colliders, transforms);
            }
        }

        if (colliders.Count == 0) return null;
        return CollisionWorld.Build(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colliders),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(transforms));
    }

    /// <summary>Scale-free pose of a possibly-scaled model matrix. Rotation MUST come from a
    /// decomposition — Quaternion.CreateFromRotationMatrix assumes an orthonormal basis and is
    /// not scale-invariant (even uniform scale yields a non-unit quaternion).</summary>
    public static (Vector3 Position, Quaternion Rotation) DecomposePose(in Matrix4x4 model)
    {
        if (Matrix4x4.Decompose(model, out _, out var rotation, out var translation))
        {
            return (translation, rotation);
        }
        // Degenerate (zero/sheared) basis: keep the position, best-effort unit rotation.
        return (model.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(model)));
    }

    private static Vector3 OwnerScale(in Matrix4x4 ownerModel) =>
        ownerModel.IsIdentity || !Matrix4x4.Decompose(ownerModel, out var scale, out _, out _)
            ? Vector3.One
            : scale;

    private static void AppendCollider(
        ColliderShapeData shape, in Matrix4x4 ownerModel, List<Collider> colliders, List<RigidTransform> transforms)
    {
        var filter = new CollisionFilter { BelongsTo = 1u << shape.Layer, CollidesWith = ~0u };

        // Exported dimensions only fold the collider's scale RELATIVE to its entity root
        // (ColliderScaleFold at export time); the root's own scale arrives via the contract
        // matrix and folds in here with the same rules. Shapes rotated against the scaled axes
        // share export-time folding's axis-mapping approximation.
        var ownerScale = OwnerScale(ownerModel);
        Collider collider;
        switch (shape.ShapeType)
        {
            case PhysicsShapeType.Box:
                collider = Collider.CreateBox(ColliderScaleFold.BoxSize(shape.Size, ownerScale) * 0.5f, filter);
                break;
            case PhysicsShapeType.Sphere:
                collider = Collider.CreateSphere(ColliderScaleFold.SphereRadius(shape.Radius, ownerScale), filter);
                break;
            case PhysicsShapeType.Capsule:
                var radius = ColliderScaleFold.CapsuleRadius(shape.Radius, ownerScale);
                var height = ColliderScaleFold.CapsuleHeight(shape.Height, ownerScale);
                collider = Collider.CreateCapsule(radius, MathF.Max(0f, height * 0.5f - radius), filter);
                break;
            default:
                return;
        }

        // Row-vector composition: collider local pose × owner model. The full matrix transforms
        // LocalCenter (owner scale displaces it — the exporter stores it in the root's unscaled
        // local space), while the pose rotation comes from a scale-free decomposition.
        var local = Matrix4x4.CreateFromQuaternion(shape.LocalRotation)
            * Matrix4x4.CreateTranslation(shape.LocalCenter);
        var world = local * ownerModel;
        var (position, rotation) = DecomposePose(world);

        colliders.Add(collider);
        transforms.Add(new RigidTransform(position, rotation));
    }

    // -------- simulation spawns + render instances --------

    public sealed record AssembledScene(
        List<RuntimeInstance> Instances,
        Entity? Player);

    /// <summary>Spawn sim entities and build render instances. Must run on the runner's owner
    /// thread BEFORE <c>runner.Start()</c> (world-pool thread affinity).</summary>
    public static AssembledScene Assemble(RuntimeLevel level, SimulationRunner runner, PbrRenderer pbr)
    {
        var geometry = new GeometryCache(pbr);
        var instances = new List<RuntimeInstance>();
        Entity? player = null;

        foreach (var entity in level.Level.Entities)
        {
            var model = ToModelMatrix(entity.WorldMatrix);
            PbrInstance? render = null;
            if (entity.Components.Renderable?.Mesh is { } meshField)
            {
                var mesh = geometry.InstantiateMesh(level.MeshAssets[meshField], entity.Materials, level);
                render = new PbrInstance { Mesh = mesh, Model = model };
            }

            Entity? simEntity = null;
            var (position, rotation) = DecomposePose(model);
            var components = entity.Components;
            if (components.Agent is { } agent)
            {
                var capsule = FindShape(components, PhysicsShapeType.Capsule);
                var radius = capsule?.Radius ?? 0.4f;
                var halfLength = capsule is { } c ? MathF.Max(0f, c.Height * 0.5f - c.Radius) : 0.5f;
                var spawned = runner.SpawnAgent(
                    position, rotation,
                    agent.MoveSpeed, DefaultArriveRadius,
                    radius, halfLength);
                simEntity = spawned;
                player ??= spawned; // first agent is the player (bridge convention)
            }
            else if (components.Rigidbody?.BodyType == PhysicsBodyType.Dynamic)
            {
                var sphere = FindShape(components, PhysicsShapeType.Sphere);
                var radius = sphere?.Radius ?? 0.35f;
                simEntity = runner.SpawnBall(position, rotation, radius, Math.Max(0.01f, components.Rigidbody.Mass));
            }

            if (render is not null)
            {
                instances.Add(new RuntimeInstance(simEntity, render));
            }
        }

        return new AssembledScene(instances, player);
    }

    private static ColliderShapeData? FindShape(EntityComponentsData components, PhysicsShapeType type)
    {
        foreach (var shape in components.Collider?.Colliders ?? [])
        {
            if (shape.ShapeType == type) return shape;
        }
        return null;
    }

    // -------- lights / ambient --------

    public static void PopulateLighting(RuntimeLevel level, PbrScene scene)
    {
        var state = level.Level.Lighting?.ResolveActiveState();
        if (state is null) return;

        var environment = state.Environment;
        scene.Ambient = new PbrAmbient
        {
            Sky = ToVector3(environment.AmbientColor),
            Equator = ToVector3(environment.AmbientEquatorColor),
            Ground = ToVector3(environment.AmbientGroundColor),
            // Ambient energy drives the hemisphere strength (Godot ambient_light_energy).
            Exposure = environment.AmbientEnergy,
            Flat = !string.Equals(environment.AmbientMode, "Skybox", StringComparison.OrdinalIgnoreCase),
        };
        // Background/clear tone from the environment (the sky) so the .NET background matches Godot —
        // but only when a real WorldEnvironment was exported. A default EnvironmentData must not stomp
        // the camera-derived clear (which RuntimeLoop set before calling this).
        if (environment.HasBackground)
        {
            var bg = environment.BackgroundColor;
            scene.ClearColor = new ColorRgba(bg.R, bg.G, bg.B, 1f);
        }
        scene.Tonemap = new PbrTonemap
        {
            Mode = ParseTonemapMode(environment.TonemapMode),
            Exposure = environment.TonemapExposure,
            White = environment.TonemapWhite,
        };

        foreach (var light in state.Lights)
        {
            if (!light.Enabled) continue;
            scene.Lights.Add(new PbrLight
            {
                Type = light.Type switch
                {
                    "Point" => PbrLightType.Point,
                    "Spot" => PbrLightType.Spot,
                    _ => PbrLightType.Directional,
                },
                Position = light.Position,
                // Contract stores the light's aim (forward); the shader wants surface→light.
                Direction = Vector3.Normalize(-light.Direction),
                Color = ToVector3(light.Color),
                Intensity = light.Intensity,
                Range = light.Range,
                SpotOuterDegrees = light.SpotAngle,
                SpotInnerDegrees = light.InnerSpotAngle,
                // Real-time shadows — the engine casts from directional, spot, and point lights via
                // its shadow atlas. Soft (5-tap PCF) whenever shadows are on; the contract carries no
                // hard/soft flag yet, so it's not data-driven.
                CastsShadows = light.ShadowsEnabled,
                ShadowStrength = light.ShadowStrength,
                SoftShadows = light.ShadowsEnabled,
            });
        }
    }

    private static Vector3 ToVector3(Color32 color) => new(color.R, color.G, color.B);

    // Map the exported tonemap name (Godot's ToneMapper enum: Linear/Reinhardt/Filmic/Aces/Agx) to
    // the engine's operator. Case-insensitive; unknown values fall back to Linear (no tonemap).
    private static PbrTonemapMode ParseTonemapMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "reinhard" or "reinhardt" => PbrTonemapMode.Reinhard,
        "filmic" => PbrTonemapMode.Filmic,
        "aces" => PbrTonemapMode.Aces,
        "agx" => PbrTonemapMode.Agx,
        _ => PbrTonemapMode.Linear,
    };

    // -------- materials --------

    public static bool HasAnyTexture(in GltfMaterialData material) =>
        material.BaseColorImage >= 0 || material.MetallicRoughnessImage >= 0 ||
        material.NormalImage >= 0 || material.OcclusionImage >= 0 || material.EmissiveImage >= 0;

    /// <summary>A slot-override material: the override JSON's FACTORS over the GLB material's
    /// TEXTURES (glTF factor × texture — Godot parity for surface_material_override with
    /// textured materials).</summary>
    public static GltfMaterialData BuildSlotOverrideMaterial(LevelMaterialData data, in GltfMaterialData glbMaterial) =>
        ToGltfMaterial(data) with
        {
            BaseColorImage = glbMaterial.BaseColorImage,
            MetallicRoughnessImage = glbMaterial.MetallicRoughnessImage,
            NormalImage = glbMaterial.NormalImage,
            OcclusionImage = glbMaterial.OcclusionImage,
            EmissiveImage = glbMaterial.EmissiveImage,
            BaseColorUvTransform = glbMaterial.BaseColorUvTransform,
        };

    /// <summary>Level material JSON → the renderer's material shape (factors only — texture
    /// paths in material documents reference Godot-project SOURCE files, not runtime assets;
    /// the texturing route is GLB-embedded KTX2, inherited by slot overrides via
    /// <see cref="BuildSlotOverrideMaterial"/>).</summary>
    private static GltfMaterialData ToGltfMaterial(LevelMaterialData data) => new(
        Name: data.Name,
        BaseColorFactor: new Vector4(data.BaseColorFactor.R, data.BaseColorFactor.G, data.BaseColorFactor.B, data.BaseColorFactor.A),
        MetallicFactor: data.MetallicFactor,
        RoughnessFactor: data.RoughnessFactor,
        EmissiveFactor: new Vector3(data.EmissiveFactor.R, data.EmissiveFactor.G, data.EmissiveFactor.B),
        NormalScale: data.NormalScale,
        OcclusionStrength: data.OcclusionStrength,
        TransmissionFactor: data.TransmissionFactor,
        AlphaMode: data.AlphaMode switch
        {
            "Blend" => GltfAlphaMode.Blend,
            "Mask" => GltfAlphaMode.Mask,
            _ => GltfAlphaMode.Opaque,
        },
        AlphaCutoff: 0.5f,
        DoubleSided: false,
        BaseColorImage: -1,
        MetallicRoughnessImage: -1,
        NormalImage: -1,
        OcclusionImage: -1,
        EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);

    // -------- geometry/material caches --------

    /// <summary>Uploads each GLB's geometry once and shares the buffers across entities; each
    /// entity's mesh clones the primitive records with slot-override material ids. GLB slot
    /// order == Materials order is the schema-v2 contract rule.</summary>
    private sealed class GeometryCache(PbrRenderer pbr)
    {
        private readonly Dictionary<GltfAsset, (PbrPrimitive Primitive, Matrix4x4 Bake, int GlbMaterialId, int GlbMaterialIndex)[]> _uploaded = new();
        private readonly Dictionary<(GltfAsset? Asset, int MaterialIndex, string Field), int> _levelMaterialIds = new();

        public PbrMesh InstantiateMesh(GltfAsset asset, IReadOnlyList<string?> slotOverrides, RuntimeLevel level)
        {
            var uploaded = Upload(asset);
            var primitives = new PbrPrimitive[uploaded.Length];
            for (var i = 0; i < uploaded.Length; i++)
            {
                var (primitive, _, glbMaterialId, glbMaterialIndex) = uploaded[i];
                var overrideField = i < slotOverrides.Count ? slotOverrides[i] : null;
                primitives[i] = overrideField is null
                    ? primitive with { MaterialId = glbMaterialId }
                    : primitive with { MaterialId = ResolveLevelMaterial(overrideField, level, asset, glbMaterialIndex) };
            }
            return new PbrMesh(primitives);
        }

        private (PbrPrimitive Primitive, Matrix4x4 Bake, int GlbMaterialId, int GlbMaterialIndex)[] Upload(GltfAsset asset)
        {
            if (_uploaded.TryGetValue(asset, out var cached)) return cached;

            // Register the GLB's own materials (used for null slots / the environment).
            var glbMaterialIds = new int[asset.Materials.Length];
            for (var i = 0; i < asset.Materials.Length; i++)
            {
                glbMaterialIds[i] = pbr.Materials.AddMaterial(in asset.Materials[i], asset.Images);
            }
            var fallback = -1;

            var list = new List<(PbrPrimitive, Matrix4x4, int, int)>();
            foreach (var instance in asset.Instances)
            {
                foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
                {
                    // Bake the GLB node transform into the vertices so one PbrInstance model
                    // matrix per entity is enough (entity GLBs are entity-local by contract).
                    var vertices = BakeTransform(primitive.Vertices, instance.WorldTransform);
                    var materialId = primitive.MaterialIndex >= 0
                        ? glbMaterialIds[primitive.MaterialIndex]
                        : (fallback >= 0 ? fallback : fallback = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f)));
                    list.Add((pbr.UploadPrimitive(vertices, primitive.Indices, materialId), instance.WorldTransform, materialId, primitive.MaterialIndex));
                }
            }

            var result = list.ToArray();
            _uploaded[asset] = result;
            return result;
        }

        private int ResolveLevelMaterial(string field, RuntimeLevel level, GltfAsset asset, int glbMaterialIndex)
        {
            // Slot overrides carry the FACTORS; textures stay with the GLB's own material
            // (glTF semantics: factor × texture — the Godot-parity behaviour for
            // surface_material_override with textured materials). The cache key includes the
            // texture source so the same override JSON over differently-textured primitives
            // yields distinct GPU materials.
            var inherit = glbMaterialIndex >= 0 && HasAnyTexture(asset.Materials[glbMaterialIndex]);
            var key = inherit ? (asset, glbMaterialIndex, field) : ((GltfAsset?)null, -1, field);
            if (_levelMaterialIds.TryGetValue(key, out var id)) return id;

            var material = inherit
                ? BuildSlotOverrideMaterial(level.Materials[field], in asset.Materials[glbMaterialIndex])
                : ToGltfMaterial(level.Materials[field]);
            var images = inherit ? asset.Images : [];

            id = pbr.Materials.AddMaterial(in material, images);
            _levelMaterialIds[key] = id;
            return id;
        }

        private static float[] BakeTransform(float[] vertices, in Matrix4x4 transform)
        {
            if (transform.IsIdentity) return vertices;
            var baked = new float[vertices.Length];
            Matrix4x4.Invert(transform, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            for (var i = 0; i < vertices.Length; i += GltfPrimitive.FloatsPerVertex)
            {
                var position = Vector3.Transform(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]), transform);
                var normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]), normalMatrix));
                var tangent = Vector3.TransformNormal(new Vector3(vertices[i + 8], vertices[i + 9], vertices[i + 10]), transform);
                baked[i] = position.X; baked[i + 1] = position.Y; baked[i + 2] = position.Z;
                baked[i + 3] = normal.X; baked[i + 4] = normal.Y; baked[i + 5] = normal.Z;
                baked[i + 6] = vertices[i + 6]; baked[i + 7] = vertices[i + 7];
                var tangentLength = tangent.Length();
                if (tangentLength > 1e-6f) tangent /= tangentLength;
                baked[i + 8] = tangent.X; baked[i + 9] = tangent.Y; baked[i + 10] = tangent.Z;
                baked[i + 11] = vertices[i + 11];
            }
            return baked;
        }
    }
}
