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

        private Button? _playDotnetButton;

        public override void _EnterTree()
        {
            AddToolMenuItem(ExportMenuItem, Callable.From(OnExportActiveScene));
            AddToolMenuItem(GeneratePrefabsMenuItem, Callable.From(OnGenerateModelPrefabs));
            AddToolMenuItem(ConvertModelsMenuItem, Callable.From(OnConvertModels));
            _playDotnetButton = new Button
            {
                Text = "Play .NET",
                TooltipText = "Launch the active scene's exported data in the standalone .NET runtime (ParadiseRuntime: SDL window, engine PBR renderer, real simulation). Uses the existing data/ export — save the scene to refresh it.",
                Flat = true,
            };
            _playDotnetButton.Pressed += OnPlayDotnet;
            AddControlToContainer(CustomControlContainer.Toolbar, _playDotnetButton);
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
            if (_playDotnetButton is not null)
            {
                RemoveControlFromContainer(CustomControlContainer.Toolbar, _playDotnetButton);
                _playDotnetButton.QueueFree();
                _playDotnetButton = null;
            }
        }

        /// <summary>Toolbar "Play .NET": launch the ALREADY-exported scene data detached in the
        /// standalone runtime via `dotnet run` (builds on demand — the first launch after a code
        /// change takes a few seconds before the window appears). Deliberately does NOT export:
        /// data/ is authoring output, kept fresh by the save hook / Paradise menu — launching is
        /// a pure consumer of it.</summary>
        private void OnPlayDotnet()
        {
            try
            {
                Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root is null)
                {
                    GD.PushWarning("[ParadiseExport] No edited scene to play.");
                    return;
                }

                string sceneName = Export.SceneDataExporter.ResolveSceneName(root);
                string sceneJson = new ParadiseExport.Paths.ExportPaths(ProjectSettings.GlobalizePath("res://data"))
                    .GetLevelDataOutputPath(sceneName);
                if (!System.IO.File.Exists(sceneJson))
                {
                    GD.PushError(
                        $"[ParadiseExport] '{sceneJson}' does not exist — save the scene (auto-export) " +
                        "or run Project > Tools > Paradise/Export Active Scene first.");
                    return;
                }

                string runtimeProject = System.IO.Path.Combine(
                    ProjectSettings.GlobalizePath("res://"), "ParadiseRuntime");
                string dotnet = ResolveDotnetPath();
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paradise_play_dotnet.log");

                long pid;
                if (System.OperatingSystem.IsWindows())
                {
                    pid = OS.CreateProcess(dotnet, ["run", "--project", runtimeProject, "--", "--scene", sceneJson]);
                }
                else
                {
                    // Shell wrapper for two GUI-launch realities: OS.CreateProcess drops the
                    // child's output (build errors would vanish — log them to a file instead),
                    // and the editor's PATH lacks the dotnet directory, which build targets
                    // invoking `dotnet` (child processes) need.
                    string dotnetDir = System.IO.Path.GetDirectoryName(dotnet) ?? "/usr/local/share/dotnet";
                    string command =
                        $"export PATH=\"{dotnetDir}:$PATH\"; " +
                        $"exec \"{dotnet}\" run --project \"{runtimeProject}\" -- --scene \"{sceneJson}\" > \"{logPath}\" 2>&1";
                    pid = OS.CreateProcess("/bin/sh", ["-c", command]);
                }

                if (pid <= 0)
                {
                    GD.PushError($"[ParadiseExport] Failed to launch '{dotnet}' — is the .NET SDK installed?");
                    return;
                }

                GD.Print($"[ParadiseExport] Launched .NET runtime (pid {pid}): {sceneJson} — output: {logPath}");
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[ParadiseExport] Play .NET failed: {ex.Message}");
            }
        }

        private static string ResolveDotnetPath()
        {
            // A GUI-launched editor doesn't inherit the shell PATH (notably on macOS), so probe
            // the standard SDK locations before falling back to PATH resolution.
            foreach (string candidate in new[]
            {
                "/usr/local/share/dotnet/dotnet", // macOS official installer
                "/usr/local/bin/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/bin/dotnet",                // Linux distro packages
                "/usr/share/dotnet/dotnet",
            })
            {
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return "dotnet";
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
