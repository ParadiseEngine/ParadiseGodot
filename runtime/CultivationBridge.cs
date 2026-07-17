using System;
using System.Linq;
using Godot;
using ParadiseCultivation;
using ParadiseGame.Ui;
using ParadiseGodot.Runtime.Ui;
using ParadiseUi;

namespace ParadiseGodot.Runtime
{
    /// <summary>Godot play-mode host for the Immortal Cultivation slice
    /// (<c>scenes/cultivation.tscn</c>). The game runs on <see cref="CultivationRunner"/>'s
    /// 60 Hz sim thread — the same snapshot machinery as <see cref="EcsSceneBridge"/>'s
    /// SimulationRunner (immutable world snapshots, pre-created pool, command queue). This
    /// bridge only routes Godot input into the runner's UI queue and hosts the ImGui render
    /// half (<see cref="ImGuiCanvasRenderer"/> replaying the sim-thread-produced snapshots as
    /// canvas items). Same game core + panels as <c>ParadiseRuntime --game cultivation</c>.</summary>
    public partial class CultivationBridge : Node
    {
        [Export(PropertyHint.File, "*.json")] public string ConfigPath { get; set; } = "res://data/cultivation/config.json";
        [Export] public int Seed { get; set; } = 20260716;
        [Export] public int WorldSizeIndex { get; set; } = -1; // -1 = config default

        private CultivationRunner? _runner;
        private ImGuiUiCore? _imgui;
        private bool _faulted;

        public override void _Ready()
        {
            // ConfigPath names the core file; siblings (names/dialogue/text) compose in.
            var configDir = ConfigPath[..ConfigPath.LastIndexOf('/')];
            string ReadPart(string file)
            {
                var text = Godot.FileAccess.GetFileAsString($"{configDir}/{file}");
                if (string.IsNullOrEmpty(text))
                {
                    throw new System.IO.InvalidDataException($"config file '{configDir}/{file}' not found or empty");
                }
                return text;
            }

            CultivationConfig config;
            string glyphSource;
            try
            {
                config = CultivationConfig.Load(ReadPart);
                // Glyph source: every authored character across ALL config files gets a glyph.
                glyphSource = string.Concat(ConfigFiles.All.Select(ReadPart));
            }
            catch (Exception e)
            {
                GD.PushError($"[CultivationBridge] Config load failed: {e.Message}");
                return;
            }

            var size = (Vector2I)GetViewport().GetVisibleRect().Size;
            try
            {
                // CJK-capable font (chat accepts free text): config path, or probe system fonts.
                var fontConfig = new UiFontConfig(
                    string.IsNullOrWhiteSpace(config.Ui.FontPath)
                        ? null
                        : ProjectSettings.GlobalizePath(config.Ui.FontPath),
                    config.Ui.FontSizePixels,
                    glyphSource);
                _imgui = new ImGuiUiCore((uint)size.X, (uint)size.Y, fontConfig);
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
            {
                GD.PushError($"[CultivationBridge] cimgui unavailable — cannot run the cultivation UI: {e.Message}");
                return;
            }

            // The runner ctor pre-creates its world pool on THIS (owner) thread —
            // SharedWorld.CreateWorld is thread-affinity-guarded (see .claude/lessons.md).
            _runner = new CultivationRunner(config, Seed, WorldSizeIndex >= 0 ? WorldSizeIndex : null)
            {
                // Godot play mode writes saves under user://, not the process cwd.
                SaveRoot = ProjectSettings.GlobalizePath($"user://{config.Save.Directory}"),
                GlyphSource = glyphSource, // LLM text is filtered to the baked glyph set
            };
            // Saved panel settings win over the environment; either way no key = fully offline.
            if (OpenAiLlmClient.TryCreate(config.Llm, LlmSettings.Resolve(_runner.SaveRoot)) is { } llmClient)
            {
                _runner.Llm = llmClient; // the runner owns and disposes the client
                GD.Print($"[CultivationBridge] LLM layer online ({llmClient.Model} @ {llmClient.BaseUrl}).");
            }
            _imgui.AddDraw(new CultivationUi(_runner).Draw);
            _runner.UiInput = _imgui.Input; // the sim thread owns the ImGui frame from here on

            var renderer = new ImGuiCanvasRenderer { Name = "ImGuiRenderer" };
            renderer.Initialize(_imgui);
            var layer = new CanvasLayer { Name = "ImGuiLayer", Layer = 100 };
            layer.AddChild(renderer);
            AddChild(layer);

            GetViewport().SizeChanged += OnViewportResized;

            _runner.Start();
            GD.Print(
                $"[CultivationBridge] seed {Seed}: {_runner.Map.Width}x{_runner.Map.Height} world, " +
                $"{_runner.Map.Sites.Count} sites, {_runner.Npcs.Count} cultivators. Sim thread started.");
        }

        private void OnViewportResized()
        {
            if (_runner is null) return;
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;
            _runner.EnqueueUiEvent(UiEvent.Resize(size.X, size.Y));
        }

        public override void _Process(double delta)
        {
            if (_runner is null || _faulted) return;
            if (_runner.ThreadException is { } ex)
            {
                GD.PushError($"[CultivationBridge] simulation thread faulted: {ex}");
                _faulted = true;
            }
        }

        public override void _ExitTree()
        {
            _runner?.Dispose();
            _runner = null;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_runner is null) return;

            switch (@event)
            {
                case InputEventMouseMotion motion:
                    _runner.EnqueueUiEvent(UiEvent.PointerMove(motion.Position.X, motion.Position.Y));
                    break;

                case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(0f, wheelUp.Factor));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(0f, -wheelDown.Factor));
                    break;

                case InputEventMouseButton { Pressed: true } down when ToUiButton(down.ButtonIndex) is { } downButton:
                    _runner.EnqueueUiEvent(new UiEvent(
                        UiEventKind.PointerDown, down.Position.X, down.Position.Y, downButton,
                        default, default, false));
                    break;
                case InputEventMouseButton { Pressed: false } up when ToUiButton(up.ButtonIndex) is { } upButton:
                    _runner.EnqueueUiEvent(UiEvent.PointerUp(up.Position.X, up.Position.Y, upButton));
                    break;

                case InputEventKey key:
                {
                    if (ToUiKey(key.Keycode) is { } uiKey)
                    {
                        _runner.EnqueueUiEvent(key.Pressed ? UiEvent.KeyDown(uiKey) : UiEvent.KeyUp(uiKey));
                    }
                    if (key.Pressed && key.Unicode >= 0x20 && key.Unicode != 0x7F)
                    {
                        _runner.EnqueueUiEvent(UiEvent.Text((uint)key.Unicode));
                    }
                    break;
                }
            }
        }

        private static UiPointerButton? ToUiButton(MouseButton button) => button switch
        {
            MouseButton.Left => UiPointerButton.Left,
            MouseButton.Right => UiPointerButton.Right,
            MouseButton.Middle => UiPointerButton.Middle,
            _ => null,
        };

        private static UiKey? ToUiKey(Key key) => key switch
        {
            Key.Enter or Key.KpEnter => UiKey.Enter,
            Key.Escape => UiKey.Escape,
            Key.Backspace => UiKey.Backspace,
            Key.Delete => UiKey.Delete,
            Key.Tab => UiKey.Tab,
            Key.Left => UiKey.Left,
            Key.Right => UiKey.Right,
            Key.Up => UiKey.Up,
            Key.Down => UiKey.Down,
            Key.Home => UiKey.Home,
            Key.End => UiKey.End,
            Key.Ctrl => UiKey.Ctrl,
            Key.Shift => UiKey.Shift,
            Key.A => UiKey.A,
            Key.C => UiKey.C,
            Key.D => UiKey.D,
            Key.S => UiKey.S,
            Key.V => UiKey.V,
            Key.W => UiKey.W,
            Key.X => UiKey.X,
            Key.Y => UiKey.Y,
            Key.Z => UiKey.Z,
            _ => null,
        };
    }
}
