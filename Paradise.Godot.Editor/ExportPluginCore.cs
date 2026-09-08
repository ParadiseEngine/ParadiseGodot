#if TOOLS
using Godot;
using Paradise.Export;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;

namespace ParadiseGodot
{
    /// <summary>Editor plugin behavior, delegated from the res:// shim.</summary>
    /// <remarks>
    /// Cross-assembly GodotObject inheritance breaks assembly reloads with duplicate script registrations
    /// (godotengine/godot#75352), so the shim forwards to this plain class.
    /// </remarks>
    public sealed class ExportPluginCore
    {
        private readonly EditorPlugin _host;

        public ExportPluginCore(EditorPlugin host) => _host = host;

        private const string OpenDocumentMenuItem = "Paradise/Open Document…";
        private static readonly (string Label, string Method)[] MenuItems =
        [
            (OpenDocumentMenuItem, nameof(OnOpenDocument)),
            ("Paradise/Extract Models", nameof(OnExtractModels)),
            ("Paradise/Project Setup", "OnProjectSetup"),
            ("Paradise/Settings…", nameof(OnOpenSettings)),
            ("Paradise/Convert Project", nameof(OnConvertProject)),
        ];

        private Button? _playDotnetButton;
        // The res:// shim must expose these names. Name-based callables survive assembly reloads;
        // Callable.From delegates retain GC handles that become invalid on reload.
        private static readonly string[] ForwardedMethods =
        [
            "OnOpenDocument",
            "OnDocumentChosen",
            "OnDocumentDialogClosed",
            "OnProjectSetup",
            "OnOpenSettings",
            "OnPlayDotnet",
            "OnStopDotnet",
            "OnExtractModels",
            "OnToggleWatch",
            "OnConvertProject",
        ];

        private ParadiseSettingsDialog? _settingsDialog;
        private FileDialog? _documentDialog;
        private Button? _stopButton;
        private Button? _watchButton;
        private readonly Play.ParadiseCli _cli = new();

        public void EnterTree()
        {
            // Diagnose an outdated shim before its missing forwarders cause native callable errors.
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

            foreach (var item in MenuItems)
            {
                _host.AddToolMenuItem(item.Label, new Callable(_host, item.Method));
            }
            _playDotnetButton = AddToolbarButton("Play", nameof(OnPlayDotnet),
                "Run the open document's game through `paradise host play`: builds the assets into .editor/play/, brings the launcher named by [host] in assets/project.toml up to date, and runs it.");
            _stopButton = AddToolbarButton("Stop", nameof(OnStopDotnet), "Stop the game Play started.");
            _watchButton = AddToolbarButton("Watch", nameof(OnToggleWatch),
                "Start or stop `paradise assets watch` for this project: mints sidecars, rebuilds the play tree "
                + "on every change, and carries the tray that reports build status.", toggle: true);
            // Save edits to the source document before the working cache can be regenerated.
            _host.SceneSaved += OnSceneSaved;
            GD.Print($"[Paradise.Export] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");
            ProjectSetup.CheckExportVersion();
            KeepGodotOutOfTheAssetTrees();
        }

        // Create markers at every load: fresh clones lack the derived trees, and Godot must
        // ignore them before the build populates them with engine documents.
        private static void KeepGodotOutOfTheAssetTrees()
        {
            if (!ParadiseProject.TryOpen(out var project, out _)) return;
            using (project)
            {
                try
                {
                    foreach (var marker in project.EnsureGodotIgnores())
                    {
                        GD.Print($"[Paradise] Wrote {marker} so Godot never scans that tree.");
                    }
                }
                catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
                {
                    GD.PushWarning($"[Paradise] Could not write a .gdignore: {ex.Message}");
                }
            }
        }

        public void ExitTree()
        {
            // Remove the button first so a later teardown failure cannot leave it calling an unloaded assembly.
            RemoveToolbarButton(ref _playDotnetButton);
            RemoveToolbarButton(ref _stopButton);
            RemoveToolbarButton(ref _watchButton);
            // Watchers otherwise survive editor shutdown.
            Play.WatchSession.StopAll();
            OnDocumentDialogClosed();
            foreach (var item in MenuItems) _host.RemoveToolMenuItem(item.Label);
            _host.SceneSaved -= OnSceneSaved;
            _settingsDialog?.QueueFree();
            _settingsDialog = null;
        }

        private Button AddToolbarButton(string text, string method, string tooltip, bool toggle = false)
        {
            var button = new Button { Text = text, TooltipText = tooltip, Flat = true, ToggleMode = toggle };
            button.Connect(BaseButton.SignalName.Pressed, new Callable(_host, method));
            _host.AddControlToContainer(EditorPlugin.CustomControlContainer.Toolbar, button);
            return button;
        }

        private void RemoveToolbarButton(ref Button? button)
        {
            if (button is null) return;
            _host.RemoveControlFromContainer(EditorPlugin.CustomControlContainer.Toolbar, button);
            button.QueueFree();
            button = null;
        }

        /// <summary>Play the edited document through the CLI, which owns builds and launch paths.</summary>
        public void OnPlayDotnet()
        {
            try
            {
                Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root is null)
                {
                    GD.PushWarning("[Paradise] No edited scene to play.");
                    return;
                }

                string[] extraArgs = ParadiseSettingsDialog.PlayDotnetArguments();
                if (DocumentHostPaths(root) is not { } paths) return;
                if (!_cli.Play(paths.Root, paths.Document, extraArgs, out string? problem))
                {
                    GD.PushError($"[Paradise] {problem}");
                    return;
                }

                GD.Print($"[Paradise] Playing '{paths.Document}' through `paradise host play` — output: {Play.ParadiseCli.LogPath}");
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[Paradise] Play failed: {ex.Message}");
            }
        }

        public void OnStopDotnet()
        {
            if (!_cli.IsPlaying)
            {
                GD.Print("[Paradise] Nothing is playing.");
                return;
            }

            _cli.Stop();
            GD.Print("[Paradise] Stopped.");
        }

        /// <summary>Extract model documents and missing starter prefabs from every GLB under assets/.</summary>
        public void OnExtractModels()
        {
            var project = OpenProject();
            if (project is null) return;

            string root;
            using (project)
            {
                root = project.Files.ConvertPathToInternal(project.Layout.Root);
            }

            int code = Play.ParadiseCli.Run(["assets", "extract", "--all", "--project", root], root, out string output, out string? failure);
            if (failure is not null)
            {
                GD.PushError($"[Paradise] {failure}");
                return;
            }

            foreach (string line in output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries))
            {
                GD.Print($"[paradise] {line}");
            }
            if (code != 0) GD.PushError($"[Paradise] `paradise assets extract --all` exited {code}.");
        }

        // The FileSystem dock cannot show assets/ because Godot ignores the source tree.
        public void OnOpenDocument()
        {
            var opened = OpenProject();
            if (opened is null) return;

            string assets;
            using (opened)
            {
                assets = opened.Files.ConvertPathToInternal(opened.Layout.Assets);
            }

            _documentDialog?.QueueFree();
            _documentDialog = new FileDialog
            {
                Title = "Open Paradise document",
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                CurrentDir = assets,
                Filters = [$"*{AssetProjectPaths.DocumentSuffix} ; Paradise documents"],
            };
            _documentDialog.Connect(
                FileDialog.SignalName.FileSelected, new Callable(_host, "OnDocumentChosen"));
            _documentDialog.Connect(
                Window.SignalName.CloseRequested, new Callable(_host, "OnDocumentDialogClosed"));
            EditorInterface.Singleton.GetBaseControl().AddChild(_documentDialog);
            _documentDialog.PopupCentered(new Vector2I(900, 640));
        }

        // Reopen the project after selection: the dialog outlives the mount used to open it.
        public void OnDocumentChosen(string hostPath)
        {
            OnDocumentDialogClosed();
            using var project = OpenProject();
            if (project is null) return;

            DocumentWorkfile.Open(project, project.Files.ConvertPathFromInternal(hostPath));
            StartWatching(project.Files.ConvertPathToInternal(project.Layout.Root));
        }

        // Automatic watching leaves an existing external watcher unannounced.
        private void StartWatching(string projectRoot)
        {
            if (!ParadiseSettingsDialog.ReadFlag(Play.WatchSession.AutoWatchSetting, @default: true)) return;

            if (Play.WatchSession.EnsureFor(projectRoot, out var problem))
            {
                GD.Print($"[Paradise] Watching {projectRoot} — {Play.WatchSession.LogPathFor(projectRoot)}");
            }
            else if (problem?.StartsWith("Another", StringComparison.Ordinal) != true)
            {
                GD.PushWarning($"[Paradise] {problem}");
            }
            RefreshWatchButton(projectRoot);
        }

        private void RefreshWatchButton(string projectRoot)
        {
            if (_watchButton is null) return;
            bool watching = Play.WatchSession.IsWatching(projectRoot);
            _watchButton.ButtonPressed = watching;
            _watchButton.Text = watching ? "Watching" : "Watch";
        }

        /// <summary>Build stale document workfiles and model scenes under .editor/godot/.</summary>
        /// <remarks>Run on demand: parsing every GLB is too expensive for plugin load.</remarks>
        public void OnConvertProject()
        {
            using var project = OpenProject();
            if (project is null) return;

            var models = ProjectMirror.Models(project.Files, project.Layout, project.Paths);
            int converted = 0, failed = 0;
            foreach (var model in models)
            {
                if (!model.Stale) continue;
                if (project.Paths.ToResourcePath(model.Mirror) is not { } resPath)
                {
                    failed++;
                    continue;
                }

                if (Authoring.ModelMirror.Write(
                    project.Files.ConvertPathToInternal(model.Source), resPath, out var modelProblem))
                {
                    WorkfileStamp.Write(project.Files, model.Mirror, model.Source);
                    converted++;
                }
                else
                {
                    GD.PushWarning($"[Paradise] {model.Source}: {modelProblem}");
                    failed++;
                }
            }

            var documents = ProjectMirror.Documents(project.Files, project.Layout, project.Paths);
            int built = 0;
            foreach (var document in documents)
            {
                // Rebuilding a current workfile would discard authored nodes.
                if (!document.Stale) continue;
                if (DocumentWorkfile.Materialize(project, document.Source)) built++;
            }

            GD.Print(
                $"[Paradise] Converted the project into .editor/godot/: {built} of {documents.Count} " +
                $"document(s) built, {converted} of {models.Count} model(s) mirrored" +
                (failed > 0 ? $", {failed} failed." : "."));
        }

        /// <summary>Toggle watching explicitly, regardless of the auto-watch setting.</summary>
        public void OnToggleWatch()
        {
            using var project = OpenProject();
            if (project is null) return;

            string root = project.Files.ConvertPathToInternal(project.Layout.Root);
            if (Play.WatchSession.IsWatching(root))
            {
                Play.WatchSession.StopFor(root);
                GD.Print("[Paradise] Stopped watching.");
            }
            else if (Play.WatchSession.EnsureFor(root, out var refusal))
            {
                GD.Print($"[Paradise] Watching {root} — {Play.WatchSession.LogPathFor(root)}");
            }
            else
            {
                GD.PushWarning($"[Paradise] {refusal}");
            }
            RefreshWatchButton(root);
        }

        // Pass source paths to the CLI; it resolves and builds the play-tree copies.
        private static (string Root, string Document)? DocumentHostPaths(Node root)
        {
            if (DocumentSession.DocumentOf(root) is not { } authoringPath)
            {
                GD.PushError(
                    "[Paradise] This scene is not an open Paradise document, so there is nothing " +
                    $"to play. Open one with '{OpenDocumentMenuItem}'.");
                return null;
            }

            using var project = OpenProject();
            if (project is null) return null;

            return (
                project.Files.ConvertPathToInternal(project.Layout.Root),
                project.Files.ConvertPathToInternal(project.Paths.FromAssetReferencePath(authoringPath)));
        }

        private static ParadiseProject? OpenProject()
        {
            if (ParadiseProject.TryOpen(out var project, out var problem)) return project;
            GD.PushError($"[Paradise] {problem}");
            return null;
        }

        // Save the document after Godot saves the workfile, so a refusal preserves the author's scene.
        // The stamp is in memory and does not need to be included in the .tscn write.
        private void OnSceneSaved(string filePath)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (DocumentSession.DocumentOf(root) is null) return;

            if (!ParadiseProject.TryOpen(out var project, out var problem))
            {
                GD.PushError($"[Paradise] The scene saved, but its document did not: {problem}");
                return;
            }

            using (project)
            {
                DocumentWriter.Save(project, root!);
            }
        }

        public void OnDocumentDialogClosed()
        {
            _documentDialog?.QueueFree();
            _documentDialog = null;
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
