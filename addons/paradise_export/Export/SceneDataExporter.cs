#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using ParadiseExport.Core.Data;
using ParadiseExport.Core.Geometry;
using ParadiseExport.Core.Paths;
using ParadiseExport.Core.Serialization;
using ParadiseGodot.Authoring;
using SN = System.Numerics;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Walks the edited Godot scene and exports the camera, lights, and entities into an
    /// engine-neutral <see cref="LevelData"/> via the Core library, writing it to
    /// <c>data/scenes/&lt;Scene&gt;.json</c>. Godot's right-handed transforms are converted to the
    /// contract's left-handed convention through <see cref="CoordinateConversion"/>.
    ///
    /// Materials, navmesh, and full lighting/environment fidelity arrive in later phases — see
    /// MIGRATION.md.
    /// </summary>
    internal static class SceneDataExporter
    {
        private static readonly ISceneDocumentWriter Writer = new JsonSceneDocumentWriter();

        public static string? ExportEditedScene(EditorInterface editorInterface)
        {
            Node? root = editorInterface.GetEditedSceneRoot();
            if (root is null)
            {
                GD.PushWarning("[ParadiseExport] No edited scene to export.");
                return null;
            }

            var document = new LevelData();
            foreach (Node node in Descendants(root))
            {
                switch (node)
                {
                    case Camera3D camera when document.Camera is null:
                        document.Camera = ExportCamera(camera);
                        break;
                    case Light3D light:
                        EnsureLightingState(document).Lights.Add(ExportLight(light));
                        break;
                    case EntityExport entity:
                        document.Entities.Add(ExportEntity(entity));
                        break;
                }
            }

            string sceneName = ResolveSceneName(root);
            var paths = new ExportPaths(ProjectSettings.GlobalizePath("res://data"));
            paths.EnsureOutputDirectory();
            string outputPath = paths.GetLevelDataOutputPath(sceneName);
            Writer.Write(outputPath, document);
            GD.Print($"[ParadiseExport] Exported scene data: {outputPath}");
            return outputPath;
        }

        private static CameraData ExportCamera(Camera3D camera) => new()
        {
            Position = CoordinateConversion.Position(ToSN(camera.GlobalPosition)),
            // TODO (later phase): convert Godot right-handed Euler angles to the contract's
            // left-handed convention. Under the Z-mirror, the X/Y Euler components change sign, and
            // the exact mapping depends on the Euler order Godot uses for GlobalRotationDegrees vs
            // Unity's eulerAngles. The ONLY value exercised today is the SampleScene baseline's
            // [0,0,0]; do NOT rely on this raw pass-through for a rotated camera until a
            // rotated-camera golden fixture exists to validate the conversion against.
            Rotation = ToSN(camera.GlobalRotationDegrees),
            // Camera3D.Size is the orthographic half-height (matches Unity's orthographicSize);
            // perspective-camera FOV is out of Phase 1 scope.
            OrthographicSize = camera.Size,
            // Godot has no per-camera background colour (clear colour comes from the environment);
            // the exact source is resolved with lighting/environment fidelity in a later phase.
        };

        private static SceneLightData ExportLight(Light3D light)
        {
            // Godot lights aim down their local -Z; the contract stores a left-handed direction.
            SN.Vector3 forward = ToSN(-light.GlobalTransform.Basis.Z);
            Color color = light.LightColor;
            return new SceneLightData
            {
                Id = light.Name.ToString(),
                Type = LightTypeName(light),
                Position = CoordinateConversion.Position(ToSN(light.GlobalPosition)),
                Direction = CoordinateConversion.Direction(forward),
                Color = Color32.FromRgba(color.R, color.G, color.B, color.A),
                Enabled = light.Visible,
                Intensity = light.LightEnergy,
                ShadowsEnabled = light.ShadowEnabled,
            };
        }

        private static string LightTypeName(Light3D light) => light switch
        {
            DirectionalLight3D => "Directional",
            OmniLight3D => "Point",
            SpotLight3D => "Spot",
            _ => "Directional",
        };

        private static LightingStateData EnsureLightingState(LevelData document)
        {
            document.Lighting ??= new LightingData { ActiveState = "Default" };
            if (document.Lighting.States.Count == 0)
            {
                document.Lighting.States.Add(new LightingStateData { Name = "Default" });
            }

            return document.Lighting.States[0];
        }

        private static string ResolveSceneName(Node root)
        {
            string scenePath = root.SceneFilePath;
            return string.IsNullOrEmpty(scenePath)
                ? root.Name.ToString()
                : Path.GetFileNameWithoutExtension(scenePath);
        }

        private static LevelEntityData ExportEntity(EntityExport entity)
        {
            SN.Vector3 localPos = CoordinateConversion.Position(ToSN(entity.Position));
            SN.Quaternion localRot = CoordinateConversion.Rotation(ToSN(entity.Quaternion));
            SN.Vector3 localScale = ToSN(entity.Scale);

            Transform3D global = entity.GlobalTransform;
            SN.Vector3 worldPos = CoordinateConversion.Position(ToSN(global.Origin));
            SN.Quaternion worldRot = CoordinateConversion.Rotation(ToSN(global.Basis.GetRotationQuaternion()));
            SN.Vector3 worldScale = ToSN(global.Basis.Scale);

            string name = entity.Name.ToString();
            return new LevelEntityData
            {
                Id = name,
                EntityGuid = entity.EntityGuid,
                StableId = name,
                Kind = entity.ResolvedKind,
                SpawnPhase = "LevelStart",
                IsActive = entity.ActiveOnLoad,
                Prefab = NullIfEmpty(entity.ModelPath),
                InitialAnimation = entity.ResolvedInitialAnimation,
                Parent = ResolveParent(entity),
                LocalPosition = localPos,
                LocalRotation = localRot,
                LocalScale = localScale,
                LocalMatrix = ContractMatrix.Trs(localPos, localRot, localScale),
                WorldMatrix = ContractMatrix.Trs(worldPos, worldRot, worldScale),
                Components = BuildComponents(entity),
            };
        }

        private static EntityParentData? ResolveParent(EntityExport entity)
        {
            for (Node? parent = entity.GetParent(); parent is not null; parent = parent.GetParent())
            {
                if (parent is EntityExport ancestor)
                {
                    return new EntityParentData { Id = ancestor.Name.ToString() };
                }
            }

            return null;
        }

        private static EntityComponentsData BuildComponents(EntityExport entity)
        {
            var components = new EntityComponentsData();
            if (!string.IsNullOrEmpty(entity.ModelPath))
            {
                components.Renderable = new RenderableComponentData();
            }

            ColliderComponentData colliders = BuildColliders(entity, entity.PhysicsColliders);
            if (colliders.Colliders.Count > 0)
            {
                components.Collider = colliders;
                components.Rigidbody = BuildRigidbody(entity);
            }

            if (BuildColliders(entity, entity.InteractionColliders).Colliders.Count > 0)
            {
                components.Interactable = new EntityInteractableComponentData { DisplayName = entity.Name.ToString() };
            }

            if (entity.IsAgent)
            {
                components.Agent = new AgentComponentData
                {
                    MoveSpeed = entity.ResolvedMoveSpeed,
                    AngularSpeed = entity.ResolvedAngularSpeed,
                    Acceleration = entity.ResolvedAcceleration,
                    IdleClip = entity.ResolvedIdleAnimation,
                    WalkClip = entity.ResolvedWalkAnimation,
                };
            }

            return components;
        }

        // No RigidBody3D detection yet (EntityExport is a plain Node3D): mirror the Unity fallback —
        // an agent is kinematic, anything else static. Dynamic-body export arrives with the physics
        // pass in a later phase.
        private static RigidbodyComponentData BuildRigidbody(EntityExport entity) => new()
        {
            BodyType = entity.IsAgent ? PhysicsBodyType.Kinematic : PhysicsBodyType.Static,
            Mass = 0f,
            LinearDamping = 0f,
            Restitution = 0.2f,
            Friction = 0.5f,
            Layer = 0,
            LayerName = "",
        };

        private static ColliderComponentData BuildColliders(EntityExport root, Godot.Collections.Array<NodePath> paths)
        {
            var data = new ColliderComponentData();
            foreach (NodePath path in paths)
            {
                if (root.GetNodeOrNull(path) is CollisionShape3D shape &&
                    shape.Shape is not null &&
                    TryExportShape(root, shape, out ColliderShapeData document))
                {
                    data.Colliders.Add(document);
                }
            }

            return data;
        }

        private static bool TryExportShape(Node3D root, CollisionShape3D collider, out ColliderShapeData data)
        {
            data = new ColliderShapeData();
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

            // Collider pose expressed in the entity root's local space, then converted to the
            // contract's left-handed convention.
            Transform3D rootLocal = root.GlobalTransform.AffineInverse() * collider.GlobalTransform;
            data.Id = collider.Name.ToString();
            data.Path = RelativePath(root, collider);
            data.IsTrigger = false;
            data.LayerName = "";
            data.LocalCenter = CoordinateConversion.Position(ToSN(rootLocal.Origin));
            data.LocalRotation = CoordinateConversion.Rotation(ToSN(rootLocal.Basis.GetRotationQuaternion()));
            return true;
        }

        // Root-exclusive path (matches the Unity convention: empty when target == root).
        private static string RelativePath(Node root, Node target)
        {
            if (target == root)
            {
                return "";
            }

            string path = root.GetPathTo(target).ToString();
            return path == "." ? "" : path;
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                yield return child;
                foreach (Node descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private static SN.Vector3 ToSN(Vector3 v) => new(v.X, v.Y, v.Z);

        private static SN.Quaternion ToSN(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
#endif
