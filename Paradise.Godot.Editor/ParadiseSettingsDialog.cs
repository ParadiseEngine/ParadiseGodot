#if TOOLS
using System.IO;
using Godot;

namespace ParadiseGodot
{
    /// <summary>
    /// "Paradise/Settings…" window: machine-level settings (EditorSettings, per-user, never
    /// committed) — where the <c>paradise</c> CLI is when it is not on PATH, and the launch
    /// arguments Play passes on. Everything about the GAME lives in its asset project:
    /// <c>assets/project.toml</c> and the config documents beside it are the game's to edit.
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
        private readonly LineEdit _profileEdit;
        private readonly LineEdit _envEdit;
        private readonly CheckBox _autoWatchCheck;
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

            _envEdit = AddTextRow(layout, "Build env",
                "NAME=VALUE pairs exported before the CLI runs, e.g. " +
                "ParadiseUseEngineSource=false. MSBuild reads environment variables as " +
                "properties, and an editor launched from the desktop inherits no shell.");
            _profileEdit = AddTextRow(layout, "Build profile",
                "The profile the watcher rebuilds with, from [build.profiles] in " +
                "assets/project.toml. Empty means the CLI's own default.");
            _autoWatchCheck = AddCheckRow(layout, "Auto-watch",
                "Start `paradise assets watch` for the project when a document is opened. One " +
                "watcher per project, stopped when the editor closes. Turn this off if you run " +
                "your own, or if another editor is watching the same project.");

            AboutToPopup += LoadFromSettings;
            Confirmed += SaveAndApply;
        }

        internal static string ReadSetting(string name)
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(name) ? settings.GetSetting(name).AsString().Trim() : "";
        }

        /// <summary>A boolean setting, with the value to use until the author first saves one.
        /// Unset is not false: a default of true has to survive a settings file that has never
        /// heard of the key.</summary>
        internal static bool ReadFlag(string name, bool @default)
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(name) ? settings.GetSetting(name).AsBool() : @default;
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

        private static CheckBox AddCheckRow(VBoxContainer layout, string label, string hint)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) });

            var check = new CheckBox { TooltipText = hint };
            row.AddChild(check);
            layout.AddChild(row);
            return check;
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
            _profileEdit.Text = ReadSetting(Play.WatchSession.ProfileSetting);
            _envEdit.Text = ReadSetting(Play.ParadiseCli.CliEnvironmentSetting);
            _autoWatchCheck.ButtonPressed = ReadFlag(Play.WatchSession.AutoWatchSetting, @default: true);
            RefreshStatus();
        }

        private void SaveAndApply()
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            settings.SetSetting(PlayDotnetArgsSetting, _playArgsEdit.Text.Trim());
            settings.SetSetting(Play.ParadiseCli.CliPathSetting, _cliEdit.Text.Trim());
            settings.SetSetting(Play.WatchSession.ProfileSetting, _profileEdit.Text.Trim());
            settings.SetSetting(Play.ParadiseCli.CliEnvironmentSetting, _envEdit.Text.Trim());
            settings.SetSetting(Play.WatchSession.AutoWatchSetting, _autoWatchCheck.ButtonPressed);
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
