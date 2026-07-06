#if TOOLS
using Godot;
using ParadiseExport;

namespace ParadiseGodot
{
    /// <summary>
    /// Phase 0 editor plugin scaffold. Registers a Project &gt; Tools menu item and confirms the
    /// engine-neutral <c>ParadiseExport</c> library is wired in. Export logic arrives in
    /// later phases — see MIGRATION.md.
    /// </summary>
    [Tool]
    public partial class ParadiseExportPlugin : EditorPlugin
    {
        private const string ExportMenuItem = "Paradise/Export Active Scene";
        private const string GeneratePrefabsMenuItem = "Paradise/Generate Model Prefabs";
        private const string ConvertModelsMenuItem = "Paradise/Convert Models (FBX→GLB→KTX2)";

        public override void _EnterTree()
        {
            AddToolMenuItem(ExportMenuItem, Callable.From(OnExportActiveScene));
            AddToolMenuItem(GeneratePrefabsMenuItem, Callable.From(OnGenerateModelPrefabs));
            AddToolMenuItem(ConvertModelsMenuItem, Callable.From(OnConvertModels));
            // Automation: re-export scene data whenever the edited scene is saved.
            SceneSaved += OnSceneSaved;
            GD.Print($"[ParadiseExport] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");

            // Headless/CI regeneration hook: PARADISE_EXPORT_SCENE=res://scenes/sample.tscn
            // godot --headless --editor --path . — exports the scene and quits the editor.
            string headlessScene = OS.GetEnvironment("PARADISE_EXPORT_SCENE");
            if (!string.IsNullOrEmpty(headlessScene))
            {
                Callable.From(() => RunHeadlessExport(headlessScene)).CallDeferred();
            }
        }

        public override void _ExitTree()
        {
            RemoveToolMenuItem(ExportMenuItem);
            RemoveToolMenuItem(GeneratePrefabsMenuItem);
            RemoveToolMenuItem(ConvertModelsMenuItem);
            SceneSaved -= OnSceneSaved;
        }

        private void OnSceneSaved(string filePath)
        {
            // In Godot 4, SceneSaved fires for the current root scene, so re-exporting the active
            // edited scene targets the just-saved scene. filePath is unused today (kept in the
            // signature for future resilience if sub-scene saves ever emit independently).
            try
            {
                Export.SceneDataExporter.ExportEditedScene(EditorInterface.Singleton);
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[ParadiseExport] Auto re-export on save failed: {ex.Message}");
            }
        }

        private void OnGenerateModelPrefabs()
        {
            Pipeline.ModelPrefabGenerator.GenerateAll();
        }

        private void OnConvertModels()
        {
            Pipeline.AssetPipeline.ConvertAllModels();
        }

        private void RunHeadlessExport(string scenePath)
        {
            int exitCode = 0;
            try
            {
                var packed = GD.Load<PackedScene>(scenePath);
                Node root = packed.Instantiate();
                // Exporters read GlobalTransform, which requires tree membership — parent the
                // instance under the plugin for the duration of the export.
                AddChild(root);
                try
                {
                    string? output = Export.SceneDataExporter.ExportRoot(root);
                    GD.Print($"[ParadiseExport] Headless export {(output is null ? "produced no output" : $"wrote {output}")}.");
                    if (output is null) exitCode = 1;
                }
                finally
                {
                    RemoveChild(root);
                    root.QueueFree();
                }
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[ParadiseExport] Headless export failed: {ex}");
                exitCode = 1;
            }

            GetTree().Quit(exitCode);
        }

        private void OnExportActiveScene()
        {
            Export.SceneDataExporter.ExportEditedScene(EditorInterface.Singleton);
        }
    }
}
#endif
