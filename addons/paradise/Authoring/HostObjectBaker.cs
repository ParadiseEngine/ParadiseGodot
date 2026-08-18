#if TOOLS
using System.Collections.Generic;
using Godot;
using Paradise.Export.Data;
using Paradise.Export.Geometry;
using Paradise.Export.Paths;
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

        /// <summary>The data-relative GLB field for a mesh reference, or null (with a warning) when
        /// it resolves outside the data directory and so could never load at runtime.</summary>
        public static string? MeshField(Node3D entity, string? sourcePath, ExportPaths paths)
        {
            if (!IsGlbPath(sourcePath))
            {
                return null;
            }

            string? field = paths.DataRelativeMeshField(sourcePath!);
            if (field is null)
            {
                GD.PushWarning(
                    $"[Paradise.Export] Entity '{entity.Name}' references model '{sourcePath}' outside "
                    + $"{ParadisePaths.DataDirPrefix} — the runtime resolves meshes under the data directory, "
                    + "so it will not render. Move the asset there.");
            }
            return field;
        }

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

        /// <summary>
        /// A spritesheet contract field: the source image resolved under <c>data/sprites/</c>, stored
        /// with the runtime (.ktx2) extension — the sidecar the data-ingest pass encodes next to the
        /// source. Null (with a warning) when the image is a sub-resource or lives outside
        /// <c>data/sprites/</c>: the resolver accepts EXACTLY the set the sidecar pass covers, so an
        /// exported sheet field always has a generator.
        /// </summary>
        public static string? SheetField(Node3D entity, string? texturePath, ExportPaths paths)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }

            if (texturePath!.Contains("::", System.StringComparison.Ordinal))
            {
                GD.PushWarning(
                    $"[Paradise.Export] Entity '{entity.Name}' uses a sub-resource spritesheet ('{texturePath}') — "
                    + $"the runtime needs a standalone image under {ParadisePaths.SpritesDir}/. The sheet is not exported.");
                return null;
            }

            string? field = paths.DataRelativeMeshField(texturePath);
            if (field is null || !field.StartsWith("sprites/", System.StringComparison.Ordinal))
            {
                GD.PushWarning(
                    $"[Paradise.Export] Entity '{entity.Name}' references spritesheet '{texturePath}' outside "
                    + $"{ParadisePaths.SpritesDir}/ — the KTX2 sidecar pass only covers that directory, so the .NET "
                    + "runtime could never load it. Move the image under the sprites directory. The sheet is not exported.");
                return null;
            }

            return System.IO.Path.ChangeExtension(field, ".ktx2");
        }

        /// <summary>Read the geometry half of a sprite animation off the Sprite3D itself — the node
        /// Godot renders natively. Frame pixels × pixel_size is Godot's own world size for the quad.
        /// The playback clock (fps, loop, frame count) stays authored.</summary>
        public static void BakeSprite(
            Node3D entity, Sprite3D sprite, ExportPaths paths, SpriteAnimationComponentData data)
        {
            float frameWidth = sprite.Texture is { } texture
                ? texture.GetWidth() / (float)System.Math.Max(1, sprite.Hframes)
                : 0f;
            float frameHeight = sprite.Texture is { } tex2
                ? tex2.GetHeight() / (float)System.Math.Max(1, sprite.Vframes)
                : 0f;

            data.Sheet = SheetField(entity, sprite.Texture?.ResourcePath, paths);
            data.Columns = sprite.Hframes;
            data.Rows = sprite.Vframes;
            data.QuadSize = new SN.Vector2(frameWidth * sprite.PixelSize, frameHeight * sprite.PixelSize);
            data.Billboard = sprite.Billboard != BaseMaterial3D.BillboardModeEnum.Disabled;
        }

        // ---- collision shapes -----------------------------------------------------------

        /// <summary>Read one collision shape into the contract, in the entity's own local space.
        /// False when the shape kind has no contract equivalent.</summary>
        public static bool TryBakeShape(Node3D root, CollisionShape3D collider, ColliderShapeData data)
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
            data.Id = collider.Name.ToString();
            data.Path = RelativePath(root, collider);
            data.IsTrigger = ResolveIsTrigger(collider);
            data.Layer = ResolveLayerIndex(collider);
            data.LayerName = "";
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

        private static SN.Vector3 ToSN(Vector3 v) => new(v.X, v.Y, v.Z);
        private static SN.Quaternion ToSN(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
#endif
