#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;
using SN = System.Numerics;

namespace ParadiseGodot.Authoring
{
    /// <summary>Bake Godot objects into the values described by schema authoredBy kinds.</summary>
    /// <remarks>Documents carry values; Godot node paths have no runtime meaning.</remarks>
    public static class HostObjectBaker
    {
        // Meshes


        /// <summary>The source GLB/glTF of an instanced node, or null.</summary>
        public static string? SourceGlbOf(Node node) => IsGlbPath(node.SceneFilePath) ? node.SceneFilePath : null;

        public static bool IsGlbPath(string? path) => path is not null && ModelDocuments.IsGlb(path);

        /// <summary>Model descendants, excluding nested entities that own their own models.</summary>
        public static IEnumerable<Node> ModelDescendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is IAuthoredEntity)
                {
                    continue;
                }

                yield return child;
                foreach (Node descendant in ModelDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        // Sprite sheets


        /// <summary>Bake sprite geometry using frame pixels times PixelSize for world size.</summary>
        /// <remarks>Playback fields stay authored because Sprite3D has no playback clock.</remarks>
        /// <param name="sheet">Standalone image reference, or null to preserve the record's default.</param>
        public static Dictionary<string, AuthoredValue> BakeSprite(Sprite3D sprite, AuthoredValue? sheet)
        {
            float frameWidth = sprite.Texture is { } texture
                ? texture.GetWidth() / (float)System.Math.Max(1, sprite.Hframes)
                : 0f;
            float frameHeight = sprite.Texture is { } tex2
                ? tex2.GetHeight() / (float)System.Math.Max(1, sprite.Vframes)
                : 0f;

            var leaves = new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["Columns"] = Integer(sprite.Hframes),
                ["Rows"] = Integer(sprite.Vframes),
                ["QuadSize"] = Numbers(frameWidth * sprite.PixelSize, frameHeight * sprite.PixelSize),
                ["Billboard"] = Boolean(sprite.Billboard != BaseMaterial3D.BillboardModeEnum.Disabled),
            };

            if (sheet is { } reference) leaves["Sheet"] = reference;
            return leaves;
        }

        // Environment

        /// <summary>Bake the lighting, sky, fog and effects described by HostEnvironment.</summary>
        /// <remarks>
        /// Flat ambient repeats one colour across sky/equator/ground; procedural sky ambient
        /// approximates them with the zenith, horizon and nadir colours.
        /// SkyCurve and GroundCurve retain Godot's values; renderers invert them if needed.
        /// Shadow-map size and blur stay absent to preserve renderer defaults, since Godot sets
        /// shadow sizing per project.
        /// </remarks>
        public static Dictionary<string, AuthoredValue> BakeEnvironment(global::Godot.Environment environment)
        {
            ProceduralSkyMaterial? sky = environment.Sky?.SkyMaterial as ProceduralSkyMaterial;
            bool skyBackground = environment.BackgroundMode == global::Godot.Environment.BGMode.Sky;
            bool skyAmbient = environment.AmbientLightSource switch
            {
                global::Godot.Environment.AmbientSource.Sky => true,
                global::Godot.Environment.AmbientSource.Bg => skyBackground,
                _ => false,
            };
            bool skyReflections = environment.ReflectedLightSource switch
            {
                global::Godot.Environment.ReflectionSource.Sky => true,
                global::Godot.Environment.ReflectionSource.Bg => skyBackground,
                _ => false,
            };

            Color ambient = environment.AmbientLightColor;
            Color ambientSky = skyAmbient && sky is not null ? sky.SkyTopColor : ambient;
            Color ambientEquator = skyAmbient && sky is not null ? sky.SkyHorizonColor.Lerp(sky.GroundHorizonColor, 0.5f) : ambient;
            Color ambientGround = skyAmbient && sky is not null ? sky.GroundBottomColor : ambient;
            // The contract has no ambient-off mode; zero energy disables it.
            bool ambientOff = environment.AmbientLightSource == global::Godot.Environment.AmbientSource.Disabled;

            var leaves = new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["AmbientMode"] = Text(skyAmbient ? "Sky" : "Color"),
                ["AmbientSky"] = Rgba(ambientSky),
                ["AmbientEquator"] = Rgba(ambientEquator),
                ["AmbientGround"] = Rgba(ambientGround),
                ["AmbientEnergy"] = Number(ambientOff ? 0f : environment.AmbientLightEnergy),
                ["SkyReflections"] = Boolean(skyReflections),
                ["HasBackground"] = Boolean(skyBackground || environment.BackgroundMode == global::Godot.Environment.BGMode.Color),
                ["BackgroundColor"] = Rgba(environment.BackgroundColor),
                ["SkyGradient"] = Boolean(skyBackground && sky is not null),
                ["TonemapMode"] = Text(TonemapName(environment.TonemapMode)),
                ["TonemapExposure"] = Number(environment.TonemapExposure),
                ["TonemapWhite"] = Number(environment.TonemapWhite),
                ["FogEnabled"] = Boolean(environment.FogEnabled),
                ["FogColor"] = Rgba(environment.FogLightColor),
                ["FogDensity"] = Number(environment.FogDensity),
                ["SsaoEnabled"] = Boolean(environment.SsaoEnabled),
                ["SsaoRadius"] = Number(environment.SsaoRadius),
                ["SsaoIntensity"] = Number(environment.SsaoIntensity),
                ["SsaoPower"] = Number(environment.SsaoPower),
                ["GlowEnabled"] = Boolean(environment.GlowEnabled),
                ["GlowIntensity"] = Number(environment.GlowIntensity),
                ["GlowThreshold"] = Number(environment.GlowHdrThreshold),
            };

            if (sky is not null)
            {
                leaves["SkyTop"] = Rgba(sky.SkyTopColor);
                leaves["SkyHorizon"] = Rgba(sky.SkyHorizonColor);
                leaves["GroundHorizon"] = Rgba(sky.GroundHorizonColor);
                leaves["GroundBottom"] = Rgba(sky.GroundBottomColor);
                leaves["SkyCurve"] = Number(sky.SkyCurve);
                leaves["GroundCurve"] = Number(sky.GroundCurve);
            }

            return leaves;
        }

        /// <summary>Contract spelling: Godot calls Reinhard "Reinhardt".</summary>
        private static string TonemapName(global::Godot.Environment.ToneMapper mode) => mode switch
        {
            global::Godot.Environment.ToneMapper.Reinhardt => "Reinhard",
            global::Godot.Environment.ToneMapper.Filmic => "Filmic",
            global::Godot.Environment.ToneMapper.Aces => "Aces",
            global::Godot.Environment.ToneMapper.Agx => "AgX",
            _ => "Linear",
        };

        /// <summary>Bake the lens and world pose described by HostCamera.</summary>
        /// <remarks>Both conventions face local -Z. Fov carries Godot's fov value in degrees
        /// without a keep_aspect conversion.</remarks>
        public static Dictionary<string, AuthoredValue> BakeCamera(Camera3D camera)
        {
            Transform3D global = camera.GlobalTransform;
            Quaternion rotation = global.Basis.GetRotationQuaternion();
            return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["Projection"] = Text(camera.Projection == Camera3D.ProjectionType.Orthogonal
                    ? "Orthographic"
                    : "Perspective"),
                ["Fov"] = Number(camera.Fov),
                ["OrthographicSize"] = Number(camera.Size),
                ["Near"] = Number(camera.Near),
                ["Far"] = Number(camera.Far),
                ["Position"] = Numbers(global.Origin.X, global.Origin.Y, global.Origin.Z),
                ["Rotation"] = Numbers(rotation.X, rotation.Y, rotation.Z, rotation.W),
            };
        }

        // Collision shapes

        /// <summary>Bake HostShape leaves plus per-axis size and center fields.</summary>
        /// <remarks>Records may declare vector fields or scalar axes; the caller selects their leaves.</remarks>
        public static Dictionary<string, AuthoredValue>? BakeShape(Node3D root, CollisionShape3D collider)
        {
            Transform3D colliderTransform = collider.GlobalTransform;
            Transform3D rootTransform = root.GlobalTransform;
            SN.Vector3 relativeScale = ColliderScaleFold.RelativeScale(
                ToSN(colliderTransform.Basis.Scale), ToSN(rootTransform.Basis.Scale));
            PhysicsShapeType shapeType;
            SN.Vector3 size = default;
            float radius = 0f;
            float height = 0f;

            switch (collider.Shape)
            {
                case BoxShape3D box:
                    shapeType = PhysicsShapeType.Box;
                    size = ColliderScaleFold.BoxSize(ToSN(box.Size), relativeScale);
                    break;
                case SphereShape3D sphere:
                    shapeType = PhysicsShapeType.Sphere;
                    radius = ColliderScaleFold.SphereRadius(sphere.Radius, relativeScale);
                    break;
                case CapsuleShape3D capsule:
                    shapeType = PhysicsShapeType.Capsule;
                    radius = ColliderScaleFold.CapsuleRadius(capsule.Radius, relativeScale);
                    height = ColliderScaleFold.CapsuleHeight(capsule.Height, relativeScale);
                    break;
                default:
                    return null;
            }

            // Both coordinate systems are right-handed; no pose conversion is needed.
            Transform3D rootLocal = rootTransform.AffineInverse() * colliderTransform;
            Vector3 center = rootLocal.Origin;
            Quaternion rotation = rootLocal.Basis.GetRotationQuaternion();
            CollisionObject3D? body = CollisionBodyOf(collider);
            return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["ShapeType"] = Text(shapeType.ToString()),
                ["LocalCenter"] = Numbers(center.X, center.Y, center.Z),
                ["LocalRotation"] = Numbers(rotation.X, rotation.Y, rotation.Z, rotation.W),
                ["Size"] = Numbers(size.X, size.Y, size.Z),
                ["Radius"] = Number(radius),
                ["Height"] = Number(height),
                // Area3D shapes are triggers, excluded from the runtime solid collision world.
                ["IsTrigger"] = Boolean(body is Area3D),
                ["Layer"] = Integer(ResolveLayerIndex(body)),
                ["SizeX"] = Number(size.X),
                ["SizeY"] = Number(size.Y),
                ["SizeZ"] = Number(size.Z),
                ["CenterX"] = Number(center.X),
                ["CenterY"] = Number(center.Y),
                ["CenterZ"] = Number(center.Z),
            };
        }

        private static CollisionObject3D? CollisionBodyOf(Node shape)
        {
            for (Node? node = shape; node is not null; node = node.GetParent())
            {
                if (node is CollisionObject3D body) return body;
            }
            return null;
        }

        // Godot stores a layer mask; the contract stores one index (consumers use 1u << Layer).
        // Use the nearest body's lowest set bit, or index 0 for an empty mask.
        private static int ResolveLayerIndex(CollisionObject3D? body)
        {
            if (body is null) return 0;

            uint mask = body.CollisionLayer;
            if (CollisionLayerContract.IsMultiLayer(mask))
            {
                // Warn because the contract cannot preserve multi-layer membership.
                GD.PushWarning(
                    $"[Paradise.Export] Body '{body.GetPath()}' is on multiple collision layers "
                    + $"(mask {mask}); the export contract keeps only the lowest "
                    + $"(index {CollisionLayerContract.MaskToLayerIndex(mask)}).");
            }
            return CollisionLayerContract.MaskToLayerIndex(mask);
        }

        public static string RelativePath(Node root, Node target)
        {
            if (target == root)
            {
                return "";
            }

            string path = root.GetPathTo(target).ToString();
            return path == "." ? "" : path;
        }

        // Lights

        /// <summary>Bake the values described by HostLight.</summary>
        /// <remarks>Object identity belongs to document meta, not light leaves.</remarks>
        public static Dictionary<string, AuthoredValue> BakeLight(Light3D light)
        {
            // Godot lights face local -Z, matching the contract's world-space direction.
            Vector3 forward = -light.GlobalTransform.Basis.Z;
            Vector3 position = light.GlobalPosition;
            Color color = light.LightColor;
            return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["Type"] = Text(LightTypeName(light)),
                ["Position"] = Numbers(position.X, position.Y, position.Z),
                ["Direction"] = Numbers(forward.X, forward.Y, forward.Z),
                ["Color"] = Rgba(color),
                ["Enabled"] = Boolean(light.Visible),
                ["Intensity"] = Number(light.LightEnergy),
                ["ShadowsEnabled"] = Boolean(light.ShadowEnabled),
                // ShadowOpacity is strength: 1 means fully dark.
                ["ShadowStrength"] = Number(light.ShadowOpacity),
                ["Specular"] = Number(light.GetParam(Light3D.Param.Specular)),
                ["Size"] = Number(light.GetParam(Light3D.Param.Size)),
                // Godot stores a spot half-angle; the contract and shader require the full cone.
                ["Range"] = Number(light switch
                {
                    OmniLight3D omni => omni.OmniRange,
                    SpotLight3D spot => spot.SpotRange,
                    _ => 0f,
                }),
                ["SpotAngle"] = Number(light is SpotLight3D s ? s.SpotAngle * 2f : 0f),
                // The shader applies pow(distance, -exponent); 1 is inverse-linear.
                // Directional lights ignore distance attenuation.
                ["AttenuationExponent"] = Number(light.GetParam(Light3D.Param.Attenuation)),
            };
        }

        // Leaf constructors

        public static AuthoredValue Text(string value) => new(AuthoredValueKind.Text, Text: value);

        public static AuthoredValue Number(double value) => new(AuthoredValueKind.Number, Number: value);

        public static AuthoredValue Integer(long value) => new(AuthoredValueKind.Integer, Integer: value);

        public static AuthoredValue Boolean(bool value) => new(AuthoredValueKind.Bool, Bool: value);

        public static AuthoredValue Numbers(params float[] values) =>
            new(AuthoredValueKind.Numbers, Numbers: values);

        /// <summary>RGBA channels in the generated reader's 0..1 format.</summary>
        public static AuthoredValue Rgba(Color c) =>
            new(AuthoredValueKind.Rgba, Numbers: [c.R, c.G, c.B, c.A]);

        private static string LightTypeName(Light3D light) => light switch
        {
            DirectionalLight3D => "Directional",
            OmniLight3D => "Point",
            SpotLight3D => "Spot",
            _ => "Directional",
        };

        private static SN.Vector3 ToSN(Vector3 v) => new(v.X, v.Y, v.Z);
    }
}
#endif
