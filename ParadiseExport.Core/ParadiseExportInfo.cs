#nullable enable
using DotRecast.Detour.Io;
using Newtonsoft.Json;

namespace ParadiseExport.Core
{
    /// <summary>
    /// Phase 0 smoke surface. Proves the engine-neutral Core library builds and that its
    /// Newtonsoft + DotRecast dependencies resolve and are usable. The real exporters
    /// (LevelDocument, ExportJsonWriter, the DotRecast writer, the Blender/toktx pipeline)
    /// land in later phases — see MIGRATION.md.
    /// </summary>
    public static class ParadiseExportInfo
    {
        public const string Version = "0.1.0";

        public static string Describe()
        {
            var info = new
            {
                tool = "ParadiseExport.Core",
                version = Version,
                newtonsoft = typeof(JsonConvert).Assembly.GetName().Version?.ToString(),
                dotRecast = typeof(DtMeshSetWriter).Assembly.GetName().Version?.ToString(),
            };
            return JsonConvert.SerializeObject(info);
        }
    }
}
