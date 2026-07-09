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

            return ExportRoot(root);
        }

        /// <summary>Export an arbitrary IN-TREE scene root (exporters read GlobalTransform, so
        /// the node must be inside a tree). Used by the editor path above and by the headless
        /// export hook (PARADISE_EXPORT_SCENE) that regenerates data/ in CI.</summary>
        public static string? ExportRoot(Node root)
        {
            string sceneName = ResolveSceneName(root);
            var paths = new ExportPaths(ProjectSettings.GlobalizePath("res://data"));
            var document = new LevelData();
            var materials = new MaterialExporter();
            var prefabs = new PrefabExporter(materials, paths);
            paths.EnsureOutputDirectory();
            var environmentExported = false; // only the first WorldEnvironment in the tree is used
            foreach (Node node in Descendants(root))
            {
                switch (node)
                {
                    case Camera3D camera when document.Camera is null:
                        document.Camera = ExportCamera(camera);
                        break;
                    case WorldEnvironment { Environment: { } env } when !environmentExported:
                        ExportEnvironment(env, EnsureLightingState(document).Environment);
                        environmentExported = true;
                        break;
                    case Light3D light:
                        EnsureLightingState(document).Lights.Add(ExportLight(light));
                        break;
                    case EntityExport entity:
                        document.Entities.Add(ExportEntity(entity, materials, prefabs, paths));
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

        // Export the Godot Environment. For now only tone mapping is carried across (the runtime
        // renderer applies the matching operator before the sRGB encode); ambient/sky/fog fidelity
        // is resolved in a later pass. TonemapMode names follow Godot's ToneMapper enum
        // (Linear, Reinhardt, Filmic, Aces, Agx) — the runtime parses them case-insensitively.
        private static void ExportEnvironment(Godot.Environment env, EnvironmentData data)
        {
            data.TonemapMode = env.TonemapMode.ToString();
            data.TonemapExposure = env.TonemapExposure;
            data.TonemapWhite = env.TonemapWhite;

            // Screen-space AO (Godot Environment.ssao_*). The runtime approximates Godot's GTAO with
            // a world-position pre-pass + hemisphere estimate — radius/intensity/power map across
            // directly. ssao_detail/horizon/sharpness/light_affect are GTAO-specific tuning that the
            // hemisphere approximation doesn't model, so they're intentionally not forwarded.
            data.SsaoEnabled = env.SsaoEnabled;
            data.SsaoRadius = env.SsaoRadius;
            data.SsaoIntensity = env.SsaoIntensity;
            data.SsaoPower = env.SsaoPower;

            // Ambient: a Sky source with a procedural sky is a hemisphere lit by the sky's
            // top/horizon/ground colours; anything else is a flat ambient colour. AmbientMode is set
            // by the branch that actually runs (a Sky source with a non-procedural/null material
            // still falls through to flat, and must read "Color", not "Skybox"). Colours are
            // linearized to match the engine's linear-space ambient term (as with emission export).
            data.AmbientEnergy = env.AmbientLightEnergy;
            data.HasBackground = true; // a real WorldEnvironment was exported → its clear is authoritative

            bool skyAmbient = env.AmbientLightSource == Godot.Environment.AmbientSource.Sky;
            if (skyAmbient && env.Sky?.SkyMaterial is ProceduralSkyMaterial sky)
            {
                data.AmbientMode = "Skybox";
                // Hemisphere-ambient IRRADIANCE per zone, not raw directional sky radiance. An
                // up-facing surface integrates the whole upper sky (bright ≈ mean of top+horizon);
                // a side-facing surface sees roughly half sky + half ground; a down-facing surface
                // sees the dark sky-ground. This 3-point lerp APPROXIMATES Godot's sky-SH ambient
                // distribution (up brightest, sides/undersides dark) — the raw zenith colour was too
                // dim up and the raw horizon colour far too bright on sides, which flattened shading.
                Color skyIrr = sky.SkyTopColor.SrgbToLinear().Lerp(sky.SkyHorizonColor.SrgbToLinear(), 0.5f);
                Color groundIrr = sky.GroundBottomColor.SrgbToLinear();
                Color sideIrr = skyIrr.Lerp(groundIrr, 0.5f);
                data.AmbientColor = ToColor32(skyIrr);
                data.AmbientEquatorColor = ToColor32(sideIrr);
                data.AmbientGroundColor = ToColor32(groundIrr);
                // A downward-looking camera sees mostly the sky's lower (ground) hemisphere, so use
                // its bottom colour as the flat clear tone. Kept in sRGB (the clear bypasses the
                // shader tonemap/OETF, and the scene pixels around it are sRGB-encoded).
                data.BackgroundColor = ToColor32(sky.GroundBottomColor);

                // Gradient-sky background. With the camera pitched down, the visible background is the
                // sky's dark lower/ground hemisphere everywhere, lifting slightly toward the top edge
                // where it catches the horizon band. So: screen top = ground bottom lifted a little
                // toward the horizon colour; screen bottom = the dark ground bottom. Tone-mapped here
                // (exposure + white applied) so the shader just lerps + encodes and the sky sits in the
                // scene's tonemapped space. NOTE: always uses the Filmic curve regardless of
                // TonemapMode — for a non-Filmic scene this is an approximation, which is acceptable
                // for a background gradient (the exposure/white ARE honoured for all modes).
                Color groundBottomLin = sky.GroundBottomColor.SrgbToLinear();
                Color groundHorizonLin = sky.GroundHorizonColor.SrgbToLinear();
                float tmExposure = env.TonemapExposure;
                float tmWhite = env.TonemapWhite;
                data.SkyGradient = true;
                data.SkyTopColor = ToColor32(FilmicTonemap(groundBottomLin.Lerp(groundHorizonLin, 0.12f), tmExposure, tmWhite));
                data.SkyHorizonColor = ToColor32(FilmicTonemap(groundBottomLin, tmExposure, tmWhite));
            }
            else
            {
                data.AmbientMode = "Color";
                Color a = env.AmbientLightColor.SrgbToLinear();
                data.AmbientColor = data.AmbientEquatorColor = data.AmbientGroundColor = ToColor32(a);
                data.BackgroundColor = ToColor32(env.BackgroundColor);
            }
        }

        private static Color32 ToColor32(Color c) => Color32.FromRgba(c.R, c.G, c.B, c.A);

        // Godot's Filmic tone operator (Environment tonemap_mode = 2), per channel, matching
        // pbr.slang's tonemapFilmic (exposure_bias 2 → A=0.22*4, B=0.30*2; white-point normalized).
        // exposure scales the linear input, white sets the normalization point — both applied so the
        // exported sky sits in the scene's tonemapped space for any exposure/white.
        private static Color FilmicTonemap(Color linear, float exposure, float white)
        {
            static float Curve(float x)
            {
                const float A = 0.22f * 4f, B = 0.30f * 2f, C = 0.10f, D = 0.20f, E = 0.01f, F = 0.30f;
                return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
            }
            float w = Curve(white < 1e-4f ? 1e-4f : white);
            return new Color(
                Curve(linear.R * exposure) / w,
                Curve(linear.G * exposure) / w,
                Curve(linear.B * exposure) / w, 1f);
        }

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
                // Godot's shadow_opacity (1 = fully dark) maps to the contract's shadow strength.
                ShadowStrength = light.ShadowOpacity,
                // Point/spot need range + cone. Godot's SpotAngle is the HALF-angle (axis→edge); the
                // contract/shader use the FULL cone angle, so double it.
                Range = light switch
                {
                    OmniLight3D omni => omni.OmniRange,
                    SpotLight3D spot => spot.SpotRange,
                    _ => 0f,
                },
                SpotAngle = light is SpotLight3D s ? s.SpotAngle * 2f : 0f,
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

        // Internal: the plugin's Play .NET button derives the exported JSON path from the same
        // name rule without re-exporting.
        internal static string ResolveSceneName(Node root)
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

        private static LevelEntityData ExportEntity(EntityExport entity, MaterialExporter materials, PrefabExporter prefabs, ExportPaths paths)
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
                Components = BuildComponents(entity, paths),
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

        // Resolve the entity's SOURCE mesh GLB to a data/-relative contract field. Prefers the
        // authored ModelPath; otherwise the nearest instanced model child (a node whose
        // SceneFilePath is a .glb/.gltf under data/). A .tscn/.scn ModelPath is a prefab hint, not
        // a mesh, so it is ignored here (the caller keeps the ModelPath-hint Renderable branch).
        // Returns null when no GLB is found or it resolves OUTSIDE data/ (unreachable at runtime).
        private static string? ResolveMeshField(EntityExport entity, ExportPaths paths)
        {
            if (IsGlbPath(entity.ModelPath))
            {
                return WarnIfUnreachable(paths.DataRelativeMeshField(entity.ModelPath), entity, entity.ModelPath);
            }

            foreach (Node descendant in ModelDescendants(entity))
            {
                if (IsGlbPath(descendant.SceneFilePath))
                {
                    return WarnIfUnreachable(paths.DataRelativeMeshField(descendant.SceneFilePath), entity, descendant.SceneFilePath);
                }
            }

            return null;
        }

        // Descendants of the entity, NOT descending into a nested EntityExport (that child owns
        // its own model), so a parent never claims a child entity's instanced GLB.
        private static IEnumerable<Node> ModelDescendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is EntityExport)
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

        private static bool IsGlbPath(string? path) =>
            !string.IsNullOrEmpty(path) &&
            (path.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".gltf", System.StringComparison.OrdinalIgnoreCase));

        private static string? WarnIfUnreachable(string? field, EntityExport entity, string source)
        {
            if (field is null)
            {
                GD.PushWarning(
                    $"[ParadiseExport] Entity '{entity.Name}' references model '{source}' outside res://data/ — " +
                    "the runtime resolves meshes under data/, so it will not render. Move the asset under data/.");
            }

            return field;
        }

        private static EntityComponentsData BuildComponents(EntityExport entity, ExportPaths paths)
        {
            var components = new EntityComponentsData();
            // Schema v2 (source-GLB pipeline): Renderable.Mesh REFERENCES the entity's source GLB
            // under data/ (no per-entity bake). The runtime resolves it as data/<field> and reads
            // the shared, KTX2-converted GLB — the same file the Godot editor renders.
            string? meshField = ResolveMeshField(entity, paths);
            if (meshField is not null)
            {
                components.Renderable = new RenderableComponentData { Mesh = meshField };
            }
            else if (!string.IsNullOrEmpty(entity.ModelPath))
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
                    Acceleration = entity.ResolvedAcceleration,
                    IdleClip = entity.ResolvedIdleAnimation,
                    WalkClip = entity.ResolvedWalkAnimation,
                };
            }

            return components;
        }

        // No RigidBody3D detection (EntityExport is a plain Node3D): the authored IsDynamicBody
        // flag marks dynamic bodies (balls), an agent is kinematic, anything else static.
        private static RigidbodyComponentData BuildRigidbody(EntityExport entity) => new()
        {
            BodyType = entity.IsDynamicBody
                ? PhysicsBodyType.Dynamic
                : entity.IsAgent ? PhysicsBodyType.Kinematic : PhysicsBodyType.Static,
            Mass = entity.IsDynamicBody ? entity.BodyMass : 0f,
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
            data.Layer = ResolveLayerIndex(collider);
            data.LayerName = "";
            data.LocalCenter = ToSN(rootLocal.Origin);
            data.LocalRotation = ToSN(rootLocal.Basis.GetRotationQuaternion());
            return true;
        }

        // Godot stores collision layers as a bitmask on the owning body; the engine-neutral
        // contract carries a Unity-style single layer INDEX (consumers do 1u << Layer — see
        // ParadiseRuntime.SceneAssembler.AppendCollider). Map the nearest CollisionObject3D
        // ancestor's mask to the index of its lowest set bit; an unlayered body maps to 0.
        // (Godot's default collision_layer is 1 → index 0; obstacle mask 2 → index 1.)
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
                            $"[ParadiseExport] Body '{body.GetPath()}' is on multiple collision layers " +
                            $"(mask {mask}); the export contract keeps only the lowest (index {CollisionLayerContract.MaskToLayerIndex(mask)}).");
                    }

                    return CollisionLayerContract.MaskToLayerIndex(mask);
                }
            }

            return 0;
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
