#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using ParadiseExport.Data;
using ParadiseExport.Paths;
using ParadiseExport.Serialization;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Exports engine-neutral project settings (physics collision matrix + render settings) to
    /// <c>data/ProjectSettings.json</c>, mirroring the Unity tool.
    ///
    /// Layer policy (resolves the migration's open question): Godot's collision_layer/collision_mask
    /// are 32-bit (parity with Unity's 32 layers), but Godot has <b>no global layer-vs-layer
    /// collision matrix</b> — collisions are decided per body. We therefore emit a permissive matrix
    /// (every layer collides with every layer), which is both the honest mapping and identical to
    /// Unity's default. Visual/render layers differ (Godot exposes 20 vs Unity 32); light cull masks
    /// are not part of project settings and are handled per-light, where bits ≥20 are dropped.
    /// </summary>
    internal static class ProjectSettingsExporter
    {
        public static void Export(ExportPaths paths)
        {
            var settings = new ProjectSettingsData
            {
                Physics = new PhysicsSettingsData
                {
                    CollisionMatrix = new PhysicsCollisionMatrixData { LayerMasks = PermissiveCollisionMatrix() },
                },
                Rendering = ReadRenderSettings(),
            };

            string outputPath = paths.GetProjectSettingsOutputPath();
            ExportJsonWriter.WriteJsonDocument(outputPath, settings);
            GD.Print($"[ParadiseExport] Exported project settings: {outputPath}");
        }

        // 32 layers, each colliding with every layer (-1 = all bits set).
        private static List<int> PermissiveCollisionMatrix() =>
            Enumerable.Repeat(-1, 32).ToList();

        private static RenderSettingsData ReadRenderSettings()
        {
            var rendering = new RenderSettingsData
            {
                RenderScale = (float)GetDouble("rendering/scaling_3d/scale", 1.0),
                MsaaSamples = MsaaSampleCount(GetInt("rendering/anti_aliasing/quality/msaa_3d", 0)),
                // Godot anisotropic filtering is an enum (0 = disabled); the contract wants 1 = off
                // else up to 16. ValidateAndNormalize clamps to [1, 16].
                AnisotropicLevel = GetInt("rendering/textures/default_filters/anisotropic_filtering_level", 2) > 0 ? 16 : 1,
            };
            rendering.ValidateAndNormalize();
            return rendering;
        }

        private static int MsaaSampleCount(int godotMsaa) => godotMsaa switch
        {
            0 => 1, // disabled
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 1,
        };

        private static double GetDouble(string name, double fallback) =>
            ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).AsDouble() : fallback;

        private static int GetInt(string name, int fallback) =>
            ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).AsInt32() : fallback;
    }
}
#endif
