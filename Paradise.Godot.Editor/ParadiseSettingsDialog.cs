#if TOOLS
using System.IO;
using Godot;
using Paradise.Export.Data;

namespace ParadiseGodot
{
    /// <summary>
    /// "Paradise/Settings…" window, two storage scopes:
    /// machine-level (EditorSettings, per-user, never committed) — where the <c>paradise</c> CLI
    /// is when it is not on PATH, and the launch arguments Play passes on; and project-level (ProjectSettings, committed in project.godot) — the
    /// global physics dynamics tuning, re-exported to <c>data/ProjectSettings.json</c> on save
    /// so the standalone runtime simulates with the same values.
    /// </summary>
    [Tool]
    public partial class ParadiseSettingsDialog : ConfirmationDialog
    {
        private const string PlayDotnetArgsSetting = "paradise/play/dotnet_args";

        /// <summary>Arguments Play passes to the launcher, after the <c>--</c> that ends the CLI's
        /// own. Only the initial default — an intentionally emptied setting stays empty.</summary>
        public const string DefaultPlayDotnetArgs = "";

        private readonly LineEdit _cliEdit;
        private readonly Label _cliStatus;
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

            (_cliEdit, _cliStatus) = AddToolRow(layout, "paradise CLI",
                "The `paradise` CLI Play and Extract Models run. Empty = auto: PATH, then the global " +
                "dotnet tool (~/.dotnet/tools/paradise). Install it with " +
                "`dotnet tool install --global Paradise.Cli`.");
            _playArgsEdit = AddTextRow(layout, "Play args",
                "Arguments Play passes on to the game's launcher, after the CLI's own " +
                "(`paradise host play … -- <these>`), e.g. --fov 60. Double quotes group an " +
                "argument with spaces.");

            layout.AddChild(new Label
            {
                Text = "Project (saved to project.godot)",
            });
            _dataDirEdit = AddTextRow(layout, "Data directory",
                $"res:// directory the engine-neutral contract is exported to (default {ParadisePaths.DefaultDataDir}). " +
                "The asset pipeline (KTX2 hooks, primitives) and the runtime host read the same tree.");

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

        internal static string ReadSetting(string name)
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(name) ? settings.GetSetting(name).AsString().Trim() : "";
        }

        /// <summary>The arguments Play passes on to the launcher, tokenized for a process argv.
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
            _cliEdit.Text = ReadSetting(Play.ParadiseCli.CliPathSetting);
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
            settings.SetSetting(PlayDotnetArgsSetting, _playArgsEdit.Text.Trim());
            settings.SetSetting(Play.ParadiseCli.CliPathSetting, _cliEdit.Text.Trim());
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
            string text = _cliEdit.Text.Trim();
            bool ok;
            if (text.Length == 0)
            {
                string? found = Play.ParadiseCli.Find();
                ok = found is not null;
                _cliStatus.Text = ok
                    ? $"Auto-detected: {found}"
                    : "No `paradise` CLI found — set a path here, or `dotnet tool install --global Paradise.Cli`.";
            }
            else
            {
                ok = File.Exists(text);
                _cliStatus.Text = ok ? "OK" : "File does not exist.";
            }
            _cliStatus.Modulate = ok ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.75f, 0.4f);
        }

    }
}
#endif
