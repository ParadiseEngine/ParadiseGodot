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

        public override void _EnterTree()
        {
            AddToolMenuItem(ExportMenuItem, Callable.From(OnExportActiveScene));
            GD.Print($"[ParadiseExport] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");
        }

        public override void _ExitTree()
        {
            RemoveToolMenuItem(ExportMenuItem);
        }

        private void OnExportActiveScene()
        {
            GD.Print("[ParadiseExport] Export Active Scene — not yet implemented (Phase 1).");
        }
    }
}
#endif
