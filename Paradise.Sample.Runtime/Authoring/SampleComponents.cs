using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Paradise.Authoring;
using Paradise.Export.Data;

// Opt in to the generated reader. It is public surface, so an assembly that only publishes a
// schema for editors does not get one; this assembly LOADS documents, which is exactly the case
// the opt-in exists to distinguish.
[assembly: AuthoredRegistry]

namespace Paradise.Sample.Runtime;

// The sample game's own authored components.
//
// Contract v6 deleted every authored component the ENGINE used to declare, and did not replace
// them: an entity is its components, and which components those are is the game's statement. So
// these are ported here verbatim from Paradise.Export 0.25.0 rather than reinvented — the sample
// renderer already knew how to consume exactly these fields, and changing their shape in the same
// move that changed their owner would have made a regression impossible to attribute.
//
// Two of the old records did NOT come across. Name and Transform are the authoring format's own
// vocabulary now (WellKnownEntityComponents: `meta` carries identity, name and parent; `transform`
// carries LOCAL position, rotation and scale), so a game does not declare them and the loader
// composes world matrices itself.
//
// EnvironmentData keeps the Godot sky-shader constants — SkySunSizeCos, SkySkyCurveInv and the
// rest. They are deliberately absent from the engine's general HostEnvironment kind, because a fit
// to one renderer's sky shader is not something every host can answer; a game that wants them says
// so in its own record, which is exactly what this is.

[Guid("f2c0357e-94dd-4a5a-9803-518066cb54b2")]
[Authored(DisplayName = "Renderable")]
public sealed record RenderableComponentData
{
    /// <summary>
    /// Authored by picking the source GLB, and BAKED to the data-relative path the runtime
    /// resolves.
    ///
    /// An ASSET rather than a mesh-node reference, because that is how it was actually
    /// authored: the field this replaces was a file picker, and in the sample scenes only 6 of
    /// 28 entities with a mesh had a node to point at at all — the rest named a file. A node
    /// reference would have been unauthorable for most of them.
    /// </summary>
    [AuthoredByHost<HostAsset>]
    [AuthorAssetKinds(".glb", ".gltf")]
    [AuthorDoc("The source GLB this entity renders.")]
    public string? Mesh { get; set; }

    [AuthorDoc("Optional node inside the GLB; empty means its whole default scene.")]
    public string? MeshNode { get; set; }

    // The material slots are NOT here. They were, from v4, and they moved to
    // MaterialsComponentData in v5 for the reason the whole schema moved: they are not
    // geometry. Two objects sharing a GLB and differing only in their slots are two drawable
    // VARIANTS and one mesh, which is what a renderer's upload table is keyed on — and one
    // record holding both said they were one thing.
}

[Guid("e1cd1bc8-86f2-4225-adc9-4a324c70ebf9")]
[Authored(DisplayName = "Collider")]
public sealed record ColliderComponentData
{
    /// <summary>A list of shape references. Each is edited with the host's own handles and
    /// baked into the numbers below it at export.</summary>
    [AuthorDoc("Collision shapes, edited with the host's own handles.")]
    public List<ColliderShapeData> Colliders { get; set; } = new();
}

[Guid("d3e53cd4-89c6-4ca8-851e-7596da889c68")]
[Authored(DisplayName = "Sprite animation")]
// The sheet is a host reference; the grid and the clock are typed in. v5 marked the WHOLE record
// [AuthoredByHost(Sprite)], which v6 refuses (PAUT011) — HostSprite is a value kind, one reference,
// and a record is not a reference. The composed kind that can carry the grid off a sprite object is
// HostSpriteSheet (engine #238); this moves to it with that bump.
public sealed record SpriteAnimationComponentData
{
    [AuthoredByHost<HostSprite>]
    public string? Sheet { get; set; }
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public int FrameCount { get; set; }
    public float Fps { get; set; } = 10f;
    public bool Loop { get; set; } = true;
    /// <summary>World size of the quad (meters, X = width, Y = height).</summary>
    public Vector2 QuadSize { get; set; } = Vector2.One;
    /// <summary>Face the camera (Y-billboard is not modelled — full billboard or fixed).</summary>
    public bool Billboard { get; set; } = true;

    public void ValidateAndNormalize()
    {
        Columns = Math.Max(1, Columns);
        Rows = Math.Max(1, Rows);
        FrameCount = Math.Clamp(FrameCount <= 0 ? Columns * Rows : FrameCount, 1, Columns * Rows);
        Fps = float.IsFinite(Fps) && Fps > 0f ? Fps : 10f;
        QuadSize = new Vector2(
            float.IsFinite(QuadSize.X) && QuadSize.X > 0f ? QuadSize.X : 1f,
            float.IsFinite(QuadSize.Y) && QuadSize.Y > 0f ? QuadSize.Y : 1f);
    }
}

/// <summary>How a particle draws. Its own type because the contract serializes enums by NAME,
/// so the spelling here is the wire format.</summary>
public enum ParticleRenderKind
{
    Sprite,
    Voxel,
}

[Guid("1b4d1bdd-dea1-4b86-9b6a-879c46346b9e")]
[Authored(DisplayName = "Particle emitter")]
public sealed record ParticleEmitterComponentData
{
    [AuthorDoc("Sprite = camera-facing flipbook quads; Voxel = solid tinted cubes.")]
    public ParticleRenderKind Kind { get; set; } = ParticleRenderKind.Sprite;
    /// <summary>Live-particle cap; clamped to the runtime's per-emitter buffer (64).</summary>
    public int MaxParticles { get; set; } = 64;
    public float EmitRate { get; set; } = 8f;
    public float LifetimeSeconds { get; set; } = 1.5f;
    public float InitialSpeed { get; set; } = 2f;
    public float SpreadDegrees { get; set; } = 25f;
    /// <summary>Y acceleration (m/s²); negative pulls down.</summary>
    public float Gravity { get; set; } = -9.8f;
    /// <summary>Per-second linear damping applied to particle velocity.</summary>
    public float Drag { get; set; }
    /// <summary>World size at birth/death (quad edge for Sprite, cube edge for Voxel).</summary>
    public float StartSize { get; set; } = 0.25f;
    public float EndSize { get; set; } = 0.25f;
    /// <summary>RNG seed — same seed, same particle stream in both hosts.</summary>
    [AuthorDoc("Same seed, same particle stream in every host.")]
    public uint Seed { get; set; } = 1;
    /// <summary>Tint (Sprite: multiplies the sheet; Voxel: the cube albedo).</summary>
    public Color32 Color { get; set; } = Color32.FromRgba(1f, 1f, 1f);

    // Sprite kind only: flipbook sheet (same conventions as SpriteAnimationComponentData).
    // Fps 0 stretches the flipbook once over each particle's lifetime.
    [AuthoredByHost<HostAsset>, AuthorAssetKinds(".png", ".jpg", ".jpeg")]
    [AuthorVisibleWhen(nameof(Kind), ParticleRenderKind.Sprite)]
    [AuthorDoc("Flipbook spritesheet for the particles.")]
    public string? Sheet { get; set; }
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public int FrameCount { get; set; }
    public float Fps { get; set; }

    public void ValidateAndNormalize()
    {
        MaxParticles = Math.Clamp(MaxParticles, 1, 64);
        EmitRate = float.IsFinite(EmitRate) && EmitRate > 0f ? EmitRate : 8f;
        LifetimeSeconds = float.IsFinite(LifetimeSeconds) && LifetimeSeconds > 0f ? LifetimeSeconds : 1.5f;
        InitialSpeed = float.IsFinite(InitialSpeed) && InitialSpeed >= 0f ? InitialSpeed : 2f;
        SpreadDegrees = float.IsFinite(SpreadDegrees) ? Math.Clamp(SpreadDegrees, 0f, 180f) : 25f;
        Gravity = float.IsFinite(Gravity) ? Gravity : -9.8f;
        Drag = float.IsFinite(Drag) && Drag >= 0f ? Drag : 0f;
        StartSize = float.IsFinite(StartSize) && StartSize > 0f ? StartSize : 0.25f;
        EndSize = float.IsFinite(EndSize) && EndSize > 0f ? EndSize : StartSize;
        Seed = Seed == 0 ? 1u : Seed;
        Columns = Math.Max(1, Columns);
        Rows = Math.Max(1, Rows);
        FrameCount = Math.Clamp(FrameCount <= 0 ? Columns * Rows : FrameCount, 1, Columns * Rows);
        Fps = float.IsFinite(Fps) && Fps >= 0f ? Fps : 0f;
    }
}

[Guid("fc886b84-c48c-4415-afd9-b03d6faf5ab7")]
[Authored(DisplayName = "Light")]
[AuthoredByHost<HostLight>]
public sealed record SceneLightData
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Direction { get; set; } = Vector3.Zero;
    public Color32 Color { get; set; } = Color32.FromRgba(1f, 1f, 1f);
    public bool Enabled { get; set; } = true;
    public float Intensity { get; set; } = 1f;
    public bool UseColorTemperature { get; set; }
    public float ColorTemperature { get; set; } = 6570f;
    public float Range { get; set; }
    // Distance-falloff exponent (Godot LIGHT_PARAM_ATTENUATION / omni_/spot_attenuation). The
    // runtime applies pow(distance, -exponent) for point/spot lights; Godot's default 1.0 is
    // inverse-linear (not inverse-square). Unused by directionals.
    public float AttenuationExponent { get; set; } = 1f;
    public float SpotAngle { get; set; }
    public float InnerSpotAngle { get; set; }
    public Vector2 AreaSize { get; set; } = Vector2.Zero;
    public bool ShadowsEnabled { get; set; }
    public string ShadowType { get; set; } = "";
    public float ShadowStrength { get; set; } = 1f;
    // Godot Light3D LIGHT_PARAM_SPECULAR: scales only the specular lobe (Godot default 0.5).
    public float Specular { get; set; } = 0.5f;
    // Godot Light3D LIGHT_PARAM_SIZE (light_size / angular_distance): directional = angular
    // diameter in DEGREES; point/spot = world radius in meters. Softens specular highlights.
    public float Size { get; set; }
    public int LayerMask { get; set; }
    public int RenderingLayerMask { get; set; }
    public string Group { get; set; } = "";
}

[Guid("f5f4a867-fe27-426a-82f2-1a2de5aceb2f")]
[Authored(DisplayName = "Environment")]
public sealed record EnvironmentData
{
    /// <summary>Per-layer shadow map resolution the scene asks its renderer for, in texels.
    /// Null leaves the renderer's own default in place. It sizes a GPU resource, which is why
    /// it sits beside the mood rather than inside it.</summary>
    [AuthorDoc("Shadow map resolution in texels; unset leaves the renderer's default.")]
    public int? ShadowMapSize { get; set; }

    /// <summary>Soft-shadow blur: the PCF disk radius in shadow texels — the penumbra width of
    /// every shadow edge. Null leaves the renderer's default.</summary>
    [AuthorDoc("PCF disk radius in shadow texels; unset leaves the renderer's default.")]
    public float? ShadowBlur { get; set; }

    public string AmbientMode { get; set; } = "Color";
    public Color32 AmbientColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
    public Color32 AmbientEquatorColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
    public Color32 AmbientGroundColor { get; set; } = Color32.FromRgba(0.2f, 0.19f, 0.18f);
    // L2 spherical-harmonic sky irradiance (E/π): 9 RGB coefficients (27 floats, Ramamoorthi
    // order, band factors Â=(1, 2/3, 1/4) premultiplied) — the per-normal ambient Godot's
    // sky-SH produces. Full-precision floats (SH coefficients can be negative, so the 8-bit
    // Color32 encoding does not apply). Null when AmbientMode is not "Skybox".
    public float[]? AmbientSh { get; set; }
    // Ambient SPECULAR from the sky (Godot Environment.reflected_light_source ≠ Disabled).
    public bool SkyReflections { get; set; }
    // ProceduralSky sun disk/halo params (cosine thresholds + curve), matching Godot's
    // sky_material.cpp uniforms. SizeCos = cos(light angular distance); disk never triggers at
    // the default 2 (sentinel > 1) when no sun was found. The runtime pairs these with the
    // first ENABLED directional light for direction/colour/energy.
    public float SkySunSizeCos { get; set; } = 2f;
    public float SkySunAngleMaxCos { get; set; } = 2f;
    public float SkySunInvCurve { get; set; } = 24f;
    public float Exposure { get; set; } = 1f;
    // Ambient light energy (Godot Environment.ambient_light_energy). Scales the hemisphere ambient.
    public float AmbientEnergy { get; set; } = 1f;
    // Resolved background/clear tone (from the sky when background_mode is Sky), used as the
    // runtime clear color so the .NET background matches Godot instead of a flat neutral. Only
    // authoritative when HasBackground is set (a WorldEnvironment was actually exported); a
    // default-constructed EnvironmentData must NOT override the camera-derived clear.
    public bool HasBackground { get; set; }
    public Color32 BackgroundColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
    // Procedural-sky background (Godot ProceduralSkyMaterial), colours linear + already tone-mapped,
    // set only for a Sky source. The runtime evaluates Godot's two-part gradient per view ray: sky
    // (top→horizon) above the horizon, ground (bottom→horizon) below. Curves are Godot's inverse
    // curves (inv_sky_curve = 0.6/sky_curve, inv_ground_curve = 0.6/ground_curve).
    public bool SkyGradient { get; set; }
    public Color32 SkyTopColor { get; set; } = Color32.FromRgba(0.03f, 0.024f, 0.016f);
    public Color32 SkyHorizonColor { get; set; } = Color32.FromRgba(0.2f, 0.2f, 0.21f);
    public Color32 SkyGroundBottomColor { get; set; } = Color32.FromRgba(0.03f, 0.024f, 0.016f);
    public Color32 SkyGroundHorizonColor { get; set; } = Color32.FromRgba(0.2f, 0.2f, 0.21f);
    public float SkySkyCurveInv { get; set; } = 4f;
    public float SkyGroundCurveInv { get; set; } = 30f;
    public bool FogEnabled { get; set; }
    public Color32 FogColor { get; set; } = Color32.FromRgba(0.5f, 0.52f, 0.56f);
    public float FogDensity { get; set; }

    // Screen-space ambient occlusion (Godot Environment.ssao_*). When enabled, the runtime runs a
    // world-position pre-pass and darkens the ambient term in creases/contacts.
    public bool SsaoEnabled { get; set; }
    public float SsaoRadius { get; set; } = 1f;
    public float SsaoIntensity { get; set; } = 2f;
    public float SsaoPower { get; set; } = 1.5f;

    // Tone mapping exported from Godot's Environment (Environment.tonemap_*). TonemapMode names
    // match Godot's ToneMapper enum (Linear, Reinhardt, Filmic, Aces, Agx). The runtime renderer
    // applies the matching operator before the sRGB encode so the .NET render matches Godot.
    public string TonemapMode { get; set; } = "Linear";
    public float TonemapExposure { get; set; } = 1f;
    public float TonemapWhite { get; set; } = 1f;

    // Bloom / glow (Godot Environment.glow_*). The runtime's HDR composite runs a threshold +
    // dual-filter bloom and adds it back scaled by intensity — the .NET analog of Godot's glow.
    public bool GlowEnabled { get; set; }
    public float GlowIntensity { get; set; } = 0.6f;
    public float GlowThreshold { get; set; } = 1f;
}

[Guid("bdc4fc87-d7b4-41f1-bc90-fc827005adfc")]
[Authored(DisplayName = "Materials")]
public sealed record MaterialsComponentData
{
    [AuthorDoc("Material documents, one per GLB primitive. A null entry keeps the GLB's own.")]
    public List<string?> Slots { get; set; } = new();
}

[Guid("b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11")]
[Authored(DisplayName = "Rigidbody")]
public sealed record RigidbodyComponentData
{
    [AuthorDoc("Static bodies never move; dynamic ones are simulated.")]
    public PhysicsBodyType BodyType { get; set; }

    [Kilograms, AuthorRange(0.001, 10000)]
    // The guard EntityExport could not express: mass means nothing on a static body, and a
    // field that is meaningless most of the time is a field authors mis-set.
    [AuthorVisibleWhen(nameof(BodyType), PhysicsBodyType.Dynamic)]
    [AuthorDoc("Mass in kilograms. Ignored for static bodies.")]
    public float Mass { get; set; } = 1f;

    [AuthorRange(0, 100), AuthorDoc("Linear velocity bleed-off per second.")]
    public float LinearDamping { get; set; } = 0.2f;

    [Unit01, AuthorDoc("Bounciness: 0 absorbs the impact, 1 returns it.")]
    public float Restitution { get; set; } = 0.2f;

    [Unit01, AuthorDoc("Surface friction: 0 is ice, 1 is grippy.")]
    public float Friction { get; set; } = 0.5f;

    [AuthorDoc("Collision layer index. Prefer LayerName where the project defines one.")]
    public int Layer { get; set; }

    [AuthorDoc("Named collision layer, resolved against the project's layer contract.")]
    public string? LayerName { get; set; } = "";
}
