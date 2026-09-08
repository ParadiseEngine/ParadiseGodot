#if TOOLS
using System.IO;
using Godot;

namespace ParadiseGodot
{
    /// <summary>Per-user editor settings. Game configuration stays in the asset project.</summary>
    [Tool]
    public partial class ParadiseSettingsDialog : ConfirmationDialog
    {
        private const string PlayDotnetArgsSetting = "paradise/play/dotnet_args";

        /// <summary>Initial launcher arguments; a saved empty setting stays empty.</summary>
        public const string DefaultPlayDotnetArgs = "";

        private readonly LineEdit _cliEdit;
        private readonly Label _cliStatus;
        private readonly LineEdit _playArgsEdit;
        private readonly LineEdit _profileEdit;
        private readonly LineEdit _envEdit;
        private readonly CheckBox _autoWatchCheck;
        private EditorFileDialog? _fileDialog;

        public ParadiseSettingsDialog()
        {
            Title = "ParadiseEngine Settings";
            OkButtonText = "Save";
            MinSize = new Vector2I(700, 0);

            var layout = new VBoxContainer();
            layout.AddThemeConstantOverride("separation", 10);
            AddChild(layout);

            (_cliEdit, _cliStatus) = AddCliRow(layout,
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

        internal static bool ReadFlag(string name, bool @default)
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(name) ? settings.GetSetting(name).AsBool() : @default;
        }

        public static string[] PlayDotnetArguments() => TokenizeArguments(ReadPlayArguments());

        private static string ReadPlayArguments()
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            return settings.HasSetting(PlayDotnetArgsSetting)
                ? settings.GetSetting(PlayDotnetArgsSetting).AsString()
                : DefaultPlayDotnetArgs;
        }

        /// <summary>Split on whitespace with double-quote grouping, identically on every platform.</summary>
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

        private static HBoxContainer AddRow(VBoxContainer layout, string label)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) });
            layout.AddChild(row);
            return row;
        }

        private (LineEdit Edit, Label Status) AddCliRow(VBoxContainer layout, string hint)
        {
            var row = AddRow(layout, "paradise CLI path");
            var edit = new LineEdit
            {
                PlaceholderText = "empty = auto-detect (environment / vendored tools / PATH)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = hint,
            };
            row.AddChild(edit);

            var browse = new Button { Text = "Browse…" };
            browse.Pressed += OpenCliBrowse;
            row.AddChild(browse);

            var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            layout.AddChild(status);

            edit.TextChanged += _ => RefreshStatus();
            return (edit, status);
        }

        private static CheckBox AddCheckRow(VBoxContainer layout, string label, string hint)
        {
            var check = new CheckBox { TooltipText = hint };
            AddRow(layout, label).AddChild(check);
            return check;
        }

        private static LineEdit AddTextRow(VBoxContainer layout, string label, string hint)
        {
            var edit = new LineEdit
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = hint,
            };
            AddRow(layout, label).AddChild(edit);
            return edit;
        }

        private void OpenCliBrowse()
        {
            if (_fileDialog is null)
            {
                _fileDialog = new EditorFileDialog
                {
                    Title = "Select the paradise CLI executable",
                    FileMode = EditorFileDialog.FileModeEnum.OpenFile,
                    Access = EditorFileDialog.AccessEnum.Filesystem,
                };
                _fileDialog.FileSelected += path =>
                {
                    _cliEdit.Text = path;
                    RefreshStatus();
                };
                AddChild(_fileDialog);
            }

            _fileDialog.PopupCenteredRatio(0.6f);
        }

        private void LoadFromSettings()
        {
            _cliEdit.Text = ReadSetting(Play.ParadiseCli.CliPathSetting);
            _playArgsEdit.Text = ReadPlayArguments();
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
