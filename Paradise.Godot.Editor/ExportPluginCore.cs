#if TOOLS
using Godot;
using Paradise.Export;
using ParadiseGodot.Documents;
using ParadiseGodot.Project;

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

        private const string OpenDocumentMenuItem = "Paradise/Open Document…";
        private const string ExtractModelsMenuItem = "Paradise/Extract Models";
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
            "OnOpenDocument",
            "OnDocumentChosen",
            "OnDocumentDialogClosed",
            "OnProjectSetup",
            "OnOpenSettings",
            "OnPlayDotnet",
            "OnStopDotnet",
            "OnExtractModels",
        ];

        private ParadiseSettingsDialog? _settingsDialog;
        private FileDialog? _documentDialog;
        private Button? _stopButton;
        private readonly Play.ParadiseCli _cli = new();

        public void EnterTree()
        {

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

            _host.AddToolMenuItem(OpenDocumentMenuItem, new Callable(_host, "OnOpenDocument"));
            _host.AddToolMenuItem(ExtractModelsMenuItem, new Callable(_host, "OnExtractModels"));
            _host.AddToolMenuItem(ProjectSetupMenuItem, new Callable(_host, "OnProjectSetup"));
            _host.AddToolMenuItem(SettingsMenuItem, new Callable(_host, "OnOpenSettings"));
            _playDotnetButton = new Button
            {
                Text = "Play",
                TooltipText = "Run the open document's game through `paradise host play`: builds the assets into .editor/play/, brings the launcher named by [host] in assets/project.toml up to date, and runs it.",
                Flat = true,
            };
            _playDotnetButton.Connect(BaseButton.SignalName.Pressed, new Callable(_host, "OnPlayDotnet"));
            _host.AddControlToContainer(EditorPlugin.CustomControlContainer.Toolbar, _playDotnetButton);
            _stopButton = new Button
            {
                Text = "Stop",
                TooltipText = "Stop the game Play started.",
                Flat = true,
            };
            _stopButton.Connect(BaseButton.SignalName.Pressed, new Callable(_host, "OnStopDotnet"));
            _host.AddControlToContainer(EditorPlugin.CustomControlContainer.Toolbar, _stopButton);
            // Ctrl+S has to reach the document, or the author's edits live only in a cache that the
            // next open overwrites.
            _host.SceneSaved += OnSceneSaved;
            GD.Print($"[Paradise.Export] Plugin loaded. Core: {ParadiseExportInfo.Describe()}");
            ProjectSetup.CheckExportVersion();
            KeepGodotOutOfTheAssetTrees();

        }

        /// <summary>At every load, not only on Project Setup: the build creates <c>.editor/</c>
        /// and <c>build/</c> on its own, and a fresh clone has neither yet — the marker has to be
        /// there before Godot's next scan finds a tree full of documents it cannot import.</summary>
        private static void KeepGodotOutOfTheAssetTrees()
        {
            if (!ParadiseProject.TryOpen(out var project, out _) || project is null) return;
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
            // Button first: if any teardown below throws, a leftover toolbar button whose pressed
            // connection points into an unloaded assembly is the failure users actually see.
            if (_playDotnetButton is not null)
            {
                _host.RemoveControlFromContainer(EditorPlugin.CustomControlContainer.Toolbar, _playDotnetButton);
                _playDotnetButton.QueueFree();
                _playDotnetButton = null;
            }
            if (_stopButton is not null)
            {
                _host.RemoveControlFromContainer(EditorPlugin.CustomControlContainer.Toolbar, _stopButton);
                _stopButton.QueueFree();
                _stopButton = null;
            }
            OnDocumentDialogClosed();
            _host.RemoveToolMenuItem(OpenDocumentMenuItem);
            _host.RemoveToolMenuItem(ExtractModelsMenuItem);
            _host.RemoveToolMenuItem(ProjectSetupMenuItem);
            _host.RemoveToolMenuItem(SettingsMenuItem);
            _host.SceneSaved -= OnSceneSaved;
            if (_settingsDialog is not null)
            {
                _settingsDialog.QueueFree();
                _settingsDialog = null;
            }
        }

        /// <summary>
        /// Toolbar "Play": hand the edited document to <c>paradise host play</c>, which builds the
        /// assets into <c>.editor/play/</c>, brings the launcher up to date and runs it.
        /// </summary>
        /// <remarks>The addon builds nothing itself; what to run and where it is built are the
        /// CLI's to know.</remarks>
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
                if (DocumentHostPath(root, out string? projectRoot) is not { } document) return;
                if (!_cli.Play(projectRoot!, document, extraArgs, out string? problem))
                {
                    GD.PushError($"[Paradise] {problem}");
                    return;
                }

                GD.Print($"[Paradise] Playing '{document}' through `paradise host play` — output: {Play.ParadiseCli.LogPath}");
            }
            catch (System.Exception ex)
            {
                GD.PushError($"[Paradise] Play failed: {ex.Message}");
            }
        }

        /// <summary>Toolbar "Stop": end the game Play started, and the CLI with it.</summary>
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

        /// <summary>
        /// "Paradise/Extract Models": <c>paradise assets extract --all</c> — every GLB under
        /// <c>assets/</c> gets its mesh, skeleton, clip and material documents, and a starter prefab
        /// where nothing places it yet. Runs to completion; the output lands in the editor log.
        /// </summary>
        public void OnExtractModels()
        {
            if (!ParadiseProject.TryOpen(out var project, out var problem) || project is null)
            {
                GD.PushError($"[Paradise] {problem}");
                return;
            }

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

        /// <summary>Pick a <c>*.prefab</c> under assets/ and open it as a scene.</summary>
        /// <remarks>A dialog rather than the FileSystem dock: documents live under
        /// <c>assets/</c>, which a Godot project marks <c>.gdignore</c> precisely so Godot does not
        /// try to import the source tree. The dock cannot show what it is told to ignore.</remarks>
        public void OnOpenDocument()
        {
            if (!ParadiseProject.TryOpen(out var opened, out var problem) || opened is null)
            {
                GD.PushError($"[Paradise] {problem}");
                return;
            }

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
            // Name-based, like every other callable here: a delegate-backed one holds a GC handle
            // into the current assembly, and a rebuild between opening this dialog and choosing a
            // file would leave the selection firing into nothing.
            _documentDialog.Connect(
                FileDialog.SignalName.FileSelected, new Callable(_host, "OnDocumentChosen"));
            _documentDialog.Connect(
                Window.SignalName.CloseRequested, new Callable(_host, "OnDocumentDialogClosed"));
            EditorInterface.Singleton.GetBaseControl().AddChild(_documentDialog);
            _documentDialog.PopupCentered(new Vector2I(900, 640));
        }

        /// <summary>The project is reopened here rather than captured: the dialog is modal to the
        /// author, not to this method, and a disposed mount would be waiting on the other side.</summary>
        public void OnDocumentChosen(string hostPath)
        {
            OnDocumentDialogClosed();
            if (!ParadiseProject.TryOpen(out var project, out var problem) || project is null)
            {
                GD.PushError($"[Paradise] {problem}");
                return;
            }

            using (project)
            {
                DocumentWorkfile.Open(project, project.Files.ConvertPathFromInternal(hostPath));
            }
        }

        /// <summary>The edited document as a host path, and the asset project's root — what
        /// <c>paradise host play --project … --scene …</c> takes. The CLI resolves the built twin
        /// and builds it, so nothing here looks at <c>.editor/play/</c>.</summary>
        private static string? DocumentHostPath(Node root, out string? projectRoot)
        {
            projectRoot = null;
            if (DocumentSession.DocumentOf(root) is not { } authoringPath)
            {
                GD.PushError(
                    "[Paradise] This scene is not an open Paradise document, so there is nothing " +
                    $"to play. Open one with '{OpenDocumentMenuItem}'.");
                return null;
            }

            if (!ParadiseProject.TryOpen(out var project, out var problem) || project is null)
            {
                GD.PushError($"[Paradise] {problem}");
                return null;
            }

            using (project)
            {
                projectRoot = project.Files.ConvertPathToInternal(project.Layout.Root);
                return project.Files.ConvertPathToInternal(project.Paths.FromAssetReferencePath(authoringPath));
            }
        }

        /// <summary>
        /// Godot has written the working scene; write the document it came from.
        /// </summary>
        /// <remarks>
        /// After the <c>.tscn</c> rather than before it, which is the opposite of what the Blender
        /// host does — and for the opposite reason. Blender saves pre-write so the fresh stamp lands
        /// INSIDE the .blend it is about to write; Godot's stamp lives in this process, so there is
        /// nothing to get into the file, and running after means a refusal never costs the author
        /// their working scene.
        /// </remarks>
        private void OnSceneSaved(string filePath)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (DocumentSession.DocumentOf(root) is null) return;

            if (!ParadiseProject.TryOpen(out var project, out var problem) || project is null)
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
