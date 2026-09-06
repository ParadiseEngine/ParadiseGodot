#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Paths;
using ParadiseGodot.Documents;
using SN = System.Numerics;

namespace ParadiseGodot.Authoring
{
    /// <summary>
    /// Turns a reference to one of Godot's own objects into the plain numbers the contract carries.
    ///
    /// This is the export half of every <c>authoredBy</c> in the schema: the author points at a
    /// <c>CollisionShape3D</c>, a mesh, a <c>Sprite3D</c> or a file and edits it with Godot's own
    /// tools, and this reads the result out. A node path means nothing to the runtime, so nothing
    /// but values ever crosses the boundary.
    ///
    /// Moved here VERBATIM from SceneDataExporter when authoring became schema-driven. The scale
    /// folding, trigger detection and layer-index mapping are subtle and already covered by the
    /// export contract tests; they were carried across unchanged on purpose.
    /// </summary>
    public static class HostObjectBaker
    {
        // ---- meshes ---------------------------------------------------------------------


        /// <summary>The GLB a node was instanced from, if any. Used to resolve a mesh reference that
        /// points at an instanced model rather than naming a file directly.</summary>
        public static string? SourceGlbOf(Node node) => IsGlbPath(node.SceneFilePath) ? node.SceneFilePath : null;

        public static bool IsGlbPath(string? path) =>
            !string.IsNullOrEmpty(path) &&
            (path!.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".gltf", System.StringComparison.OrdinalIgnoreCase));

        /// <summary>Descendants of an entity, NOT descending into a nested entity (that child owns
        /// its own model), so a parent never claims a child entity's instanced GLB.</summary>
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

        // ---- spritesheets ---------------------------------------------------------------


        /// <summary>Read the geometry half of a sprite animation off the Sprite3D itself — the node
        /// Godot renders natively. Frame pixels × pixel_size is Godot's own world size for the quad.
        /// The playback clock (fps, loop, frame count) stays authored, because no sprite object
        /// holds a frame rate.</summary>
        /// <param name="sheet">The sheet as a reference, or null when the sprite has no standalone
        /// image. Absent rather than empty: a field left out keeps the record's own default, where
        /// an empty reference would assert that the sprite points at nothing.</param>
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

        // ---- environment ----------------------------------------------------------------

        /// <summary>
        /// Read a scene's lighting mood off its <see cref="WorldEnvironment"/> — what
        /// <c>HostEnvironment</c> describes: ambient, background, the procedural-sky gradient, fog,
        /// tone mapping and the two screen-space effects.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Godot keeps one ambient colour; the kind keeps three (sky, equator, ground). With a flat
        /// ambient all three are that colour. With sky ambient over a procedural sky, they are the
        /// gradient's zenith, horizon and nadir — the integration a renderer would do, approximated
        /// as the colours it would integrate.
        /// </para>
        /// <para>
        /// <c>SkyCurve</c> and <c>GroundCurve</c> travel as Godot spells them (0.15 / 0.02): the curve a
        /// sky HAS, not the exponent one shader fits to it. A renderer that wants <c>pow()</c> inverts
        /// it itself — the #238 review's decision, so the kind's field never holds a host property's
        /// reciprocal under the same name.
        /// </para>
        /// <para>
        /// The shadow-map size and blur are left absent: Godot sizes shadows per project, not per
        /// scene, and absent keeps the renderer's own.
        /// </para>
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
            // Disabled ambient is a flat black at zero energy: the kind has no "off", and this is
            // what off looks like to a renderer.
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

        /// <summary>The operator's name in the literature, which is what the kind spells — Godot
        /// calls Reinhard "Reinhardt".</summary>
        private static string TonemapName(global::Godot.Environment.ToneMapper mode) => mode switch
        {
            global::Godot.Environment.ToneMapper.Reinhardt => "Reinhard",
            global::Godot.Environment.ToneMapper.Filmic => "Filmic",
            global::Godot.Environment.ToneMapper.Aces => "Aces",
            global::Godot.Environment.ToneMapper.Agx => "AgX",
            _ => "Linear",
        };

        /// <summary>Read a camera's lens and pose — what <c>HostCamera</c> describes.</summary>
        /// <remarks>Godot cameras look down their local −Z, which is the contract's convention too,
        /// so the world rotation is stored verbatim. <c>Fov</c> is Godot's <c>fov</c>: a VERTICAL
        /// field of view in degrees, which is what the kind declares, so keep_aspect is not
        /// consulted — a host that measured it horizontally would have to convert.</remarks>
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

        // ---- collision shapes -----------------------------------------------------------

        /// <summary>What one collision shape bakes to, in the entity's own local space. The
        /// engine declares no collider record since 0.41 (#261): a game record marked
        /// <c>[AuthoredByHost&lt;HostShape&gt;]</c> takes these leaves by name, so this carrier is
        /// the baker's own and never leaves it.</summary>
        private sealed class BakedShape
        {
            public PhysicsShapeType ShapeType;
            public SN.Vector3 LocalCenter;
            public SN.Quaternion LocalRotation = SN.Quaternion.Identity;
            public SN.Vector3 Size;
            public float Radius;
            public float Height;
            public bool IsTrigger;
            public int Layer;
        }

        /// <summary>Read one collision shape, in the entity's own local space. False when the
        /// shape kind has no contract equivalent.</summary>
        private static bool TryBakeShape(Node3D root, CollisionShape3D collider, BakedShape data)
        {
            SN.Vector3 relativeScale = ColliderScaleFold.RelativeScale(
                ToSN(collider.GlobalTransform.Basis.Scale),
                ToSN(root.GlobalTransform.Basis.Scale));

            switch (collider.Shape)
            {
                case BoxShape3D box:
                    data.ShapeType = PhysicsShapeType.Box;
                    data.Size = ColliderScaleFold.BoxSize(ToSN(box.Size), relativeScale);
                    break;
                case SphereShape3D sphere:
                    data.ShapeType = PhysicsShapeType.Sphere;
                    data.Radius = ColliderScaleFold.SphereRadius(sphere.Radius, relativeScale);
                    break;
                case CapsuleShape3D capsule:
                    data.ShapeType = PhysicsShapeType.Capsule;
                    data.Radius = ColliderScaleFold.CapsuleRadius(capsule.Radius, relativeScale);
                    data.Height = ColliderScaleFold.CapsuleHeight(capsule.Height, relativeScale);
                    break;
                default:
                    return false;
            }

            // Collider pose expressed in the entity root's local space (right-handed, verbatim).
            Transform3D rootLocal = root.GlobalTransform.AffineInverse() * collider.GlobalTransform;
            data.IsTrigger = ResolveIsTrigger(collider);
            data.Layer = ResolveLayerIndex(collider);
            data.LocalCenter = ToSN(rootLocal.Origin);
            data.LocalRotation = ToSN(rootLocal.Basis.GetRotationQuaternion());
            return true;
        }

        // A shape owned by an Area3D is a sensor, Godot's trigger idiom (Unity's isTrigger) —
        // exported through the contract's IsTrigger so the runtime keeps it out of the solid
        // collision world (e.g. pool-pocket capture regions).
        private static bool ResolveIsTrigger(Node shape)
        {
            for (Node? node = shape; node is not null; node = node.GetParent())
            {
                if (node is CollisionObject3D body)
                {
                    return body is Area3D;
                }
            }

            return false;
        }

        // Godot stores collision layers as a bitmask on the owning body; the engine-neutral
        // contract carries a Unity-style single layer INDEX (consumers do 1u << Layer). Map the
        // nearest CollisionObject3D ancestor's mask to the index of its lowest set bit; an
        // unlayered body maps to 0.
        private static int ResolveLayerIndex(Node shape)
        {
            for (Node? node = shape; node is not null; node = node.GetParent())
            {
                if (node is CollisionObject3D body)
                {
                    uint mask = body.CollisionLayer;
                    if (CollisionLayerContract.IsMultiLayer(mask))
                    {
                        // The single-int contract can't carry multi-layer membership — the .NET
                        // runtime would see only the lowest bit while the Godot bridge keeps all.
                        // Be loud instead of silently lossy.
                        GD.PushWarning(
                            $"[Paradise.Export] Body '{body.GetPath()}' is on multiple collision layers "
                            + $"(mask {mask}); the export contract keeps only the lowest "
                            + $"(index {CollisionLayerContract.MaskToLayerIndex(mask)}).");
                    }

                    return CollisionLayerContract.MaskToLayerIndex(mask);
                }
            }

            return 0;
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

        // ---- lights ---------------------------------------------------------------------

        /// <summary>Read a light into the leaves <c>HostLight</c> describes.</summary>
        /// <remarks>The light's identity is NOT here. An object's identity travels in the format's
        /// <c>meta</c>, and v6 deleted the record that used to carry a second one.</remarks>
        public static Dictionary<string, AuthoredValue> BakeLight(Light3D light)
        {
            // Godot lights aim down their local -Z; the contract is right-handed, so this world-space
            // forward is stored verbatim.
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
                // Godot's shadow_opacity (1 = fully dark) maps to the kind's shadow strength.
                ["ShadowStrength"] = Number(light.ShadowOpacity),
                ["Specular"] = Number(light.GetParam(Light3D.Param.Specular)),
                ["Size"] = Number(light.GetParam(Light3D.Param.Size)),
                // Point/spot need range + cone. Godot's SpotAngle is the HALF-angle (axis to edge);
                // the kind and the shader use the FULL cone angle, so double it.
                ["Range"] = Number(light switch
                {
                    OmniLight3D omni => omni.OmniRange,
                    SpotLight3D spot => spot.SpotRange,
                    _ => 0f,
                }),
                ["SpotAngle"] = Number(light is SpotLight3D s ? s.SpotAngle * 2f : 0f),
                // Distance-falloff exponent (Godot's LIGHT_PARAM_ATTENUATION). Godot's default 1.0
                // is inverse-linear; the shader applies pow(distance, -exponent). Directionals have
                // no range falloff, so the value is written but unused for them.
                ["AttenuationExponent"] = Number(light.GetParam(Light3D.Param.Attenuation)),
            };
        }

        /// <summary>Read a collision shape into the leaves <c>HostShape</c> describes, plus the
        /// per-axis spelling a game record may use instead.</summary>
        /// <remarks>Both vocabularies are offered because a record decides which it wants: the
        /// engine's collider took <c>Size</c> and <c>LocalCenter</c>, a game's box part takes
        /// <c>SizeX</c>/<c>SizeY</c>/<c>SizeZ</c>, and both are baked from one CollisionShape3D.
        /// The caller keeps whichever the record declared.</remarks>
        public static Dictionary<string, AuthoredValue>? BakeShape(Node3D root, CollisionShape3D collider)
        {
            var data = new BakedShape();
            if (!TryBakeShape(root, collider, data)) return null;

            return new Dictionary<string, AuthoredValue>(StringComparer.Ordinal)
            {
                ["ShapeType"] = Text(data.ShapeType.ToString()),
                ["LocalCenter"] = Numbers(data.LocalCenter.X, data.LocalCenter.Y, data.LocalCenter.Z),
                ["LocalRotation"] = Numbers(
                    data.LocalRotation.X, data.LocalRotation.Y, data.LocalRotation.Z, data.LocalRotation.W),
                ["Size"] = Numbers(data.Size.X, data.Size.Y, data.Size.Z),
                ["Radius"] = Number(data.Radius),
                ["Height"] = Number(data.Height),
                ["IsTrigger"] = Boolean(data.IsTrigger),
                ["Layer"] = Integer(data.Layer),
                ["SizeX"] = Number(data.Size.X),
                ["SizeY"] = Number(data.Size.Y),
                ["SizeZ"] = Number(data.Size.Z),
                ["CenterX"] = Number(data.LocalCenter.X),
                ["CenterY"] = Number(data.LocalCenter.Y),
                ["CenterZ"] = Number(data.LocalCenter.Z),
            };
        }

        // ---- leaf constructors ----------------------------------------------------------

        public static AuthoredValue Text(string value) => new(AuthoredValueKind.Text, Text: value);

        public static AuthoredValue Number(double value) => new(AuthoredValueKind.Number, Number: value);

        public static AuthoredValue Integer(long value) => new(AuthoredValueKind.Integer, Integer: value);

        public static AuthoredValue Boolean(bool value) => new(AuthoredValueKind.Bool, Bool: value);

        public static AuthoredValue Numbers(params float[] values) =>
            new(AuthoredValueKind.Numbers, Numbers: values);

        /// <summary>A colour as four channels in 0..1 — the shape the generated reader parses.</summary>
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
        private static SN.Quaternion ToSN(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
#endif
