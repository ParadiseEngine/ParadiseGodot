#if TOOLS
using Godot;
using ParadiseExport.Core;

namespace ParadiseGodot
{
    /// <summary>
    /// Phase 0 editor plugin scaffold. Registers a Project &gt; Tools menu item and confirms the
    /// engine-neutral <c>ParadiseExport.Core</c> library is wired in. Export logic arrives in
    /// later phases — see MIGRATION.md.
    /// </summary>
    [Tool]
    public partial class ParadiseExportPlugin : EditorPlugin
    {
        private const string ExportMenuItem = "Paradise/Export Active Scene";
        private const string GeneratePrefabsMenuItem = "Paradise/Generate Model Prefabs";

        public override void _EnterTree()
        {
            AddToolMenuItem(ExportMenuItem, Callable.From(OnExportActiveScene));
            AddToolMenuItem(GeneratePrefabsMenuItem, Callable.From(OnGenerateModelPrefabs));
            GD.Print($"[ParadiseExport] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");
        }

        public override void _ExitTree()
        {
            RemoveToolMenuItem(ExportMenuItem);
            RemoveToolMenuItem(GeneratePrefabsMenuItem);
        }

        private void OnGenerateModelPrefabs()
        {
            Pipeline.ModelPrefabGenerator.GenerateAll();
        }

        private void OnExportActiveScene()
        {
            Export.SceneDataExporter.ExportEditedScene(EditorInterface.Singleton);
        }
    }
}
#endif
