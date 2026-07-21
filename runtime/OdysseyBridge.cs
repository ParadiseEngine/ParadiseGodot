using System;
using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using Paradise.Sample.ImGui;
using Paradise.Sample.Odyssey;
using Paradise.Sample.Pool.Ui;
using Paradise.Sample.Ui;
using ParadiseGodot.Runtime.Ui;
using SN = System.Numerics;

namespace ParadiseGodot.Runtime
{
    /// <summary>Godot play-mode host for the "Space Odyssey" sample (<c>scenes/odyssey.tscn</c>): a
    /// piloted 3D spaceship flying a procedural sector map (a star, orbiting planets, asteroids, a glowing
    /// warp gate). The sim ticks on the <see cref="OdysseyRunner"/>'s own 60 Hz thread; this bridge
    /// interpolates the two latest immutable snapshots in <c>_Process</c> onto built-in-mesh nodes and
    /// drives a chase <see cref="Camera3D"/> behind the ship — the 3D twin of
    /// <c>Paradise.Sample.Runtime --game odyssey</c>. Pilot with WASD (thrust/turn), hold SPACE to charge
    /// the warp drive, then fly into the gate to jump (N = new voyage). The ImGui "Star Voyager" HUD draws
    /// as a pure reader overlay. Node writes happen only on Godot's main thread; the sim never touches
    /// Godot. Right-handed throughout (the sim's forward is +Z).</summary>
    public partial class OdysseyBridge : Node3D
    {
        private const double RenderDelaySeconds = 2.0 / 60.0;
        private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;
        private const float ChaseDistance = 14f;
        private const float ChaseHeight = 6f;
        private const float LookAhead = 5f;

        private OdysseyRunner? _runner;
        private ImGuiSampleRunner? _pump;
        private ImGuiUiCore? _imgui;
        private OdysseyView? _view;
        private OdysseyViewModel? _vm;
        private Camera3D? _camera;
        private readonly List<(Node3D Node, Entity Entity, float Scale)> _nodes = new();
        private double _renderSampleTime;
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
                GD.PushError($"[OdysseyBridge] cimgui unavailable — running without the HUD: {e.Message}");
            }

            _runner = new OdysseyRunner();

            // 3D scene: deep-space environment + glow, the star's key light, a dim fill, the chase camera,
            // and one built-in-mesh node per render entity.
            SetupEnvironment();
            _camera = new Camera3D { Name = "ChaseCamera", Current = true, Fov = 60f, Far = 900f };
            AddChild(_camera);
            SpawnNodes();

            // ImGui HUD: a pure reader overlay (the sim owns its own thread; the pump only builds the
            // ImGui frame from the thread-safe ViewModel — no OnSimTick).
            if (_imgui is not null)
            {
                _vm = new OdysseyViewModel(_runner);
                _view = new OdysseyView();
                _imgui.AddDraw(() => _view.Draw(_vm));
                _pump = new ImGuiSampleRunner { UiInput = _imgui.Input };

                var renderer = new ImGuiCanvasRenderer { Name = "ImGuiRenderer" };
                renderer.Initialize(_imgui);
                var layer = new CanvasLayer { Name = "ImGuiLayer", Layer = 100 };
                layer.AddChild(renderer);
                AddChild(layer);
                GetViewport().SizeChanged += OnViewportResized;
            }

            _runner.Start();
            _pump?.Start();
            GD.Print("[OdysseyBridge] Sim thread started.");
        }

        private void SetupEnvironment()
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.01f, 0.01f, 0.03f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.10f, 0.11f, 0.16f),
                AmbientLightEnergy = 1.1f,
                GlowEnabled = true,
                GlowIntensity = 1.3f,
                GlowBloom = 0.4f,
                GlowHdrThreshold = 0.65f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            };
            AddChild(new WorldEnvironment { Name = "SpaceEnvironment", Environment = env });

            // The star's warm key light at the origin. Godot's light energy is a different unit from the
            // engine PBR host's intensity, so tuned independently for a comparable planet brightness.
            AddChild(new OmniLight3D
            {
                Name = "StarLight",
                OmniRange = 260f,
                LightEnergy = 24f,
                LightColor = new Color(1f, 0.9f, 0.7f),
            });
            // A dim cold directional fill so shadowed sides still read.
            var fill = new DirectionalLight3D
            {
                Name = "Fill",
                LightEnergy = 0.5f,
                LightColor = new Color(0.35f, 0.4f, 0.55f),
            };
            fill.LookAtFromPosition(new Vector3(30f, 40f, 20f), Vector3.Zero, Vector3.Up);
            AddChild(fill);
        }

        private void SpawnNodes()
        {
            if (_runner is null) return;

            // Ship: a cone (CylinderMesh with a zero top) pointing +Z. The mesh's local +Y is rotated to
            // +Z so the dart's nose faces the ship's forward; the sim drives the ROOT's transform (yaw).
            var shipRoot = new Node3D { Name = "Ship" };
            var shipMesh = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = 2.3f, RadialSegments = 18 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.62f, 0.66f, 0.78f),
                    Metallic = 0.65f,
                    Roughness = 0.35f,
                },
                Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), Vector3.Zero),
            };
            shipRoot.AddChild(shipMesh);
            AddChild(shipRoot);
            _nodes.Add((shipRoot, _runner.Ship, 1f));

            foreach (var body in _runner.Bodies)
            {
                var material = body.Kind switch
                {
                    0 => Emissive(body.Tint, 7.0f),  // star
                    3 => Emissive(body.Tint, 5.0f),  // warp gate
                    _ => Lit(body.Tint),             // planet / asteroid
                };

                if (body.Kind == 3)
                {
                    // Warp gate: Godot's TorusMesh lies in the XZ plane (hole along Y) — a horizontal disc
                    // edge-on to the chase camera. Rotate the mesh into the XY plane (as the .NET host's
                    // procedural torus) so it reads as an upright ring; the sim spin (about Y) still turns
                    // the parent, matching the .NET gate.
                    var gateRoot = new Node3D { Name = "Gate" };
                    gateRoot.AddChild(new MeshInstance3D
                    {
                        Mesh = new TorusMesh { InnerRadius = 0.85f, OuterRadius = 1.25f },
                        MaterialOverride = material,
                        Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), Vector3.Zero),
                    });
                    AddChild(gateRoot);
                    _nodes.Add((gateRoot, body.Entity, body.Scale));
                    continue;
                }

                var mi = new MeshInstance3D
                {
                    Name = body.Kind == 0 ? "Star" : body.Kind == 1 ? "Planet" : "Asteroid",
                    Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 32, Rings = 16 },
                    MaterialOverride = material,
                };
                AddChild(mi);
                _nodes.Add((mi, body.Entity, body.Scale));
            }
        }

        private static StandardMaterial3D Lit(SN.Vector4 tint) => new()
        {
            AlbedoColor = new Color(tint.X, tint.Y, tint.Z),
            Metallic = 0.10f,
            Roughness = 0.80f,
        };

        // Black albedo so the body is effectively PURE emission (a light source is uniformly bright, not
        // shaded like a lit sphere): a zero albedo kills the diffuse gradient while the emission stays. We
        // do NOT use ShadingMode.Unshaded — Godot's unshaded path drops emission (renders albedo only).
        private static StandardMaterial3D Emissive(SN.Vector4 tint, float energy) => new()
        {
            AlbedoColor = new Color(0f, 0f, 0f),
            Metallic = 0f,
            Roughness = 1f,
            EmissionEnabled = true,
            Emission = new Color(tint.X, tint.Y, tint.Z),
            EmissionEnergyMultiplier = energy,
        };

        private void OnViewportResized()
        {
            if (_pump is null) return;
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;
            _pump.EnqueueUiEvent(UiEvent.Resize(size.X, size.Y));
        }

        public override void _Process(double delta)
        {
            if (_runner is null || _faulted) return;

            if (_runner.ThreadException is { } ex)
            {
                GD.PushError($"[OdysseyBridge] sim thread faulted: {ex}");
                _faulted = true;
                return;
            }

            // Pilot commands (polled; layout-independent physical keys) — pushed to the sim thread.
            float thrust = (Down(Key.W) || Down(Key.Up) ? 1f : 0f) - (Down(Key.S) || Down(Key.Down) ? 1f : 0f);
            float turn = (Down(Key.D) || Down(Key.Right) ? 1f : 0f) - (Down(Key.A) || Down(Key.Left) ? 1f : 0f);
            _runner.SetThrust(thrust);
            _runner.SetTurn(turn);
            _runner.SetCharging(Down(Key.Space));

            if (!_runner.HasSnapshots) return;

            double target = Math.Min(_runner.Now - RenderDelaySeconds, _runner.LatestSnapshotTime);
            _renderSampleTime = _renderSampleTime <= 0.0 ? target : Math.Min(_renderSampleTime + delta, target);
            if (target - _renderSampleTime > MaxRenderSampleLagSeconds)
            {
                _renderSampleTime = target;
            }

            if (!_runner.TrySampleInterpolation(_renderSampleTime, out var worldA, out var worldB, out float alpha))
            {
                return;
            }
            alpha = Math.Clamp(alpha, 0f, 1f);

            Vector3 shipPos = Vector3.Zero;
            Quaternion shipRot = Quaternion.Identity;
            Entity ship = _runner.Ship;
            foreach ((Node3D node, Entity entity, float scale) in _nodes)
            {
                if (!GodotObject.IsInstanceValid(node)) continue;
                if (!worldA.IsAlive(entity) || !worldB.IsAlive(entity)) continue;

                SN.Vector3 posSN = SN.Vector3.Lerp(
                    worldA.GetComponent<Position>(entity).Value, worldB.GetComponent<Position>(entity).Value, alpha);
                SN.Quaternion rotSN = SN.Quaternion.Slerp(
                    worldA.GetComponent<Rotation>(entity).Value, worldB.GetComponent<Rotation>(entity).Value, alpha);
                Vector3 pos = ToGodot(posSN);
                Quaternion rot = ToGodot(rotSN);
                node.GlobalTransform = new Transform3D(new Basis(rot).Scaled(new Vector3(scale, scale, scale)), pos);
                if (entity == ship)
                {
                    shipPos = pos;
                    shipRot = rot;
                }
            }

            // Chase camera: behind + above the ship (the sim's forward is +Z), looking slightly ahead.
            if (_camera is not null)
            {
                Vector3 forward = (new Basis(shipRot) * new Vector3(0f, 0f, 1f));
                if (forward.LengthSquared() < 1e-4f) forward = new Vector3(0f, 0f, 1f);
                forward = forward.Normalized();
                Vector3 eye = shipPos - forward * ChaseDistance + new Vector3(0f, ChaseHeight, 0f);
                _camera.LookAtFromPosition(eye, shipPos + forward * LookAhead, Vector3.Up);
            }
        }

        private static bool Down(Key key) => Input.IsPhysicalKeyPressed(key);

        public override void _ExitTree()
        {
            _runner?.Dispose();
            _runner = null;
            _pump?.Dispose();
            _pump = null;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            switch (@event)
            {
                case InputEventKey { Pressed: true, Echo: false } key when key.PhysicalKeycode == Key.N:
                    _runner?.RequestNewVoyage();
                    break;
                case InputEventKey { Pressed: true, Echo: false } enter when enter.Keycode is Key.Enter or Key.KpEnter:
                    _runner?.RequestWarp();
                    break;
            }

            if (_pump is null) return;

            switch (@event)
            {
                case InputEventMouseMotion motion:
                    _pump.EnqueueUiEvent(UiEvent.PointerMove(motion.Position.X, motion.Position.Y));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                    _pump.EnqueueUiEvent(UiEvent.Scroll(0f, wheelUp.Factor));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                    _pump.EnqueueUiEvent(UiEvent.Scroll(0f, -wheelDown.Factor));
                    break;
                case InputEventMouseButton { Pressed: true } down when ToUiButton(down.ButtonIndex) is { } downButton:
                    _pump.EnqueueUiEvent(new UiEvent(
                        UiEventKind.PointerDown, down.Position.X, down.Position.Y, downButton, default, default, false));
                    break;
                case InputEventMouseButton { Pressed: false } up when ToUiButton(up.ButtonIndex) is { } upButton:
                    _pump.EnqueueUiEvent(UiEvent.PointerUp(up.Position.X, up.Position.Y, upButton));
                    break;
            }
        }

        private static UiPointerButton? ToUiButton(MouseButton button) => button switch
        {
            MouseButton.Left => UiPointerButton.Left,
            MouseButton.Right => UiPointerButton.Right,
            MouseButton.Middle => UiPointerButton.Middle,
            _ => null,
        };

        private static Vector3 ToGodot(SN.Vector3 v) => new(v.X, v.Y, v.Z);
        private static Quaternion ToGodot(SN.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
