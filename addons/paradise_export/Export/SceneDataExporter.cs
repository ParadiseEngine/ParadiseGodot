#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using ParadiseExport.Data;
using ParadiseExport.Geometry;
using ParadiseExport.NavMesh;
using ParadiseExport.Paths;
using ParadiseExport.Serialization;
using ParadiseGodot.Authoring;
using SN = System.Numerics;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Walks the edited Godot scene and exports the camera, lights, and entities into an
    /// engine-neutral <see cref="LevelData"/> via the Core library, writing it to
    /// <c>data/scenes/&lt;Scene&gt;.json</c>. The contract is right-handed (Y-up, −Z forward), matching
    /// Godot, so transforms are written verbatim with no handedness conversion.
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

            string sceneName = ResolveSceneName(root);
            var paths = new ExportPaths(ProjectSettings.GlobalizePath("res://data"));
            var document = new LevelData();
            var materials = new MaterialExporter();
            var prefabs = new PrefabExporter(materials, paths);
            paths.EnsureOutputDirectory();
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
                        document.Entities.Add(ExportEntity(entity, materials, prefabs));
                        break;
                }
            }

            ProjectSettingsExporter.Export(paths);
            materials.WriteExportedMaterials(paths);
            ExportNavMesh(root, sceneName, paths, document);
            string outputPath = paths.GetLevelDataOutputPath(sceneName);
            Writer.Write(outputPath, document);
            GD.Print($"[ParadiseExport] Exported scene data: {outputPath}");
            return outputPath;
        }

        private static CameraData ExportCamera(Camera3D camera) => new()
        {
            Position = ToSN(camera.GlobalPosition),
            Rotation = ToSN(camera.GlobalRotationDegrees),
            // Camera3D.Size is the orthographic half-height (matches Unity's orthographicSize);
            // perspective-camera FOV is out of Phase 1 scope.
            OrthographicSize = camera.Size,
            // Godot has no per-camera background colour (clear colour comes from the environment);
            // the exact source is resolved with lighting/environment fidelity in a later phase.
        };

        private static SceneLightData ExportLight(Light3D light)
        {
            // Godot lights aim down their local -Z; the contract is right-handed, so this world-space
            // forward is stored verbatim.
            SN.Vector3 forward = ToSN(-light.GlobalTransform.Basis.Z);
            Color color = light.LightColor;
            return new SceneLightData
            {
                Id = light.Name.ToString(),
                Type = LightTypeName(light),
                Position = ToSN(light.GlobalPosition),
                Direction = forward,
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

        // Bake the scene's static-collider navmesh and write it as the runtime's DotRecast binary,
        // recording the filename on the document. Failures (no walkable geometry, bake error) leave
        // NavMeshFile null rather than aborting the scene export.
        private static void ExportNavMesh(Node root, string sceneName, ExportPaths paths, LevelData document)
        {
            try
            {
                if (!NavMeshBake.TryBake(root, out var vertices, out var triangles))
                {
                    return;
                }

                string navMeshPath = paths.GetNavMeshOutputPath(sceneName);
                NavMeshBinaryWriter.Write(navMeshPath, vertices, triangles,
                    message => GD.PushWarning($"[ParadiseExport] {message}"));
                document.NavMeshFile = paths.GetNavMeshFileField(sceneName);
                GD.Print($"[ParadiseExport] Exported navmesh: {navMeshPath}");
            }
            catch (System.Exception ex)
            {
                GD.PushWarning($"[ParadiseExport] NavMesh export skipped: {ex.Message}");
            }
        }

        private static LevelEntityData ExportEntity(EntityExport entity, MaterialExporter materials, PrefabExporter prefabs)
        {
            SN.Vector3 localPos = ToSN(entity.Position);
            SN.Quaternion localRot = ToSN(entity.Quaternion);
            SN.Vector3 localScale = ToSN(entity.Scale);

            Transform3D global = entity.GlobalTransform;
            SN.Vector3 worldPos = ToSN(global.Origin);
            SN.Quaternion worldRot = ToSN(global.Basis.GetRotationQuaternion());
            SN.Vector3 worldScale = ToSN(global.Basis.Scale);

            PrefabExporter.Identity prefab = prefabs.ResolveAndExport(entity);
            string name = entity.Name.ToString();
            return new LevelEntityData
            {
                Id = name,
                // Mint+persist a GUID if the node has never been saved, so we never export the
                // all-zero GUID (which would collide across entities at runtime).
                EntityGuid = entity.EnsureEntityGuid(),
                StableId = name,
                Kind = entity.ResolvedKind,
                SpawnPhase = "LevelStart",
                IsActive = entity.ActiveOnLoad,
                Prefab = NullIfEmpty(entity.ModelPath),
                PrefabAssetPath = prefab.PrefabAssetPath,
                PrefabGuid = prefab.PrefabGuid,
                PrefabAssetType = prefab.PrefabAssetType,
                NearestInstanceRoot = prefab.NearestInstanceRoot,
                InitialAnimation = entity.ResolvedInitialAnimation,
                Parent = ResolveParent(entity),
                LocalPosition = localPos,
                LocalRotation = localRot,
                LocalScale = localScale,
                LocalMatrix = ContractMatrix.Trs(localPos, localRot, localScale),
                WorldMatrix = ContractMatrix.Trs(worldPos, worldRot, worldScale),
                Materials = materials.ExportMaterialSlots(entity),
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

            // Interaction collider geometry is not forwarded yet (EntityInteractableComponentData
            // only carries a display name today); presence is enough to flag the component. Build
            // the set once. Forwarding the shapes is deferred to a later phase.
            ColliderComponentData interaction = BuildColliders(entity, entity.InteractionColliders);
            if (interaction.Colliders.Count > 0)
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

            // Collider pose expressed in the entity root's local space (right-handed, verbatim).
            Transform3D rootLocal = root.GlobalTransform.AffineInverse() * collider.GlobalTransform;
            data.Id = collider.Name.ToString();
            data.Path = RelativePath(root, collider);
            data.IsTrigger = false;
            data.LayerName = "";
            data.LocalCenter = ToSN(rootLocal.Origin);
            data.LocalRotation = ToSN(rootLocal.Basis.GetRotationQuaternion());
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
