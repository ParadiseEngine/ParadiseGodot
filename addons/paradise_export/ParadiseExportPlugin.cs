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
        private const string GeneratePrimitivesMenuItem = "Paradise/Generate Primitive GLBs";
        private const string ConvertModelsMenuItem = "Paradise/Convert Models (FBX→GLB→KTX2)";
        private const string ConvertDataGlbsMenuItem = "Paradise/Convert data GLBs → KTX2";
        private const string SettingsMenuItem = "Paradise/Settings…";

        private Button? _playDotnetButton;
        private ParadiseSettingsDialog? _settingsDialog;
        private readonly Pipeline.DataGlbImportHook _dataGlbHook = new();

        public override void _EnterTree()
        {
            // Saved tool paths (toktx/Blender) take effect for the whole session — including
            // headless exports — before anything can invoke the pipeline.
            ParadiseSettingsDialog.ApplySavedSettings();

            AddToolMenuItem(ExportMenuItem, Callable.From(OnExportActiveScene));
            AddToolMenuItem(GeneratePrefabsMenuItem, Callable.From(OnGenerateModelPrefabs));
            AddToolMenuItem(GeneratePrimitivesMenuItem, Callable.From(OnGeneratePrimitives));
            AddToolMenuItem(ConvertModelsMenuItem, Callable.From(OnConvertModels));
            AddToolMenuItem(ConvertDataGlbsMenuItem, Callable.From(OnConvertDataGlbs));
            AddToolMenuItem(SettingsMenuItem, Callable.From(OnOpenSettings));
            // Auto-transcode textures of any GLB (re)imported under res://data/ to KTX2, so a model
            // dropped into data/ is runtime-ready with no manual step.
            EditorInterface.Singleton.GetResourceFilesystem().ResourcesReimported += _dataGlbHook.OnResourcesReimported;
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

            // Headless/CI hook: run one or more migration tasks then quit. Any combination of:
            //   PARADISE_GENERATE_PRIMITIVES=1   generate data/primitives/*.glb
            //   PARADISE_CONVERT_DATA_GLBS=1     transcode data/ GLB textures → KTX2 in place
            //   PARADISE_EXPORT_SCENE=res://...   export that scene's data/ contract
            // e.g. godot --headless --editor --path . — tasks run in the above order, then quit.
            if (OS.GetEnvironment("PARADISE_GENERATE_PRIMITIVES") == "1" ||
                OS.GetEnvironment("PARADISE_CONVERT_DATA_GLBS") == "1" ||
                !string.IsNullOrEmpty(OS.GetEnvironment("PARADISE_EXPORT_SCENE")))
            {
                Callable.From(RunHeadlessTasks).CallDeferred();
            }
        }

        public override void _ExitTree()
        {
            RemoveToolMenuItem(ExportMenuItem);
            RemoveToolMenuItem(GeneratePrefabsMenuItem);
            RemoveToolMenuItem(GeneratePrimitivesMenuItem);
            RemoveToolMenuItem(ConvertModelsMenuItem);
            RemoveToolMenuItem(ConvertDataGlbsMenuItem);
            RemoveToolMenuItem(SettingsMenuItem);
            SceneSaved -= OnSceneSaved;
            EditorInterface.Singleton.GetResourceFilesystem().ResourcesReimported -= _dataGlbHook.OnResourcesReimported;
            if (_settingsDialog is not null)
            {
                _settingsDialog.QueueFree();
                _settingsDialog = null;
            }
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
                // User-configured runtime arguments (Paradise/Settings…, default --imgui).
                string[] extraArgs = ParadiseSettingsDialog.PlayDotnetArguments();

                long pid;
                if (System.OperatingSystem.IsWindows())
                {
                    pid = OS.CreateProcess(dotnet, ["run", "--project", runtimeProject, "--", "--scene", sceneJson, .. extraArgs]);
                }
                else
                {
                    // Shell wrapper for two GUI-launch realities: OS.CreateProcess drops the
                    // child's output (build errors would vanish — log them to a file instead),
                    // and the editor's PATH lacks the dotnet directory, which build targets
                    // invoking `dotnet` (child processes) need.
                    string dotnetDir = System.IO.Path.GetDirectoryName(dotnet) ?? "/usr/local/share/dotnet";
                    string extra = string.Concat(
                        System.Linq.Enumerable.Select(extraArgs, a => $" {ShellQuote(a)}"));
                    string command =
                        $"export PATH=\"{dotnetDir}:$PATH\"; " +
                        $"exec \"{dotnet}\" run --project \"{runtimeProject}\" -- --scene \"{sceneJson}\"{extra} > \"{logPath}\" 2>&1";
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

        // POSIX single-quote wrapping: every token becomes one word verbatim, whatever it
        // contains ('...' with embedded quotes spliced as '\'' ).
        private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

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

        private void OnGeneratePrimitives()
        {
            Pipeline.PrimitiveGlbGenerator.GenerateAll();
        }

        private void OnConvertModels()
        {
            Pipeline.AssetPipeline.ConvertAllModels();
        }

        private void OnConvertDataGlbs()
        {
            Pipeline.DataGlbConverter.ConvertAll();
        }

        // Headless orchestrator: run whichever migration tasks the env selects, in a fixed order
        // (generate primitives → convert data GLBs → export scene), then quit with a combined code.
        private void RunHeadlessTasks()
        {
            int exitCode = 0;
            try
            {
                if (OS.GetEnvironment("PARADISE_GENERATE_PRIMITIVES") == "1")
                {
                    Pipeline.PrimitiveGlbGenerator.GenerateAll();
                }

                if (OS.GetEnvironment("PARADISE_CONVERT_DATA_GLBS") == "1")
                {
                    Pipeline.DataGlbConverter.ConvertAll();
                }

                string scenePath = OS.GetEnvironment("PARADISE_EXPORT_SCENE");
                if (!string.IsNullOrEmpty(scenePath) && !RunHeadlessExport(scenePath))
                {
                    exitCode = 1;
                }
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[ParadiseExport] Headless task failed: {ex}");
                exitCode = 1;
            }

            GetTree().Quit(exitCode);
        }

        private bool RunHeadlessExport(string scenePath)
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
                return output is not null;
            }
            finally
            {
                RemoveChild(root);
                root.QueueFree();
            }
        }

        private void OnExportActiveScene()
        {
            Export.SceneDataExporter.ExportEditedScene(EditorInterface.Singleton);
        }

        private void OnOpenSettings()
        {
            if (_settingsDialog is null)
            {
                _settingsDialog = new ParadiseSettingsDialog();
                EditorInterface.Singleton.GetBaseControl().AddChild(_settingsDialog);
            }

            _settingsDialog.PopupCentered();
        }
    }
}
#endif
