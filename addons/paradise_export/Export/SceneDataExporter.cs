#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using ParadiseExport.Core.Data;
using ParadiseExport.Core.Geometry;
using ParadiseExport.Core.Paths;
using ParadiseExport.Core.Serialization;
using SN = System.Numerics;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Phase 1 vertical slice: walks the edited Godot scene, exports the camera + lights into an
    /// engine-neutral <see cref="LevelData"/> via the Core library, and writes it to
    /// <c>data/scenes/&lt;Scene&gt;.json</c>. Godot's right-handed transforms are converted to the
    /// contract's left-handed convention through <see cref="CoordinateConversion"/>.
    ///
    /// Scope is intentionally minimal (camera + lights). Entities, materials, colliders, navmesh,
    /// and full lighting/environment fidelity arrive in later phases — see MIGRATION.md.
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
    }
}
#endif
