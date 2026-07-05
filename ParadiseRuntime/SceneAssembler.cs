using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Rendering.Pbr;
using ParadiseExport.Data;
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

        foreach (var shape in level.StaticColliders)
        {
            AppendCollider(shape, Matrix4x4.Identity, colliders, transforms);
        }

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

    private static void AppendCollider(
        ColliderShapeData shape, in Matrix4x4 ownerModel, List<Collider> colliders, List<RigidTransform> transforms)
    {
        var filter = new CollisionFilter { BelongsTo = 1u << shape.Layer, CollidesWith = ~0u };
        Collider collider;
        switch (shape.ShapeType)
        {
            case PhysicsShapeType.Box:
                collider = Collider.CreateBox(shape.Size * 0.5f, filter);
                break;
            case PhysicsShapeType.Sphere:
                collider = Collider.CreateSphere(shape.Radius, filter);
                break;
            case PhysicsShapeType.Capsule:
                collider = Collider.CreateCapsule(shape.Radius, MathF.Max(0f, shape.Height * 0.5f - shape.Radius), filter);
                break;
            default:
                return;
        }

        // Row-vector composition: collider local pose × owner model. Scale is already folded
        // into the shape dimensions by the exporter.
        var local = Matrix4x4.CreateFromQuaternion(shape.LocalRotation)
            * Matrix4x4.CreateTranslation(shape.LocalCenter);
        var world = local * ownerModel;

        colliders.Add(collider);
        transforms.Add(new RigidTransform(world.Translation, Quaternion.CreateFromRotationMatrix(world)));
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

        if (level.Level.EnvironmentMesh is { } environmentField)
        {
            var mesh = geometry.InstantiateMesh(level.MeshAssets[environmentField], slotOverrides: [], level);
            instances.Add(new RuntimeInstance(null, new PbrInstance { Mesh = mesh }));
        }

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
            var position = model.Translation;
            var rotation = Quaternion.CreateFromRotationMatrix(model);
            var components = entity.Components;
            if (components.Agent is { } agent)
            {
                var capsule = FindShape(components, PhysicsShapeType.Capsule);
                var radius = capsule?.Radius ?? 0.4f;
                var halfLength = capsule is { } c ? MathF.Max(0f, c.Height * 0.5f - c.Radius) : 0.5f;
                var spawned = runner.SpawnAgent(
                    position, rotation,
                    agent.MoveSpeed, agent.AngularSpeed, DefaultArriveRadius,
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
            Exposure = environment.Exposure,
            Flat = !string.Equals(environment.AmbientMode, "Skybox", StringComparison.OrdinalIgnoreCase),
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
            });
        }
    }

    private static Vector3 ToVector3(Color32 color) => new(color.R, color.G, color.B);

    // -------- geometry/material caches --------

    /// <summary>Uploads each GLB's geometry once and shares the buffers across entities; each
    /// entity's mesh clones the primitive records with slot-override material ids. GLB slot
    /// order == Materials order is the schema-v2 contract rule.</summary>
    private sealed class GeometryCache(PbrRenderer pbr)
    {
        private readonly Dictionary<GltfAsset, (PbrPrimitive Primitive, Matrix4x4 Bake, int GlbMaterialId)[]> _uploaded = new();
        private readonly Dictionary<string, int> _levelMaterialIds = new(StringComparer.Ordinal);

        public PbrMesh InstantiateMesh(GltfAsset asset, IReadOnlyList<string?> slotOverrides, RuntimeLevel level)
        {
            var uploaded = Upload(asset);
            var primitives = new PbrPrimitive[uploaded.Length];
            for (var i = 0; i < uploaded.Length; i++)
            {
                var (primitive, _, glbMaterialId) = uploaded[i];
                var overrideField = i < slotOverrides.Count ? slotOverrides[i] : null;
                primitives[i] = overrideField is null
                    ? primitive with { MaterialId = glbMaterialId }
                    : primitive with { MaterialId = ResolveLevelMaterial(overrideField, level) };
            }
            return new PbrMesh(primitives);
        }

        private (PbrPrimitive Primitive, Matrix4x4 Bake, int GlbMaterialId)[] Upload(GltfAsset asset)
        {
            if (_uploaded.TryGetValue(asset, out var cached)) return cached;

            // Register the GLB's own materials (used for null slots / the environment).
            var glbMaterialIds = new int[asset.Materials.Length];
            for (var i = 0; i < asset.Materials.Length; i++)
            {
                glbMaterialIds[i] = pbr.Materials.AddMaterial(in asset.Materials[i], asset.Images);
            }
            var fallback = -1;

            var list = new List<(PbrPrimitive, Matrix4x4, int)>();
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
                    list.Add((pbr.UploadPrimitive(vertices, primitive.Indices, materialId), instance.WorldTransform, materialId));
                }
            }

            var result = list.ToArray();
            _uploaded[asset] = result;
            return result;
        }

        private int ResolveLevelMaterial(string field, RuntimeLevel level)
        {
            if (_levelMaterialIds.TryGetValue(field, out var id)) return id;
            var data = level.Materials[field];
            id = pbr.Materials.AddMaterial(ToGltfMaterial(data), []);
            _levelMaterialIds[field] = id;
            return id;
        }

        /// <summary>Level material JSON → the renderer's material shape. Texture paths in
        /// material documents reference Godot-project SOURCE files, not runtime assets — the
        /// supported texturing route is GLB-embedded KTX2 (null slots); overrides are
        /// factor-only.</summary>
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
