#if TOOLS
using System.IO;
using Godot;
using ParadiseExport.Pipeline;

namespace ParadiseGodot
{
    /// <summary>
    /// "Paradise/Settings…" window: machine-level external-tool paths (ktx, Blender) stored in
    /// EditorSettings (per-user, never committed) and applied as the <c>PARADISE_*_PATH</c>
    /// environment variables the ParadiseExport pipeline already resolves first — no pipeline
    /// changes, and spawned child processes inherit them.
    /// </summary>
    [Tool]
    public partial class ParadiseSettingsDialog : ConfirmationDialog
    {
        private const string KtxSetting = "paradise/tools/ktx_path";
        private const string BlenderSetting = "paradise/tools/blender_path";

        // Track what THIS session applied, so clearing a setting can unset the variable we set
        // without wiping one the user provided externally (shell/launchd).
        private static bool _appliedKtx;
        private static bool _appliedBlender;

        private readonly LineEdit _ktxEdit;
        private readonly Label _ktxStatus;
        private readonly LineEdit _blenderEdit;
        private readonly Label _blenderStatus;
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
            RefreshStatus();
        }

        private void SaveAndApply()
        {
            EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
            settings.SetSetting(KtxSetting, _ktxEdit.Text.Trim());
            settings.SetSetting(BlenderSetting, _blenderEdit.Text.Trim());
            ApplySavedSettings();
        }

        private void RefreshStatus()
        {
            Describe(_ktxEdit, _ktxStatus, "ktx", () => KtxCreate.FindKtx());
            Describe(_blenderEdit, _blenderStatus, "Blender", BlenderFbxGlb.FindBlender);
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
