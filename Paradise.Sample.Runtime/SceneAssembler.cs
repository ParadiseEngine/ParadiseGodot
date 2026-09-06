using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Physics;

namespace Paradise.Sample.Runtime;

/// <summary>One simulated, rendered entity: the sim handle paired with its render instance.
/// Static scenery has no sim entity (null) — its transform never changes.</summary>
public sealed record RuntimeInstance(
    Entity? SimEntity,
    PbrInstance Render,
    float SimScale = 1f); // sim rebuilds Model from pos+rot; the authored uniform scale must survive

/// <summary>Builds the runtime world from a loaded level: the static CollisionWorld (from data,
/// not Godot nodes — the JSON-sourced analog of EcsSceneBridge.BuildCollisionWorld), the
/// simulation spawns (Rigidbody.Dynamic + sphere → SpawnBall), and the PBR render instances with
/// slot-wise material overrides.</summary>
public static class SceneAssembler
{
    /// <summary>Contract matrices are column-vector layout; transpose yields the
    /// System.Numerics row-vector model matrix everything downstream uses.</summary>
    public static Matrix4x4 ToModelMatrix(Matrix4x4? contractMatrix) =>
        contractMatrix is { } m ? Matrix4x4.Transpose(m) : Matrix4x4.Identity;

    // -------- collision --------

    public static CollisionWorld? BuildCollisionWorld(AuthoredScene level)
    {
        var colliders = new List<Collider>();
        var transforms = new List<RigidTransform>();

        foreach (var entity in level.Entities)
        {
            // Only truly static bodies join the static world — kinematic agents and dynamic
            // balls are simulated, exactly like the Godot bridge's navigation_source harvest.
            if (entity.Get<RigidbodyComponentData>()?.BodyType != PhysicsBodyType.Static) continue;
            var model = entity.World;
            foreach (var shape in entity.Get<ColliderComponentData>()?.Colliders ?? [])
            {
                // Triggers are sensors (pool-pocket capture regions), never solid geometry —
                // a pocket sphere in the collision world would block the pocket mouth.
                if (shape.IsTrigger) continue;
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

    /// <summary>Pocket capture regions: every trigger sphere on a static entity, in world
    /// space (the same transform/scale folding as <see cref="AppendCollider"/>). Pure over the
    /// level data — unit-testable without a renderer.</summary>
    public static List<(Vector3 Center, float Radius)> ExtractPockets(AuthoredScene level)
    {
        var pockets = new List<(Vector3, float)>();
        foreach (var entity in level.Entities)
        {
            if (entity.Get<RigidbodyComponentData>()?.BodyType != PhysicsBodyType.Static) continue;
            var model = entity.World;
            var ownerScale = OwnerScale(model);
            foreach (var shape in entity.Get<ColliderComponentData>()?.Colliders ?? [])
            {
                if (!shape.IsTrigger || shape.ShapeType != PhysicsShapeType.Sphere) continue;
                var world = Matrix4x4.CreateTranslation(shape.LocalCenter) * model;
                pockets.Add((world.Translation, ColliderScaleFold.SphereRadius(shape.Radius, ownerScale)));
            }
        }
        return pockets;
    }

    /// <summary>The scene's cushion bounce: the liveliest (max) authored Restitution across
    /// static entities that own solid Obstacle-layer colliders — the surfaces balls actually
    /// bounce off. Falls back to the project-settings default when the scene authors none.
    /// The max/fallback reduction is shared with the Godot bridge via <see cref="StaticSurfaces"/>;
    /// this method only gathers the surfaces from the exported contract.</summary>
    public static float StaticSurfaceRestitution(AuthoredScene level, float fallback = 0.4f) =>
        StaticSurfaces.BounceRestitution(GatherStaticSurfaces(level), fallback);

    private static IEnumerable<StaticSurfaces.Surface> GatherStaticSurfaces(AuthoredScene level)
    {
        foreach (var entity in level.Entities)
        {
            if (entity.Get<RigidbodyComponentData>() is not { BodyType: PhysicsBodyType.Static } rigidbody) continue;
            foreach (var shape in entity.Get<ColliderComponentData>()?.Colliders ?? [])
            {
                if (shape.IsTrigger) continue;
                // shape.Layer is a Unity-style layer INDEX; the contract-to-mask shift matches
                // AppendCollider so BounceRestitution's Obstacle test agrees across hosts.
                yield return new StaticSurfaces.Surface(rigidbody.Restitution, 1u << shape.Layer);
            }
        }
    }

    // -------- simulation spawns + render instances --------

    public sealed record AssembledScene(
        List<RuntimeInstance> Instances,
        Entity? CueBall,
        List<(Entity Entity, int InstanceIndex)> PoolBalls)
    {
        /// <summary>Flipbook sprite quads (sim-clocked); RuntimeLoop re-writes them each frame.</summary>
        public List<SpriteQuadState> Sprites { get; init; } = new();

        /// <summary>Particle emitter batches (sprite quads / voxel cubes) driven from snapshots.</summary>
        public List<ParticleBatchState> ParticleBatches { get; init; } = new();
    }

    /// <summary>Spawn sim entities and build render instances. Must run on the runner's owner
    /// thread BEFORE <c>runner.Start()</c> (world-pool thread affinity).</summary>
    public static AssembledScene Assemble(RuntimeLevel level, SimulationRunner runner, PbrRenderer pbr)
    {
        var geometry = new GeometryCache(pbr);
        var instances = new List<RuntimeInstance>();
        Entity? cueBall = null;
        var poolBalls = new List<(Entity, int)>();
        var sprites = new List<SpriteQuadState>();
        var particleBatches = new List<ParticleBatchState>();
        var pockets = ExtractPockets(level.Scene);
        var dynamics = level.PhysicsDynamics;
        var staticRestitution = StaticSurfaceRestitution(level.Scene, dynamics.DefaultStaticRestitution);
        var tuning = new PhysicsTuning(dynamics.MinSpeed, dynamics.Skin, dynamics.PushStrength,
            new Vector3(0f, dynamics.GravityY, 0f), dynamics.StaticFriction, dynamics.MinAngularSpeed);
        var trayIndex = 0;

        foreach (var entity in level.Scene.Entities)
        {
            var model = entity.World;
            PbrInstance? render = null;
            if (entity.Get<RenderableComponentData>() is { Mesh: { } meshField })
            {
                var mesh = geometry.InstantiateMesh(meshField, level.Meshes[meshField], entity.Get<MaterialsComponentData>()?.Slots ?? [], level);
                render = new PbrInstance { Mesh = mesh, Model = model };
            }

            Entity? simEntity = null;
            var (position, rotation) = DecomposePose(model);
            // Read once per entity rather than per access: Get<T> deserializes the payload each
            // time it is called, and this loop asks for the rigidbody five times.
            var rigidbody = entity.Get<RigidbodyComponentData>();
            var collider = entity.Get<ColliderComponentData>();
            if (rigidbody?.BodyType == PhysicsBodyType.Dynamic)
            {
                var sphere = FindShape(collider, PhysicsShapeType.Sphere);
                // Godot scales collision shapes by node scale; the contract stores the UNSCALED
                // shape radius, so apply the entity's (uniform) scale here or a 0.7-scaled ball
                // simulates 43% too fat and racks placed at visual spacing explode apart.
                var ownerScale = OwnerScale(model);
                var radius = (sphere?.Radius ?? 0.5f) * ownerScale.X;
                var isCue = string.Equals(entity.Name, "CueBall", StringComparison.OrdinalIgnoreCase);
                var ball = runner.SpawnBall(position, rotation, radius,
                    Math.Max(0.01f, rigidbody.Mass),
                    rigidbody.LinearDamping,
                    rigidbody.Restitution,
                    staticRestitution,
                    PoolRack.BuildBall(pockets, isCue, position, trayIndex++),
                    tuning,
                    friction: rigidbody.Friction);
                simEntity = ball;
                if (render is not null)
                {
                    poolBalls.Add((ball, instances.Count)); // instance appended just below
                }
                if (isCue)
                {
                    cueBall = ball;
                }
            }

            if (render is not null)
            {
                instances.Add(new RuntimeInstance(simEntity, render, OwnerScale(model).X));
            }

            // Sprite animations and particle emitters spawn their own sim entities (independent
            // features, matching EcsSceneBridge) with dynamic-primitive render states.
            if (entity.Get<SpriteAnimationComponentData>() is { } spriteData)
            {
                var normalized = spriteData with { };
                normalized.ValidateAndNormalize();
                var spriteEntity = runner.SpawnSpriteAnimation(
                    position, rotation, normalized.Fps, normalized.FrameCount, normalized.Loop);
                sprites.Add(new SpriteQuadState(pbr, normalized, SheetBytes(level, normalized.Sheet), spriteEntity));
            }

            if (entity.Get<ParticleEmitterComponentData>() is { } emitterData)
            {
                var normalized = emitterData with { };
                normalized.ValidateAndNormalize();
                var emitterEntity = runner.SpawnParticleEmitter(position, rotation, new ParticleConfig(
                    normalized.EmitRate,
                    normalized.LifetimeSeconds,
                    normalized.InitialSpeed,
                    float.DegreesToRadians(normalized.SpreadDegrees),
                    normalized.Gravity,
                    normalized.Drag,
                    normalized.MaxParticles),
                    normalized.Seed);
                particleBatches.Add(new ParticleBatchState(
                    pbr, normalized, SheetBytes(level, normalized.Sheet), emitterEntity));
            }
        }

        return new AssembledScene(instances, cueBall, poolBalls)
        {
            Sprites = sprites,
            ParticleBatches = particleBatches,
        };
    }

    private static byte[]? SheetBytes(RuntimeLevel level, string? sheetField) =>
        sheetField is not null && level.SpriteSheets.TryGetValue(sheetField, out var bytes) ? bytes : null;

    private static ColliderShapeData? FindShape(ColliderComponentData? collider, PhysicsShapeType type)
    {
        foreach (var shape in collider?.Colliders ?? [])
        {
            if (shape.ShapeType == type) return shape;
        }
        return null;
    }

    // -------- lights / ambient --------

    public static void PopulateLighting(RuntimeLevel level, PbrScene scene)
    {
        // v5: the lighting document block is gone. The environment is an authored
        // EnvironmentData on its own entity, and every light is a SceneLightData component
        // on the entity that IS it — gather them from the entity component lists.
        EnvironmentData? environment = null;
        var lights = new List<SceneLightData>();
        foreach (var entity in level.Scene.Entities)
        {
            if (entity.Get<EnvironmentData>() is { } env) environment = env;
            if (entity.Get<SceneLightData>() is { } light) lights.Add(light);
        }
        if (environment is null && lights.Count == 0) return;

        if (environment is not null)
        {
        scene.Ambient = new PbrAmbient
        {
            Sky = ToVector3(environment.AmbientColor),
            Equator = ToVector3(environment.AmbientEquatorColor),
            Ground = ToVector3(environment.AmbientGroundColor),
            // Ambient energy drives the hemisphere strength (Godot ambient_light_energy).
            Exposure = environment.AmbientEnergy,
            Flat = !string.Equals(environment.AmbientMode, "Skybox", StringComparison.OrdinalIgnoreCase),
            // L2 sky-SH irradiance (27 floats → 9 RGB coefficients): the per-normal ambient
            // Godot's sky-SH produces; the 3 zones above remain the fallback when absent.
            Sh = environment.AmbientSh is { Length: 27 } shFlat
                ? [.. Enumerable.Range(0, 9).Select(i => new Vector3(shFlat[i * 3], shFlat[i * 3 + 1], shFlat[i * 3 + 2]))]
                : null,
        };
        // Background/clear tone from the environment (the sky) so the .NET background matches Godot —
        // but only when a real WorldEnvironment was exported. A default EnvironmentData must not stomp
        // the camera-derived clear (which RuntimeLoop set before calling this).
        if (environment.HasBackground)
        {
            var bg = environment.BackgroundColor;
            scene.ClearColor = new ColorRgba(bg.R, bg.G, bg.B, 1f);
        }
        // Gradient-sky background (Sky source): a fullscreen top→horizon gradient instead of the flat
        // clear, matching Godot's procedural sky behind the scene. The contract stores the endpoint
        // colours sRGB-ENCODED and untonemapped (exact in 8-bit Color32); PbrScene wants LINEAR —
        // the sky shader blends in linear and tonemaps per-pixel (Godot's order).
        scene.HasSkyBackground = environment.SkyGradient;
        scene.SkyReflections = environment.SkyReflections;
        // Sky sun disk/halo: pair the exported thresholds with the first ENABLED directional
        // light, so disabling the light removes the sun from the sky exactly like hiding it does
        // in Godot. The sky wants the LINEAR colour × energy (contract light colours are
        // sRGB-encoded, matching Godot's light_color; Godot linearizes for the sky uniforms).
        var sun = lights.FirstOrDefault(l => l.Enabled && l.Type == "Directional");
        if (sun is not null)
        {
            scene.SkySunEnabled = true;
            scene.SkySunDirection = Vector3.Normalize(-sun.Direction);
            scene.SkySunColorEnergy = SrgbToLinear(ToVector3(sun.Color)) * sun.Intensity;
            scene.SkySunSizeCos = environment.SkySunSizeCos;
            scene.SkySunAngleMaxCos = environment.SkySunAngleMaxCos;
            scene.SkySunInvCurve = environment.SkySunInvCurve;
        }
        scene.SkyTopColor = SrgbToLinear(ToVector3(environment.SkyTopColor));
        scene.SkyHorizonColor = SrgbToLinear(ToVector3(environment.SkyHorizonColor));
        scene.SkyGroundBottom = SrgbToLinear(ToVector3(environment.SkyGroundBottomColor));
        scene.SkyGroundHorizon = SrgbToLinear(ToVector3(environment.SkyGroundHorizonColor));
        scene.SkySkyCurveInv = environment.SkySkyCurveInv;
        scene.SkyGroundCurveInv = environment.SkyGroundCurveInv;
        scene.Tonemap = new PbrTonemap
        {
            Mode = ParseTonemapMode(environment.TonemapMode),
            Exposure = environment.TonemapExposure,
            White = environment.TonemapWhite,
        };
        scene.Ssao = new PbrSsao
        {
            Enabled = environment.SsaoEnabled,
            Radius = environment.SsaoRadius,
            Intensity = environment.SsaoIntensity,
            Power = environment.SsaoPower,
        };
        scene.Bloom = new PbrBloom
        {
            Enabled = environment.GlowEnabled,
            Threshold = environment.GlowThreshold,
            Intensity = environment.GlowIntensity,
        };
        }

        foreach (var light in lights)
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
                // Contract light colours are sRGB-encoded (Godot's light_color property verbatim);
                // Godot linearizes them for rendering (source_color), so decode here — the same
                // convention as the sky-sun colour and the exporter's ambient sun integral. A raw
                // sRGB colour in linear lighting math skewed mixed colours cool (e.g. the warm
                // directional (1,.949,.851) lit surfaces with too much G/B).
                Color = SrgbToLinear(ToVector3(light.Color)),
                Intensity = light.Intensity,
                Range = light.Range,
                AttenuationExponent = light.AttenuationExponent,
                SpotOuterDegrees = light.SpotAngle,
                SpotInnerDegrees = light.InnerSpotAngle,
                // Real-time shadows — the engine casts from directional, spot, and point lights via
                // its shadow atlas. Soft (5-tap PCF) whenever shadows are on; the contract carries no
                // hard/soft flag yet, so it's not data-driven.
                Specular = light.Specular,
                Size = light.Size,
                CastsShadows = light.ShadowsEnabled,
                ShadowStrength = light.ShadowStrength,
                SoftShadows = light.ShadowsEnabled,
            });
        }
    }

    private static Vector3 ToVector3(Color32 color) => new(color.R, color.G, color.B);

    // sRGB EOTF (the exact piecewise curve, matching Godot's Color.SrgbToLinear and the
    // shaders' srgb helpers) — for contract colours stored sRGB-encoded (the sky gradient).
    private static Vector3 SrgbToLinear(Vector3 srgb)
    {
        static float Channel(float c) =>
            c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new Vector3(Channel(srgb.X), Channel(srgb.Y), Channel(srgb.Z));
    }

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

    public static bool HasAnyTexture(LevelMaterialData material) =>
        material.BaseColorTexture is { Length: > 0 } || material.MetallicRoughnessTexture is { Length: > 0 } ||
        material.NormalTexture is { Length: > 0 } || material.OcclusionTexture is { Length: > 0 } ||
        material.EmissiveTexture is { Length: > 0 };

    /// <summary>Whether a slot override should inherit the GLB material's textures (glTF
    /// factor × texture) rather than render solid. Matches Godot's <c>surface_material_override</c>
    /// semantics: an override that references a texture tints the GLB's; an override with NO
    /// texture (<see cref="LevelMaterialData.BaseColorTexture"/> null) FULLY REPLACES the surface
    /// (solid factor), so it must not silently pull the GLB's own texture back in. Both sides are
    /// documents now: the GLB's materials are what `paradise assets extract` wrote for it.</summary>
    public static bool ShouldInheritTextures(LevelMaterialData data, LevelMaterialData glb) =>
        HasAnyTexture(glb) && data.BaseColorTexture is not null;

    /// <summary>A slot-override material: the override's FACTORS over the GLB material's
    /// TEXTURES (glTF factor × texture — Godot parity for surface_material_override with
    /// textured materials).</summary>
    public static LevelMaterialData BuildSlotOverrideMaterial(LevelMaterialData data, LevelMaterialData glbMaterial) =>
        data with
        {
            BaseColorTexture = glbMaterial.BaseColorTexture,
            MetallicRoughnessTexture = glbMaterial.MetallicRoughnessTexture,
            NormalTexture = glbMaterial.NormalTexture,
            OcclusionTexture = glbMaterial.OcclusionTexture,
            EmissiveTexture = glbMaterial.EmissiveTexture,
            BaseColorUvOffset = glbMaterial.BaseColorUvOffset,
            BaseColorUvScale = glbMaterial.BaseColorUvScale,
            BaseColorUvRotation = glbMaterial.BaseColorUvRotation,
        };

    /// <summary>
    /// A material document as the renderer's material record plus the image table it indexes:
    /// the KTX2 bytes the build wrote for each texture the document names, -1 where a channel
    /// has none.
    /// </summary>
    public static (GltfMaterialData Material, GltfImageData[] Images) ToRendererMaterial(LevelMaterialData data, RuntimeLevel level)
    {
        var images = new List<GltfImageData>();
        int Image(string? texture)
        {
            if (texture is not { Length: > 0 } || !level.Textures.TryGetValue(texture, out var bytes)) return -1;
            images.Add(new GltfImageData(bytes));
            return images.Count - 1;
        }

        var baseColor = Image(data.BaseColorTexture);
        var metallicRoughness = Image(data.MetallicRoughnessTexture);
        var normal = Image(data.NormalTexture);
        var occlusion = Image(data.OcclusionTexture);
        var emissive = Image(data.EmissiveTexture);
        var material = ToGltfMaterial(data) with
        {
            BaseColorImage = baseColor,
            MetallicRoughnessImage = metallicRoughness,
            NormalImage = normal,
            OcclusionImage = occlusion,
            EmissiveImage = emissive,
            BaseColorUvTransform = new GltfUvTransform(
                new Vector2(
                    data.BaseColorUvOffset is { Length: 2 } offset ? offset[0] : 0f,
                    data.BaseColorUvOffset is { Length: 2 } offset2 ? offset2[1] : 0f),
                new Vector2(
                    data.BaseColorUvScale is { Length: 2 } scale ? scale[0] : 1f,
                    data.BaseColorUvScale is { Length: 2 } scale2 ? scale2[1] : 1f),
                data.BaseColorUvRotation),
        };
        return (material, [.. images]);
    }

    /// <summary>A material document's factors as the renderer's material shape, textures unbound.</summary>
    private static GltfMaterialData ToGltfMaterial(LevelMaterialData data) => new(
        Name: data.Name,
        BaseColorFactor: new Vector4(data.BaseColorFactor.R, data.BaseColorFactor.G, data.BaseColorFactor.B, data.BaseColorFactor.A),
        MetallicFactor: data.MetallicFactor,
        RoughnessFactor: data.RoughnessFactor,
        // HDR emissive: EmissiveFactor is [0,1] (Color32-clamped), so the unclamped EmissiveStrength
        // multiplier here is what lets lava exceed white and bloom.
        EmissiveFactor: new Vector3(data.EmissiveFactor.R, data.EmissiveFactor.G, data.EmissiveFactor.B) * data.EmissiveStrength,
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
        BaseColorUvTransform: GltfUvTransform.Identity)
    {
        ProcKind = ProceduralKindId(data.MaterialKind),
        ProcNoiseScale = data.NoiseScale,
        ProcFlowSpeed = data.FlowSpeed,
        ProcEmissiveStrength = data.EmissiveStrength,
        ProcColorA = new Vector3(data.ColorA.R, data.ColorA.G, data.ColorA.B),
        ProcColorB = new Vector3(data.ColorB.R, data.ColorB.G, data.ColorB.B),
    };

    /// <summary>Map an authored procedural material-kind name to the runtime shader's recipe id
    /// (see pbr.slang <c>evalProcedural</c>). Unknown/empty = 0 (a normal PBR material).</summary>
    public static int ProceduralKindId(string? kind) => kind switch
    {
        "lava" => 1,
        "marble" => 2,
        "jade" => 3,
        "ice" => 4,
        "molten_metal" => 5,
        "obsidian" => 6,
        "gem" => 7,
        "amber" => 8,
        "nebula" => 9,
        _ => 0,
    };

    // -------- geometry/material caches --------

    /// <summary>Uploads each cooked mesh's draws once and shares the buffers across entities;
    /// each entity's mesh clones the primitive records with its own slot materials. The blob's
    /// draw order IS the slot order, on both sides.</summary>
    private sealed partial class GeometryCache(PbrRenderer pbr)
    {
        /// <summary>One cooked mesh uploaded: the primitives, one per draw, and — parallel to
        /// them — each draw's material slot.</summary>
        private readonly record struct UploadedMesh(PbrPrimitive[] Primitives, int[] Slots);

        private readonly Dictionary<string, UploadedMesh> _uploaded = new(StringComparer.Ordinal);
        private readonly Dictionary<(string? Override, string? Glb), int> _materialIds = new();
        private int _fallback = -1;

        /// <summary>
        /// One entity's mesh: the shared upload, with each draw's material decided by the slot
        /// rule. The scene's slot names a material document and overrides; a null slot keeps the
        /// material the GLB had, which the build extracted to a document and recorded on the
        /// GLB's seed prefab. An override with no texture of its own inherits a textured GLB
        /// material's textures (<see cref="ShouldInheritTextures"/>).
        /// </summary>
        public PbrMesh InstantiateMesh(string field, CookedMesh cooked, IReadOnlyList<string?> slotOverrides, RuntimeLevel level)
        {
            var uploaded = Upload(field, cooked);
            var primitives = new PbrPrimitive[uploaded.Primitives.Length];
            for (var i = 0; i < primitives.Length; i++)
            {
                var slot = uploaded.Slots[i];
                var overrideField = slot >= 0 && slot < slotOverrides.Count ? slotOverrides[slot] : null;
                var glbField = slot >= 0 && slot < cooked.Materials.Count ? cooked.Materials[slot] : null;
                primitives[i] = uploaded.Primitives[i] with { MaterialId = ResolveMaterial(overrideField, glbField, level) };
            }
            return new PbrMesh(primitives);
        }

        /// <summary>
        /// The cooked mesh's draws as GPU primitives. Rigid draws arrive with their node transform
        /// already baked into the vertices by the build. A skinned blob keeps its joints and
        /// weights interleaved and stays in bind space; this sample authors no animation, so the
        /// geometry half is drawn as it is — the model at its bind pose.
        /// </summary>
        private UploadedMesh Upload(string field, CookedMesh cooked)
        {
            if (_uploaded.TryGetValue(field, out var cached)) return cached;

            var (primitives, slots) = UploadDraws(pbr, cooked.Blob, Fallback());
            return _uploaded[field] = new UploadedMesh(primitives, slots);
        }
    }

    /// <summary>
    /// A cooked mesh's draws as GPU primitives, and — parallel to them — each draw's material slot.
    /// Rigid draws arrive with their node transform already baked into the vertices by the build.
    /// A skinned blob keeps its joints and weights interleaved and stays in bind space; this sample
    /// authors no animation, so the geometry half is drawn as it is — the model at its bind pose.
    /// </summary>
    public static (PbrPrimitive[] Primitives, int[] Slots) UploadDraws(PbrRenderer pbr, byte[] blob, int materialId)
    {
        var mesh = MeshBlobFormat.Read(blob);
        var primitives = new PbrPrimitive[mesh.Draws.Count];
        var slots = new int[mesh.Draws.Count];
        for (var i = 0; i < primitives.Length; i++)
        {
            var draw = mesh.Draws[i];
            // Each draw gets its own vertex buffer holding only the vertices it indexes: the blob
            // shares one vertex stream across draws, and uploading it once per draw would pay for
            // every other draw's vertices again.
            var (vertices, indices) = Compact(mesh, draw);
            if (mesh.Layout == MeshVertexLayout.Skinned) vertices = GeometryHalf(vertices);
            slots[i] = draw.MaterialSlot;
            primitives[i] = pbr.UploadPrimitive(vertices, indices, materialId);
        }
        return (primitives, slots);
    }

    /// <summary>The vertices one draw indexes, renumbered from zero.</summary>
    private static (float[] Vertices, uint[] Indices) Compact(MeshData mesh, MeshDrawData draw)
        {
            var stride = mesh.FloatsPerVertex;
            var remap = new Dictionary<uint, uint>();
            var indices = new uint[draw.IndexCount];
            var vertices = new List<float>();
            for (var i = 0; i < indices.Length; i++)
            {
                var source = mesh.Indices[(int)draw.FirstIndex + i];
                if (!remap.TryGetValue(source, out var target))
                {
                    target = (uint)remap.Count;
                    remap[source] = target;
                    vertices.AddRange(mesh.Vertices.AsSpan((int)source * stride, stride));
                }
                indices[i] = target;
            }
            return ([.. vertices], indices);
        }

    /// <summary>The geometry floats of a skinned stream, without the joints and weights the
    /// blob interleaves after them.</summary>
private static float[] GeometryHalf(float[] skinned)
    {
        var count = skinned.Length / MeshBlob.SkinnedFloatsPerVertex;
        var geometry = new float[count * MeshBlob.StaticFloatsPerVertex];
        for (var v = 0; v < count; v++)
        {
            skinned.AsSpan(v * MeshBlob.SkinnedFloatsPerVertex, MeshBlob.StaticFloatsPerVertex)
                .CopyTo(geometry.AsSpan(v * MeshBlob.StaticFloatsPerVertex));
        }
        return geometry;
    }


    private sealed partial class GeometryCache
    {
        private int Fallback() =>
            _fallback >= 0 ? _fallback : _fallback = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f));

        private int ResolveMaterial(string? overrideField, string? glbField, RuntimeLevel level)
        {
            var key = (overrideField, glbField);
            if (_materialIds.TryGetValue(key, out var id)) return id;

            var chosen = Choose(overrideField, glbField, level);
            id = chosen is null ? Fallback() : Register(chosen.Value.Material, chosen.Value.Images);
            return _materialIds[key] = id;
        }

        private int Register(GltfMaterialData material, GltfImageData[] images) => pbr.Materials.AddMaterial(in material, images);

        /// <summary>The slot rule, as a document: the override, over the GLB's textures when it
        /// inherits them; else the GLB's own; else nothing.</summary>
        private static (GltfMaterialData Material, GltfImageData[] Images)? Choose(string? overrideField, string? glbField, RuntimeLevel level)
        {
            LevelMaterialData? overrideData = overrideField is not null && level.Materials.TryGetValue(overrideField, out var o) ? o : null;
            LevelMaterialData? glbData = glbField is not null && level.Materials.TryGetValue(glbField, out var g) ? g : null;

            LevelMaterialData? document = overrideData is null
                ? glbData
                : glbData is not null && ShouldInheritTextures(overrideData, glbData)
                    ? BuildSlotOverrideMaterial(overrideData, glbData)
                    : overrideData;
            return document is null ? null : ToRendererMaterial(document, level);
        }
    }
}
