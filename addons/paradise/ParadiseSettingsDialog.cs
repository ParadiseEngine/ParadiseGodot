#if TOOLS
using System.IO;
using Godot;
using Paradise.Export.Data;
using Paradise.Export.Pipeline;

namespace ParadiseGodot
{
    /// <summary>
    /// "Paradise/Settings…" window, two storage scopes:
    /// machine-level (EditorSettings, per-user, never committed) — external-tool paths (ktx,
    /// Blender) applied as <c>PARADISE_*_PATH</c> environment variables, and the "Play .NET"
    /// launch arguments; and project-level (ProjectSettings, committed in project.godot) — the
    /// global physics dynamics tuning, re-exported to <c>data/ProjectSettings.json</c> on save
    /// so the standalone runtime simulates with the same values.
    /// </summary>
    [Tool]
    public partial class ParadiseSettingsDialog : ConfirmationDialog
    {
        private const string KtxSetting = "paradise/tools/ktx_path";
        private const string BlenderSetting = "paradise/tools/blender_path";
        private const string PlayDotnetArgsSetting = "paradise/play/dotnet_args";
        private const string RuntimeHostSetting = "paradise/play/runtime_host";

        /// <summary>Extra Paradise.Sample.Runtime CLI arguments the "Play .NET" button appends after
        /// <c>--scene</c>. Only the initial default — an intentionally emptied setting stays empty.
        /// Empty since the Noesis migration's phase-5 flip: runtime hosts render their UI
        /// unconditionally now, and the legacy <c>--imgui</c>/<c>--noesis</c> flags are
        /// accepted-and-ignored / page-override only.</summary>
        public const string DefaultPlayDotnetArgs = "";

        // Track what THIS session applied, so clearing a setting can unset the variable we set
        // without wiping one the user provided externally (shell/launchd).
        private static bool _appliedKtx;
        private static bool _appliedBlender;

        private readonly LineEdit _ktxEdit;
        private readonly Label _ktxStatus;
        private readonly LineEdit _blenderEdit;
        private readonly Label _blenderStatus;
        private readonly LineEdit _runtimeHostEdit;
        private readonly Label _runtimeHostStatus;
        private readonly LineEdit _playArgsEdit;
        private readonly LineEdit _dataDirEdit;
        private readonly LineEdit _minSpeedEdit;
        private readonly LineEdit _skinEdit;
        private readonly LineEdit _pushStrengthEdit;
        private readonly LineEdit _staticRestitutionEdit;
        private readonly LineEdit _gravityYEdit;
        private readonly LineEdit _staticFrictionEdit;
        private readonly LineEdit _minAngularSpeedEdit;
        private EditorFileDialog? _fileDialog;
        private LineEdit? _browseTarget;

        public ParadiseSettingsDialog()
        {
            Title = "ParadiseEngine Settings";
            OkButtonText = "Save";
            MinSize = new Vector2I(700, 0);

            var layout = new VBoxContainer();
            layout.AddThemeConstantOverride("separation", 10);
            AddChild(layout);

            (_ktxEdit, _ktxStatus) = AddToolRow(layout, "ktx",
                "KTX-Software v5 `ktx` CLI (`ktx create` KTX2 encoder). Used by scene export for GLB-embedded textures.");
            (_blenderEdit, _blenderStatus) = AddToolRow(layout, "Blender",
                "FBX → GLB conversion (Paradise/Convert Models).");
            _playArgsEdit = AddTextRow(layout, "Play .NET args",
                "Extra runtime-host CLI arguments appended by the toolbar \"Play .NET\" button " +
                "(after --scene), e.g. --imgui --audio banks --fov 60. Double quotes group an " +
                "argument with spaces.");

            layout.AddChild(new Label
            {
                Text = "Project (saved to project.godot)",
            });
            _dataDirEdit = AddTextRow(layout, "Data directory",
                $"res:// directory the engine-neutral contract is exported to (default {ParadisePaths.DefaultDataDir}). " +
                "The asset pipeline (KTX2 hooks, primitives) and the runtime host read the same tree.");
            (_runtimeHostEdit, _runtimeHostStatus) = AddToolRow(layout, "runtime host",
                "The standalone .NET runtime the \"Play .NET\" button launches. A .csproj path runs " +
                "`dotnet run --project`; anything else runs directly. res:// and relative paths " +
                "resolve against the project root, so a host that lives in this repo stays portable " +
                "across devices (committed to project.godot). Empty = auto: this project's " +
                "Paradise.Sample.Runtime, else the installed paradise-runtime dotnet tool. A " +
                "machine-level EditorSettings 'paradise/play/runtime_host' overrides this if set.");

            layout.AddChild(new Label
            {
                Text = "Project physics (saved to project.godot, exported to data/ProjectSettings.json)",
            });
            _minSpeedEdit = AddTextRow(layout, "Min speed",
                "Dynamic-body speeds below this snap to rest (m/s).");
            _skinEdit = AddTextRow(layout, "Skin",
                "Clearance kept between dynamic bodies and static surfaces (meters) — the speculative-contact margin.");
            _pushStrengthEdit = AddTextRow(layout, "Push strength",
                "Scale applied to a character pusher's velocity when injected into a ball.");
            _staticRestitutionEdit = AddTextRow(layout, "Static restitution",
                "Body ↔ static bounce fallback when no obstacle-layer static in the scene authors a Restitution.");
            _gravityYEdit = AddTextRow(layout, "Gravity Y",
                "Vertical gravity (m/s²) on balls; holds them on the felt and drives draw/jump/masse. Default -9.81.");
            _staticFrictionEdit = AddTextRow(layout, "Static friction",
                "Coulomb μ for ball↔cushion/cloth contacts — the coupling that turns spin into draw/follow/english/throw.");
            _minAngularSpeedEdit = AddTextRow(layout, "Min angular speed",
                "Angular speeds below this settle to rest when a ball is supported (rad/s).");

            AboutToPopup += LoadFromSettings;
            Confirmed += SaveAndApply;
        }

        /// <summary>Apply the saved paths as environment variables — called at plugin load so
        /// settings take effect every session (including headless exports) and after Save.</summary>
        public static void ApplySavedSettings()
        {
            ApplyOne(ReadSetting(KtxSetting), KtxCreate.KtxPathEnvironmentVariable, ref _appliedKtx);
            ApplyOne(ReadSetting(BlenderSetting), BlenderFbxGlb.BlenderPathEnvironmentVariable, ref _appliedBlender);
        }

        private static void ApplyOne(string value, string variable, ref bool applied)
        {
            if (value.Length > 0)
            {
                System.Environment.SetEnvironmentVariable(variable, value);
                applied = true;
            }
            else if (applied)
            {
                System.Environment.SetEnvironmentVariable(variable, null);
                applied = false;
            }
        }

        private static string ReadSetting(string name)
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(name) ? settings.GetSetting(name).AsString().Trim() : "";
        }

        /// <summary>The configured runtime-host path ("" = auto-resolve). The machine-level
        /// EditorSettings value (personal override) wins over the committed project setting
        /// (the portable default for projects that ship their own host).</summary>
        public static string RuntimeHostPath()
        {
            string machine = ReadSetting(RuntimeHostSetting);
            if (machine.Length > 0)
            {
                return machine;
            }
            return ProjectSettings.HasSetting(RuntimeHostSetting)
                ? ProjectSettings.GetSetting(RuntimeHostSetting).AsString().Trim()
                : "";
        }

        /// <summary>The "Play .NET" extra arguments, tokenized for a process argv.
        /// <see cref="DefaultPlayDotnetArgs"/> until the user first saves the setting.</summary>
        public static string[] PlayDotnetArguments()
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            string raw = settings.HasSetting(PlayDotnetArgsSetting)
                ? settings.GetSetting(PlayDotnetArgsSetting).AsString()
                : DefaultPlayDotnetArgs;
            return TokenizeArguments(raw);
        }

        /// <summary>Whitespace-split with double-quote grouping (<c>--ui "my file.xaml"</c> is
        /// two tokens) — the minimal shell-like rule, applied identically on every platform so
        /// the setting means the same thing under CreateProcess argv and the sh wrapper.</summary>
        public static string[] TokenizeArguments(string commandLine)
        {
            var tokens = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (!quoted && char.IsWhiteSpace(c))
                {
                    if (any) tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
                else
                {
                    current.Append(c);
                    any = true;
                }
            }
            if (any) tokens.Add(current.ToString());
            return tokens.ToArray();
        }

        private (LineEdit Edit, Label Status) AddToolRow(VBoxContainer layout, string toolName, string hint)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = $"{toolName} path", CustomMinimumSize = new Vector2(110, 0) });

            var edit = new LineEdit
            {
                PlaceholderText = "empty = auto-detect (environment / vendored tools / PATH)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = hint,
            };
            row.AddChild(edit);

            var browse = new Button { Text = "Browse…" };
            browse.Pressed += () => OpenBrowse(edit, toolName);
            row.AddChild(browse);
            layout.AddChild(row);

            var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            layout.AddChild(status);

            edit.TextChanged += _ => RefreshStatus();
            return (edit, status);
        }

        private static LineEdit AddTextRow(VBoxContainer layout, string label, string hint)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) });

            var edit = new LineEdit
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = hint,
            };
            row.AddChild(edit);
            layout.AddChild(row);
            return edit;
        }

        private void OpenBrowse(LineEdit target, string toolName)
        {
            if (_fileDialog is null)
            {
                _fileDialog = new EditorFileDialog
                {
                    FileMode = EditorFileDialog.FileModeEnum.OpenFile,
                    Access = EditorFileDialog.AccessEnum.Filesystem,
                };
                _fileDialog.FileSelected += path =>
                {
                    if (_browseTarget is { } edit)
                    {
                        edit.Text = path;
                        RefreshStatus();
                    }
                };
                AddChild(_fileDialog);
            }

            _browseTarget = target;
            _fileDialog.Title = $"Select the {toolName} executable";
            _fileDialog.PopupCenteredRatio(0.6f);
        }

        private void LoadFromSettings()
        {
            _ktxEdit.Text = ReadSetting(KtxSetting);
            _blenderEdit.Text = ReadSetting(BlenderSetting);
            _runtimeHostEdit.Text = ProjectSettings.HasSetting(RuntimeHostSetting)
                ? ProjectSettings.GetSetting(RuntimeHostSetting).AsString()
                : "";
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            _playArgsEdit.Text = settings.HasSetting(PlayDotnetArgsSetting)
                ? settings.GetSetting(PlayDotnetArgsSetting).AsString()
                : DefaultPlayDotnetArgs;
            _dataDirEdit.Text = ParadisePaths.DataDir;

            var defaults = new PhysicsDynamicsSettingsData();
            _minSpeedEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.MinSpeedSetting, defaults.MinSpeed);
            _skinEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.SkinSetting, defaults.Skin);
            _pushStrengthEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.PushStrengthSetting, defaults.PushStrength);
            _staticRestitutionEdit.Text = ReadProjectFloat(
                Export.ProjectSettingsExporter.DefaultStaticRestitutionSetting, defaults.DefaultStaticRestitution);
            _gravityYEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.GravityYSetting, defaults.GravityY);
            _staticFrictionEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.StaticFrictionSetting, defaults.StaticFriction);
            _minAngularSpeedEdit.Text = ReadProjectFloat(Export.ProjectSettingsExporter.MinAngularSpeedSetting, defaults.MinAngularSpeed);
            RefreshStatus();
        }

        private void SaveAndApply()
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            settings.SetSetting(KtxSetting, _ktxEdit.Text.Trim());
            settings.SetSetting(BlenderSetting, _blenderEdit.Text.Trim());
            settings.SetSetting(PlayDotnetArgsSetting, _playArgsEdit.Text.Trim());
            ProjectSettings.SetSetting(RuntimeHostSetting, _runtimeHostEdit.Text.Trim());
            ApplySavedSettings();
            SaveDataDirectory();
            SaveProjectPhysics();
        }

        // Project physics goes to ProjectSettings (committed) and is immediately re-exported so
        // data/ProjectSettings.json never lags the dialog — the runtime reads the JSON, not
        // project.godot. Unparseable text falls back to the contract default, mirroring what
        // ValidateAndNormalize would keep at export time.
        private void SaveProjectPhysics()
        {
            var defaults = new PhysicsDynamicsSettingsData();
            WriteProjectFloat(Export.ProjectSettingsExporter.MinSpeedSetting, _minSpeedEdit.Text, defaults.MinSpeed);
            WriteProjectFloat(Export.ProjectSettingsExporter.SkinSetting, _skinEdit.Text, defaults.Skin);
            WriteProjectFloat(Export.ProjectSettingsExporter.PushStrengthSetting, _pushStrengthEdit.Text, defaults.PushStrength);
            WriteProjectFloat(Export.ProjectSettingsExporter.DefaultStaticRestitutionSetting,
                _staticRestitutionEdit.Text, defaults.DefaultStaticRestitution);
            WriteProjectFloat(Export.ProjectSettingsExporter.GravityYSetting, _gravityYEdit.Text, defaults.GravityY);
            WriteProjectFloat(Export.ProjectSettingsExporter.StaticFrictionSetting, _staticFrictionEdit.Text, defaults.StaticFriction);
            WriteProjectFloat(Export.ProjectSettingsExporter.MinAngularSpeedSetting, _minAngularSpeedEdit.Text, defaults.MinAngularSpeed);
            ProjectSettings.Save();
            Export.ProjectSettingsExporter.Export(ParadisePaths.ExportPaths());
        }

        private static string ReadProjectFloat(string name, float fallback)
        {
            float value = ProjectSettings.HasSetting(name)
                ? (float)ProjectSettings.GetSetting(name).AsDouble()
                : fallback;
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WriteProjectFloat(string name, string text, float fallback)
        {
            if (!float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            {
                value = fallback;
            }
            ProjectSettings.SetSetting(name, value);
        }

        // Empty text falls back to the conventional default rather than persisting "".
        private void SaveDataDirectory()
        {
            string dir = _dataDirEdit.Text.Trim().TrimEnd('/');
            if (dir.Length == 0)
            {
                dir = ParadisePaths.DefaultDataDir;
            }
            else if (!dir.StartsWith("res://", System.StringComparison.Ordinal))
            {
                GD.PushWarning($"[Paradise] Data directory must be a res:// path — keeping '{ParadisePaths.DataDir}'.");
                return;
            }
            ProjectSettings.SetSetting(ParadisePaths.DataDirSetting, dir);
        }

        private void RefreshStatus()
        {
            Describe(_ktxEdit, _ktxStatus, "ktx", () => KtxCreate.FindKtx());
            Describe(_blenderEdit, _blenderStatus, "Blender", BlenderFbxGlb.FindBlender);
            DescribeRuntimeHost();
        }

        private void DescribeRuntimeHost()
        {
            string text = _runtimeHostEdit.Text.Trim();
            bool ok;
            if (text.Length == 0)
            {
                string[]? host = ParadiseExportPlugin.ResolveRuntimeHostCommand();
                ok = host is not null;
                _runtimeHostStatus.Text = ok
                    ? $"Auto-detected: {string.Join(' ', host!)}"
                    : "No runtime host found — set a path here, or install the paradise-runtime dotnet tool.";
            }
            else
            {
                string resolved = ParadiseExportPlugin.ResolveHostPath(text);
                ok = File.Exists(resolved);
                _runtimeHostStatus.Text = ok ? $"OK: {resolved}" : $"File does not exist: {resolved}";
            }
            _runtimeHostStatus.Modulate = ok ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.75f, 0.4f);
        }

        private static void Describe(LineEdit edit, Label status, string toolName, System.Func<string?> autoDetect)
        {
            string path = edit.Text.Trim();
            bool ok;
            if (path.Length == 0)
            {
                string? found = autoDetect();
                ok = found is not null;
                status.Text = ok
                    ? $"Auto-detected: {found}"
                    : $"{toolName} not found — set a path here, or via environment/vendored tools.";
            }
            else
            {
                ok = File.Exists(path);
                status.Text = ok ? "OK" : "File does not exist.";
            }

            status.Modulate = ok ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.75f, 0.4f);
        }
    }
}
#endif
