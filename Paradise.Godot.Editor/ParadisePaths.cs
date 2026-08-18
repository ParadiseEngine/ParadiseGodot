#if TOOLS
using Godot;

namespace ParadiseGodot
{
    /// <summary>Project-level path configuration for the Paradise addon. The export data
    /// directory is a ProjectSettings key (committed in project.godot) so each project chooses
    /// where the engine-neutral contract lives; "res://data" is the convention and the default.
    /// All addon code resolves data paths through here — never hardcode "res://data".</summary>
    public static class ParadisePaths
    {
        public const string DataDirSetting = "paradise/export/data_dir";
        public const string DefaultDataDir = "res://data";

        /// <summary>The configured data directory as a res:// path, without a trailing slash.</summary>
        public static string DataDir
        {
            get
            {
                string value = ProjectSettings.HasSetting(DataDirSetting)
                    ? ProjectSettings.GetSetting(DataDirSetting).AsString().Trim()
                    : "";
                if (value.Length == 0)
                {
                    return DefaultDataDir;
                }
                return value.TrimEnd('/');
            }
        }

        /// <summary>The data directory prefix for res:// path checks ("res://data/").</summary>
        public static string DataDirPrefix => DataDir + "/";

        /// <summary>The configured data directory as an absolute filesystem path.</summary>
        public static string DataDirGlobal => ProjectSettings.GlobalizePath(DataDir);

        public static string SpritesDir => DataDir + "/sprites";
        public static string PrimitivesDir => DataDir + "/primitives";

        /// <summary>Engine-neutral export paths rooted at the configured data directory.</summary>
        public static Paradise.Export.Paths.ExportPaths ExportPaths()
            => new(DataDirGlobal);
    }
}
#endif
