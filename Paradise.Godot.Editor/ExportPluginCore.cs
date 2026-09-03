#if TOOLS
using Godot;
using Paradise.Export;

namespace ParadiseGodot
{
    /// <summary>
    /// Phase 0 editor plugin scaffold. Registers a Project &gt; Tools menu item and confirms the
    /// engine-neutral <c>Paradise.Export</c> library is wired in. Export logic arrives in
    /// later phases — see MIGRATION.md.
    /// </summary>
    /// <remarks>
    /// A plain class, not an EditorPlugin. The res:// <c>ParadiseExportPlugin</c> is the plugin and
    /// forwards its lifecycle here. A res:// script may not derive from a GodotObject-derived type
    /// in another assembly - Godot registers the base as a script type as well and its
    /// ScriptTypeBiMap throws a duplicate-key exception on every assembly reload, breaking editor
    /// hot-reload. See godotengine/godot#75352.
    /// </remarks>
    public sealed class ExportPluginCore
    {
        /// <summary>The plugin this core drives; every editor call goes through it.</summary>
        private readonly EditorPlugin _host;

        public ExportPluginCore(EditorPlugin host) => _host = host;

        // Scene-root metadata naming a code-driven runtime sample (`--game <name>`) for the "Play .NET"
        // button — set on scenes that spawn their world in a bridge script rather than AuthoredEntityNode nodes.
        private const string GameMetaKey = "paradise_game";

        private const string GeneratePrefabsMenuItem = "Paradise/Generate Model Prefabs";
        private const string GeneratePrimitivesMenuItem = "Paradise/Generate Primitive GLBs";
        private const string ConvertModelsMenuItem = "Paradise/Convert Models (FBX→GLB→KTX2)";
        private const string ConvertDataGlbsMenuItem = "Paradise/Convert data GLBs → KTX2";
        private const string ProjectSetupMenuItem = "Paradise/Project Setup";
        private const string SettingsMenuItem = "Paradise/Settings…";

        private Button? _playDotnetButton;
        /// <summary>
        /// Methods the res:// plugin script must forward to this core, BY THESE NAMES.
        ///
        /// Editor UI is wired with name-based callables — new Callable(_host, name) — never with
        /// Callable.From(delegate). A delegate-backed callable is a ManagedCallableMiddleman
        /// holding a GC handle into the CURRENT assembly; a .NET assembly reload frees that handle,
        /// and any UI that survives the reload then fails its clicks with
        /// "Parameter delegate_handle.value is null … ManagedCallableMiddleman:: Method not found".
        /// A name-based callable re-resolves against whatever assembly is loaded when it is
        /// invoked, so it survives every reload — and this editor now reloads often: the payload
        /// materializer, the schema auto-dump and hammer builds all trigger it.
        /// </summary>
        private static readonly string[] ForwardedMethods =
        [
            "OnGenerateModelPrefabs",
            "OnGeneratePrimitives",
            "OnConvertModels",
            "OnConvertDataGlbs",
            "OnProjectSetup",
            "OnOpenSettings",
            "OnPlayDotnet",
        ];

        private ParadiseSettingsDialog? _settingsDialog;
        private readonly Pipeline.DataGlbImportHook _dataGlbHook = new();

        public void EnterTree()
        {
            // Saved tool paths (toktx/Blender) take effect for the whole session — including
            // headless exports — before anything can invoke the pipeline.
            ParadiseSettingsDialog.ApplySavedSettings();

            // A payload shim from before these forwarders would leave every menu item dead with
            // an unhelpful native error; say what is actually wrong instead.
            foreach (var method in ForwardedMethods)
            {
                if (!_host.HasMethod(method))
                {
                    GD.PushError(
                        $"[Paradise.Export] The res:// plugin script has no '{method}' forwarder — " +
                        "addons/paradise is older than the addon assembly. Rebuild the C# project " +
                        "so the payload materializer updates it, then reload the project.");
                }
            }

            _host.AddToolMenuItem(GeneratePrefabsMenuItem, new Callable(_host, "OnGenerateModelPrefabs"));
            _host.AddToolMenuItem(GeneratePrimitivesMenuItem, new Callable(_host, "OnGeneratePrimitives"));
            _host.AddToolMenuItem(ConvertModelsMenuItem, new Callable(_host, "OnConvertModels"));
            _host.AddToolMenuItem(ConvertDataGlbsMenuItem, new Callable(_host, "OnConvertDataGlbs"));
            _host.AddToolMenuItem(ProjectSetupMenuItem, new Callable(_host, "OnProjectSetup"));
            _host.AddToolMenuItem(SettingsMenuItem, new Callable(_host, "OnOpenSettings"));
            // Auto-transcode textures of any GLB (re)imported under res://data/ to KTX2, so a model
            // dropped into data/ is runtime-ready with no manual step.
            EditorInterface.Singleton.GetResourceFilesystem().ResourcesReimported += _dataGlbHook.OnResourcesReimported;
            _playDotnetButton = new Button
            {
                Text = "Play .NET",
                TooltipText = "Launch the active scene in the standalone .NET runtime host (SDL window, engine PBR renderer, real simulation). Needs a scene the runtime can load: mark the root with 'paradise_game' metadata for a runtime sample, or build the asset project with `paradise assets build`. Host resolution: Settings… > runtime host, else this project's Paradise.Sample.Runtime, else the installed paradise-runtime dotnet tool.",
                Flat = true,
            };
            _playDotnetButton.Connect(BaseButton.SignalName.Pressed, new Callable(_host, "OnPlayDotnet"));
            _host.AddControlToContainer(EditorPlugin.CustomControlContainer.Toolbar, _playDotnetButton);
            GD.Print($"[Paradise.Export] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");
            ProjectSetup.CheckExportVersion();

            // Headless/CI hook: run one or more migration tasks then quit. Any combination of:
            //   PARADISE_GENERATE_PRIMITIVES=1   generate data/primitives/*.glb
            //   PARADISE_GENERATE_MODEL_PREFABS=1 generate a prefab per model under data/
            //   PARADISE_CONVERT_DATA_GLBS=1     transcode data/ GLB textures → KTX2 in place
            // e.g. godot --headless --editor --path . — tasks run in the above order, then quit.
            if (OS.GetEnvironment("PARADISE_GENERATE_PRIMITIVES") == "1" ||
                OS.GetEnvironment("PARADISE_GENERATE_MODEL_PREFABS") == "1" ||
                OS.GetEnvironment("PARADISE_CONVERT_DATA_GLBS") == "1")
            {
                Callable.From(RunHeadlessTasks).CallDeferred();
            }
        }

        public void ExitTree()
        {
            // Button first: if any teardown below throws, a leftover toolbar button whose pressed
            // connection points into an unloaded assembly is the failure users actually see.
            if (_playDotnetButton is not null)
            {
                _host.RemoveControlFromContainer(EditorPlugin.CustomControlContainer.Toolbar, _playDotnetButton);
                _playDotnetButton.QueueFree();
                _playDotnetButton = null;
            }
            _host.RemoveToolMenuItem(GeneratePrefabsMenuItem);
            _host.RemoveToolMenuItem(GeneratePrimitivesMenuItem);
            _host.RemoveToolMenuItem(ConvertModelsMenuItem);
            _host.RemoveToolMenuItem(ConvertDataGlbsMenuItem);
            _host.RemoveToolMenuItem(ProjectSetupMenuItem);
            _host.RemoveToolMenuItem(SettingsMenuItem);
            EditorInterface.Singleton.GetResourceFilesystem().ResourcesReimported -= _dataGlbHook.OnResourcesReimported;
            if (_settingsDialog is not null)
            {
                _settingsDialog.QueueFree();
                _settingsDialog = null;
            }
        }

        /// <summary>Toolbar "Play .NET": launch the ALREADY-exported scene data detached in the
        /// standalone runtime via `dotnet run` (builds on demand — the first launch after a code
        /// change takes a few seconds before the window appears). Deliberately does NOT export:
        /// data/ is authoring output, kept fresh by the save hook / Paradise menu — launching is
        /// a pure consumer of it.</summary>
        public void OnPlayDotnet()
        {
            try
            {
                Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root is null)
                {
                    GD.PushWarning("[Paradise.Export] No edited scene to play.");
                    return;
                }

                string[]? host = ResolveRuntimeHostCommand();
                if (host is null)
                {
                    GD.PushError(
                        "[Paradise.Export] No runtime host found. Set one in Paradise/Settings… " +
                        "(a paradise-runtime executable or a host .csproj), or install the tool: " +
                        "`dotnet tool install --global paradise-runtime`.");
                    return;
                }

                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paradise_play_dotnet.log");
                // User-configured runtime arguments (Paradise/Settings…, default --imgui).
                string[] extraArgs = ParadiseSettingsDialog.PlayDotnetArguments();

                // A scene root may declare a code-driven runtime SAMPLE via the `paradise_game` metadata
                // (e.g. Odyssey): those have no AuthoredEntityNodeBase nodes, so a --scene launch would render an
                // empty world. The SAME button reads the metadata and launches the runtime's built-in
                // sample (`--game <name>`); every other scene falls through to the data-export path — one
                // launch flow, the scene's own metadata picks the mode (mirrors `paradise_entity_guid`).
                string[] argv;
                string launchLabel;
                string game = root.HasMeta(GameMetaKey) ? root.GetMeta(GameMetaKey).AsString() : "";
                if (!string.IsNullOrEmpty(game))
                {
                    argv = [.. host[1..], "--game", game, .. extraArgs];
                    launchLabel = $"--game {game}";
                }
                else
                {
                    // Scene export left this addon with contract v6: assets/ is the source of
                    // truth and `paradise assets build` writes the tree a runtime plays, so there
                    // is no longer a data/ document for this scene to point at. Pointing Play at
                    // the project's .editor/play/ tree is the step that restores this; until then
                    // a scene without paradise_game metadata has nothing to launch, and saying so
                    // beats launching the runtime against a stale document from the old exporter.
                    GD.PushError(
                        "[Paradise] Play needs a built scene, and this addon no longer exports one. " +
                        "Run `paradise assets build` for this project, or mark the scene root with " +
                        $"the '{GameMetaKey}' metadata to launch a runtime sample instead.");
                    return;
                }

                long pid;
                if (System.OperatingSystem.IsWindows())
                {
                    pid = OS.CreateProcess(host[0], argv);
                }
                else
                {
                    // Shell wrapper for two GUI-launch realities: OS.CreateProcess drops the
                    // child's output (build errors would vanish — log them to a file instead),
                    // and the editor's PATH lacks the dotnet directory, which build targets
                    // invoking `dotnet` (child processes) need.
                    string dotnetDir = System.IO.Path.GetDirectoryName(ResolveDotnetPath()) ?? "/usr/local/share/dotnet";
                    string args = string.Concat(System.Linq.Enumerable.Select(argv, a => $" {ShellQuote(a)}"));
                    string command =
                        $"export PATH=\"{dotnetDir}:$PATH\"; " +
                        $"exec {ShellQuote(host[0])}{args} > \"{logPath}\" 2>&1";
                    pid = OS.CreateProcess("/bin/sh", ["-c", command]);
                }

                if (pid <= 0)
                {
                    GD.PushError($"[Paradise.Export] Failed to launch '{host[0]}' — is the .NET SDK installed?");
                    return;
                }

                GD.Print($"[Paradise.Export] Launched .NET runtime (pid {pid}): {launchLabel} — output: {logPath}");
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[Paradise.Export] Play .NET failed: {ex.Message}");
            }
        }

        // POSIX single-quote wrapping: every token becomes one word verbatim, whatever it
        // contains ('...' with embedded quotes spliced as '\'' ).
        private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

        /// <summary>Resolve the runtime host as an argv prefix (element 0 = executable). Order:
        /// the Paradise/Settings… "runtime host" path (a .csproj means `dotnet run --project`;
        /// machine-level EditorSettings override first, then the committed project setting),
        /// then this project's own Paradise.Sample.Runtime (the dev-workbench case), then the
        /// globally installed `paradise-runtime` dotnet tool. Null when nothing is found.</summary>
        internal static string[]? ResolveRuntimeHostCommand()
        {
            string configured = ResolveHostPath(ParadiseSettingsDialog.RuntimeHostPath());
            if (configured.Length > 0)
            {
                return configured.EndsWith(".csproj", System.StringComparison.OrdinalIgnoreCase)
                    ? [ResolveDotnetPath(), "run", "--project", configured, "--"]
                    : [configured];
            }

            string sampleProject = System.IO.Path.Combine(
                ProjectSettings.GlobalizePath("res://"), "Paradise.Sample.Runtime", "Paradise.Sample.Runtime.csproj");
            if (System.IO.File.Exists(sampleProject))
            {
                return [ResolveDotnetPath(), "run", "--project", sampleProject, "--"];
            }

            string toolName = System.OperatingSystem.IsWindows() ? "paradise-runtime.exe" : "paradise-runtime";
            string toolPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".dotnet", "tools", toolName);
            return System.IO.File.Exists(toolPath) ? [toolPath] : null;
        }

        /// <summary>Normalize a configured host path to an absolute one. `res://` and plain
        /// relative paths resolve against the project root — the committed project setting must
        /// stay device-portable, and the launched process's CWD is not guaranteed to be the
        /// project directory. Empty stays empty.</summary>
        internal static string ResolveHostPath(string configured)
        {
            if (configured.Length == 0)
            {
                return configured;
            }
            if (configured.StartsWith("res://", System.StringComparison.Ordinal))
            {
                return ProjectSettings.GlobalizePath(configured);
            }
            return System.IO.Path.IsPathRooted(configured)
                ? configured
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), configured));
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

        public void OnGenerateModelPrefabs()
        {
            Pipeline.ModelPrefabGenerator.GenerateAll();
        }

        public void OnGeneratePrimitives()
        {
            Pipeline.PrimitiveGlbGenerator.GenerateAll();
        }

        public void OnConvertModels()
        {
            WarnIfKtxMissing("model conversion");
            Pipeline.AssetPipeline.ConvertAllModels();
        }

        public void OnConvertDataGlbs()
        {
            WarnIfKtxMissing("data GLB conversion");
            Pipeline.DataGlbConverter.ConvertAll();
        }

        // Pre-flight: batch conversions run per-file and would otherwise emit one error per GLB;
        // a single up-front warning with the fix beats a wall of failures.
        private static void WarnIfKtxMissing(string operation)
        {
            if (Paradise.Assets.Pipeline.KtxTool.Find() is null)
            {
                GD.PushWarning(
                    $"[Paradise.Export] ktx CLI not found — {operation} will skip KTX2 encoding. " +
                    "Install KTX-Software and set the path in Paradise/Settings….");
            }
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

                // Reachable headlessly so it can be TESTED. It was menu-only, which is how it
                // came to produce prefabs with no renderable component without anything noticing.
                if (OS.GetEnvironment("PARADISE_GENERATE_MODEL_PREFABS") == "1")
                {
                    Pipeline.ModelPrefabGenerator.GenerateAll();
                }

                if (OS.GetEnvironment("PARADISE_CONVERT_DATA_GLBS") == "1")
                {
                    Pipeline.DataGlbConverter.ConvertAll();
                }
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[Paradise.Export] Headless task failed: {ex}");
                exitCode = 1;
            }

            _host.GetTree().Quit(exitCode);
        }

        public void OnOpenSettings()
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
