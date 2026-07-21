using System;
using Godot;
using Paradise.Sample.ImGui;
using Paradise.Sample.Pool.Ui;
using Paradise.Sample.Ui;
using ParadiseGodot.Runtime.Ui;

namespace ParadiseGodot.Runtime
{
    /// <summary>Godot play-mode host for the "Space Odyssey" sample (<c>scenes/odyssey.tscn</c>).
    /// The UI runs on <see cref="ImGuiSampleRunner"/>'s 60 Hz sim thread — the same two-half
    /// snapshot machinery as the game bridges. This bridge only routes Godot input into the
    /// runner's queue and hosts the ImGui render half (<see cref="ImGuiCanvasRenderer"/>
    /// replaying the sim-thread-produced snapshots as canvas items). Same sample core as
    /// <c>Paradise.Sample.Runtime --game odyssey</c>.</summary>
    public partial class OdysseyBridge : Node
    {
        private ImGuiSampleRunner? _runner;
        private ImGuiUiCore? _imgui;
        private OdysseyUi? _sample;
        private bool _faulted;

        public override void _Ready()
        {
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;
            try
            {
                _imgui = new ImGuiUiCore((uint)size.X, (uint)size.Y);
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
            {
                GD.PushError($"[OdysseyBridge] cimgui unavailable — cannot run the sample UI: {e.Message}");
                return;
            }

            _runner = new ImGuiSampleRunner();
            _sample = new OdysseyUi();            // MVVM composition root: owns the snapshot sim
            _runner.OnSimTick = _sample.Tick;     // step the sim on the sim thread each frame
            _imgui.AddDraw(_sample.Draw);         // the thin ImGui View over the ViewModel
            _runner.UiInput = _imgui.Input; // the sim thread owns the ImGui frame from here on

            var renderer = new ImGuiCanvasRenderer { Name = "ImGuiRenderer" };
            renderer.Initialize(_imgui);
            var layer = new CanvasLayer { Name = "ImGuiLayer", Layer = 100 };
            layer.AddChild(renderer);
            AddChild(layer);

            GetViewport().SizeChanged += OnViewportResized;

            _runner.Start();
            GD.Print("[OdysseyBridge] Sim thread started.");
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
                GD.PushError($"[OdysseyBridge] sim thread faulted: {ex}");
                _faulted = true;
            }
        }

        public override void _ExitTree()
        {
            _runner?.Dispose();
            _runner = null;
            _sample?.Dispose();
            _sample = null;
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
