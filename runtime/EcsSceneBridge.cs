using System;
using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using Paradise.Physics;
using ParadiseExport.Geometry;
using ParadiseGame;
using ParadiseGame.Physics;
using ParadiseGame.Navigation.Detour;
using ParadiseGame.Ui;
using ParadiseGodot.Runtime.Ui;
using ParadiseUi;
using SN = System.Numerics;

namespace ParadiseGodot.Runtime
{
    /// <summary>
    /// Runtime bridge between the threaded simulation and Godot. At <c>_Ready</c> it turns the live scene's
    /// marked entities into ECS entities in a <see cref="SimulationRunner"/> and starts the sim thread
    /// (fixed 60 Hz). Godot renders on its own thread by <b>interpolating between the two latest immutable
    /// world snapshots</b> in <c>_Process</c> — smooth regardless of the render frame rate. Click the ground
    /// to move the player agent; the click is queued to the sim thread.
    ///
    /// Entities are identified by the runtime-safe <c>paradise_entity_guid</c> node metadata (the editor-only
    /// EntityExport script is absent at runtime); the player by group membership. Right-handed throughout.
    /// Node writes happen only on Godot's main thread (<c>_Process</c>); the sim never touches Godot.
    /// </summary>
    public partial class EcsSceneBridge : Node3D
    {
        [Export] public string EntityGuidMeta { get; set; } = "paradise_entity_guid";
        [Export] public string PlayerGroup { get; set; } = "paradise_player";
        [Export] public string BallGroup { get; set; } = "paradise_ball";
        [Export(PropertyHint.File, "*.bin")] public string NavMeshFile { get; set; } = "";
        [Export] public float ArriveRadius { get; set; } = 0.25f;

        // Used only when the player node carries no authored EntityExport.MoveSpeed — the
        // entity's value is the single source of truth, shared with the export contract.
        private const float FallbackMoveSpeed = 3.5f;

        // Fallback character capsule dims when the player node has no CapsuleShape3D to read.
        [Export] public float CharacterRadius { get; set; } = 0.4f;
        [Export] public float CharacterHeight { get; set; } = 1.8f;

        // Fallback dynamic-ball params when a ball node has no SphereMesh to read.
        [Export] public float BallRadius { get; set; } = 0.35f;
        [Export] public float BallMass { get; set; } = 1f;

        // For headless / no-input smoke runs: if set, the player is sent here on _Ready.
        [Export] public bool AutoDemo { get; set; }
        [Export] public Vector3 AutoDemoTarget { get; set; }

        [ExportGroup("UI")]
        // Dear ImGui debug panel (sim-thread immediate mode, rendered as canvas items).
        [Export] public bool EnableImGui { get; set; } = true;
        // NoesisGUI overlay XAML (empty = no Noesis). Rendered on a headless WebGPU device and
        // composited as a premultiplied-alpha texture overlay.
        [Export(PropertyHint.File, "*.xaml")] public string UiXaml { get; set; } = "";

        // Render sampling: interpolate ~2 sim ticks behind the latest snapshot; skip ahead if we fall too far.
        private const double RenderDelaySeconds = 2.0 / 60.0;
        private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;

        private SimulationRunner? _runner;
        private Camera3D? _camera;
        private readonly List<(Node3D Node, Entity Entity)> _agents = new();
        private Entity _player;
        private bool _hasPlayer;
        private double _renderSampleTime;
        private bool _faulted;
        private int _ballCount;
        private ImGuiUiCore? _imgui;
        private NoesisViewCore? _noesis;
        private NoesisTextureOverlay? _noesisOverlay;

        public override void _Ready()
        {
            Node root = Owner ?? GetParent() ?? this;

            string navPath = ResolveNavMeshPath(root);
            byte[] navBytes = Godot.FileAccess.GetFileAsBytes(navPath);
            if (navBytes.Length == 0)
            {
                GD.PushWarning($"[EcsSceneBridge] NavMesh '{navPath}' not found or empty (re-save the scene to export it) — agent movement disabled.");
                return;
            }

            // Static collision geometry for the sim's stateless CollisionWorld — harvested from the
            // same navigation_source group the navmesh bakes from, so physics and pathfinding agree.
            Paradise.Physics.CollisionWorld? collisionWorld = BuildCollisionWorld(root);
            _runner = new SimulationRunner(DetourNavMeshLoader.LoadFromBytes(navBytes), collisionWorld);
            _camera = FindCamera(root);

            foreach (Node3D node in EntityNodes(root))
            {
                SN.Vector3 pos = ToSN(node.GlobalPosition);
                SN.Quaternion rot = ToSN(node.GlobalBasis.GetRotationQuaternion());

                if (node.IsInGroup(PlayerGroup))
                {
                    (float bodyRadius, float bodyHalfLength) = ReadPlayerCapsule(node);
                    _player = _runner.SpawnAgent(pos, rot, ReadAuthoredMoveSpeed(node), ArriveRadius, bodyRadius, bodyHalfLength);
                    _hasPlayer = true;
                    _agents.Add((node, _player));
                }
                else if (node.IsInGroup(BallGroup))
                {
                    // Parity gap: default physics params + inert PoolBall (no pocket capture) —
                    // the bridge reads the live Godot scene, not the exported Rigidbody/trigger
                    // data the .NET host gets through SceneAssembler.
                    Entity ball = _runner.SpawnBall(pos, rot, ReadBallRadius(node), BallMass);
                    _agents.Add((node, ball)); // dynamic: interpolated like the player
                    _ballCount++;
                }
                else
                {
                    _runner.SpawnStatic(pos, rot);
                }
            }

            SetupUi(); // UiInput must be composed before Start (the sim reads it each tick)

            _runner.Start();
            GD.Print($"[EcsSceneBridge] Simulation thread started — {_agents.Count} agent(s). Click the ground to move.");

            AddUiOverlayNodes();

            if (AutoDemo && _hasPlayer)
            {
                _runner.EnqueueMoveTo(_player, ToSN(AutoDemoTarget));
            }
        }

        /// <summary>Build the sim-thread UI halves (shared cores) and hand the composed input
        /// to the runner. Every failure degrades to a warning — the bridge must never lose the
        /// scene over missing UI natives.</summary>
        private void SetupUi()
        {
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;

            if (EnableImGui)
            {
                try
                {
                    _imgui = new ImGuiUiCore((uint)size.X, (uint)size.Y);
                    _imgui.AddDraw(DrawDebugPanel);
                }
                catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
                {
                    GD.PushWarning($"[EcsSceneBridge] cimgui unavailable — ImGui disabled: {e.Message}");
                    _imgui = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(UiXaml))
            {
                // Probe the Noesis native up front: NoesisViewCore creates its View LAZILY on
                // the SIM thread, and a DllNotFoundException there would fault the whole sim.
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(
                        "Noesis", typeof(global::Noesis.GUI).Assembly, null, out _))
                {
                    _noesis = new NoesisViewCore(
                        ProjectSettings.GlobalizePath(UiXaml), (uint)size.X, (uint)size.Y);
                }
                else
                {
                    GD.PushWarning("[EcsSceneBridge] Noesis native unavailable — XAML overlay disabled.");
                }
            }

            // ImGui first: debug panels claim pointer events before the game UI.
            _runner!.UiInput = (_imgui, _noesis) switch
            {
                ({ } imgui, { } noesis) => new CompositeUiInput(imgui.Input, noesis.Input),
                ({ } imgui, null) => imgui.Input,
                (null, { } noesis) => noesis.Input,
                _ => null,
            };
            _runner.UiUnhandledPointerDown = OnUiUnhandledPointerDown;
        }

        /// <summary>Add the render halves as overlay nodes: ImGui canvas items above the
        /// Noesis texture overlay (same order as the runtime's OverlayPass composition).
        /// The Noesis view core is only kept when its render device initialized — an
        /// unrenderable view would still tick invisibly on the sim thread otherwise.</summary>
        private void AddUiOverlayNodes()
        {
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;

            if (_noesis is { } noesis)
            {
                var overlay = new NoesisTextureOverlay { Name = "NoesisOverlay" };
                if (overlay.TryInitialize(noesis, size))
                {
                    var layer = new CanvasLayer { Name = "NoesisUiLayer", Layer = 90 };
                    layer.AddChild(overlay);
                    AddChild(layer);
                    _noesisOverlay = overlay;
                }
                else
                {
                    overlay.QueueFree();
                    _noesis = null;
                    _runner!.UiInput = _imgui?.Input; // drop the unrenderable half
                }
            }

            if (_imgui is { } imgui)
            {
                var renderer = new ImGuiCanvasRenderer { Name = "ImGuiRenderer" };
                renderer.Initialize(imgui);
                var layer = new CanvasLayer { Name = "ImGuiLayer", Layer = 100 };
                layer.AddChild(renderer);
                AddChild(layer);
            }

            if (_imgui is not null || _noesis is not null)
            {
                GetViewport().SizeChanged += OnViewportResized;
            }
        }

        private void OnViewportResized()
        {
            if (_runner is null) return;
            var size = (Vector2I)GetViewport().GetVisibleRect().Size;
            _runner.EnqueueUiEvent(UiEvent.Resize(size.X, size.Y));
            _noesisOverlay?.OnResize(size);
        }

        /// <summary>The ImGui debug panel — runs ON THE SIM THREAD between NewFrame and
        /// Render, so it reads and mutates sim state directly (Paused is a volatile).</summary>
        private void DrawDebugPanel()
        {
            ImGuiNET.ImGui.Begin("Paradise (Godot)");
            ImGuiNET.ImGui.Text($"entities: {_agents.Count} dynamic ({_ballCount} balls)");
            ImGuiNET.ImGui.Text($"sim: t={_runner!.Now:F2}s latest={_runner.LatestSnapshotTime:F2}s");
            var paused = _runner.Paused;
            if (ImGuiNET.ImGui.Checkbox("Paused", ref paused))
            {
                _runner.Paused = paused;
            }
            ImGuiNET.ImGui.End();
        }

        /// <summary>SIM THREAD — unconsumed pointer-downs fall through to click-to-move.
        /// CollisionWorld is immutable (thread-safe queries) and EnqueueMoveTo is a
        /// ConcurrentQueue push, so no marshaling back to the main thread is needed.</summary>
        private void OnUiUnhandledPointerDown(UiEvent uiEvent)
        {
            if (!_hasPlayer || uiEvent.Button != UiPointerButton.Left ||
                _runner?.CollisionWorld is not { } collision)
            {
                return;
            }

            var input = new RaycastInput
            {
                Start = uiEvent.WorldRayOrigin,
                End = uiEvent.WorldRayOrigin + uiEvent.WorldRayDirection * 1000f,
                Filter = PhysicsLayers.ClickRay,
            };
            if (collision.CastRay(input, out RaycastHit hit))
            {
                _runner.EnqueueMoveTo(_player, hit.Position);
            }
        }

        /// <summary>Mouse events become <see cref="UiEvent"/>s drained on the sim thread: UI
        /// gets first claim (ImGui panels, then Noesis), and unconsumed pointer-downs fall
        /// through to click-to-move via <see cref="OnUiUnhandledPointerDown"/>. Pointer-downs
        /// carry the camera pick ray so the sim needs no camera state — Godot's physics server
        /// is Dummy, so picking runs against the sim's own immutable CollisionWorld.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_runner is null)
            {
                return;
            }

            switch (@event)
            {
                case InputEventMouseMotion motion:
                    _runner.EnqueueUiEvent(UiEvent.PointerMove(motion.Position.X, motion.Position.Y));
                    break;

                case InputEventMouseButton { Pressed: true } down when ToUiButton(down.ButtonIndex) is { } button:
                    if (_camera is not null)
                    {
                        Vector3 origin = _camera.ProjectRayOrigin(down.Position);
                        Vector3 direction = _camera.ProjectRayNormal(down.Position);
                        _runner.EnqueueUiEvent(UiEvent.PointerDown(
                            down.Position.X, down.Position.Y, button, ToSN(origin), ToSN(direction)));
                    }
                    else
                    {
                        _runner.EnqueueUiEvent(new UiEvent(
                            UiEventKind.PointerDown, down.Position.X, down.Position.Y, button,
                            default, default, false));
                    }
                    break;

                case InputEventMouseButton { Pressed: false } up when ToUiButton(up.ButtonIndex) is { } button:
                    _runner.EnqueueUiEvent(UiEvent.PointerUp(up.Position.X, up.Position.Y, button));
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

        public override void _Process(double delta)
        {
            if (_runner is null || _faulted)
            {
                return;
            }

            if (_runner.ThreadException is { } ex)
            {
                GD.PushError($"[EcsSceneBridge] simulation thread faulted: {ex}");
                _faulted = true;
                return;
            }

            // Direct WASD control (camera-relative) — sent to the sim thread; zero when no keys held.
            if (_hasPlayer)
            {
                _runner.SetMoveInput(_player, ToSN(ReadWasdDirection()));
            }

            if (!_runner.HasSnapshots)
            {
                return;
            }

            // Advance the sample time smoothly toward (now − renderDelay), clamped to the latest snapshot.
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
            foreach ((Node3D node, Entity entity) in _agents)
            {
                if (!GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                bool aliveA = worldA.IsAlive(entity);
                bool aliveB = worldB.IsAlive(entity);
                if (!aliveA && !aliveB)
                {
                    continue;
                }

                LocalTransform ta = aliveA ? worldA.GetComponent<LocalTransform>(entity) : worldB.GetComponent<LocalTransform>(entity);
                LocalTransform tb = aliveB ? worldB.GetComponent<LocalTransform>(entity) : ta;

                SN.Vector3 pos = SN.Vector3.Lerp(ta.Position, tb.Position, alpha);
                SN.Quaternion rot = SN.Quaternion.Slerp(ta.Rotation, tb.Rotation, alpha);
                node.GlobalTransform = new Transform3D(new Basis(ToGodot(rot)), ToGodot(pos));
            }
        }

        public override void _ExitTree()
        {
            _runner?.Dispose();
            _runner = null;
        }

        // WASD → a horizontal world-space direction relative to the camera's facing.
        private Vector3 ReadWasdDirection()
        {
            if (_camera is null)
            {
                return Vector3.Zero;
            }

            float forwardBack = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
            float leftRight = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            if (forwardBack == 0f && leftRight == 0f)
            {
                return Vector3.Zero;
            }

            Vector3 forward = -_camera.GlobalBasis.Z;
            forward.Y = 0f;
            Vector3 right = _camera.GlobalBasis.X;
            right.Y = 0f;
            Vector3 dir = forward.Normalized() * forwardBack + right.Normalized() * leftRight;
            return dir.LengthSquared() > 0f ? dir.Normalized() : Vector3.Zero;
        }

        private string ResolveNavMeshPath(Node root)
        {
            if (!string.IsNullOrEmpty(NavMeshFile))
            {
                return NavMeshFile;
            }

            string scenePath = root.SceneFilePath;
            string sceneName = string.IsNullOrEmpty(scenePath)
                ? root.Name
                : System.IO.Path.GetFileNameWithoutExtension(scenePath);
            return $"res://data/scenes/{sceneName}.navmesh.bin";
        }

        private IEnumerable<Node3D> EntityNodes(Node root)
        {
            foreach (Node child in root.GetChildren())
            {
                if (child is Node3D node3D && node3D.HasMeta(EntityGuidMeta))
                {
                    yield return node3D;
                }

                foreach (Node3D descendant in EntityNodes(child))
                {
                    yield return descendant;
                }
            }
        }

        // ---- Static collision harvesting (navigation_source group → Paradise.Physics) ----

        private const string NavigationSourceGroup = "navigation_source";

        private static Paradise.Physics.CollisionWorld? BuildCollisionWorld(Node root)
        {
            var colliders = new List<Collider>();
            var transforms = new List<RigidTransform>();

            foreach (StaticBody3D body in Descendants<StaticBody3D>(root))
            {
                if (!body.IsInGroup(NavigationSourceGroup))
                {
                    continue;
                }

                var filter = new CollisionFilter { BelongsTo = (uint)body.CollisionLayer, CollidesWith = ~0u };
                foreach (CollisionShape3D shapeNode in Descendants<CollisionShape3D>(body))
                {
                    if (shapeNode.Disabled || shapeNode.Shape is null)
                    {
                        continue;
                    }

                    // Fold node scale into the geometry (export contract: runtime shapes carry no scale).
                    SN.Vector3 scale = ToSN(shapeNode.GlobalBasis.Scale);
                    Collider collider;
                    switch (shapeNode.Shape)
                    {
                        case BoxShape3D box:
                            collider = Collider.CreateBox(ColliderScaleFold.BoxSize(ToSN(box.Size), scale) * 0.5f, filter);
                            break;
                        case SphereShape3D sphere:
                            collider = Collider.CreateSphere(ColliderScaleFold.SphereRadius(sphere.Radius, scale), filter);
                            break;
                        case CapsuleShape3D capsule:
                        {
                            float radius = ColliderScaleFold.CapsuleRadius(capsule.Radius, scale);
                            float height = ColliderScaleFold.CapsuleHeight(capsule.Height, scale);
                            collider = Collider.CreateCapsule(radius, MathF.Max(0f, height * 0.5f - radius), filter);
                            break;
                        }
                        default:
                            GD.PushWarning($"[EcsSceneBridge] Unsupported collision shape '{shapeNode.Shape.GetType().Name}' at '{shapeNode.GetPath()}' — skipped.");
                            continue;
                    }

                    colliders.Add(collider);
                    transforms.Add(new RigidTransform(
                        ToSN(shapeNode.GlobalPosition),
                        ToSN(shapeNode.GlobalBasis.Orthonormalized().GetRotationQuaternion())));
                }
            }

            if (colliders.Count == 0)
            {
                GD.PushWarning("[EcsSceneBridge] No static colliders found in the 'navigation_source' group — movement collision disabled.");
                return null;
            }

            return Paradise.Physics.CollisionWorld.Build(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colliders),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(transforms));
        }

        // The authored EntityExport.MoveSpeed — read dynamically because EntityExport is a
        // TOOLS-only type this runtime bridge must not reference at compile time.
        private static float ReadAuthoredMoveSpeed(Node3D playerNode)
        {
            Variant value = playerNode.Get("MoveSpeed");
            float speed = value.VariantType == Variant.Type.Float ? (float)value.AsDouble() : 0f;
            return float.IsFinite(speed) && speed > 0f ? speed : FallbackMoveSpeed;
        }

        private (float Radius, float HalfLength) ReadPlayerCapsule(Node3D playerNode)
        {
            foreach (CollisionShape3D shapeNode in Descendants<CollisionShape3D>(playerNode))
            {
                if (shapeNode.Shape is CapsuleShape3D capsule)
                {
                    // Fold node scale exactly like BuildCollisionWorld does for statics, so a
                    // scaled player node keeps physics dims in sync with its visual capsule.
                    SN.Vector3 scale = ToSN(shapeNode.GlobalBasis.Scale);
                    float radius = ColliderScaleFold.CapsuleRadius(capsule.Radius, scale);
                    float height = ColliderScaleFold.CapsuleHeight(capsule.Height, scale);
                    return (radius, MathF.Max(0f, height * 0.5f - radius));
                }
            }

            return (CharacterRadius, MathF.Max(0f, CharacterHeight * 0.5f - CharacterRadius));
        }

        private float ReadBallRadius(Node3D ballNode)
        {
            foreach (MeshInstance3D meshInstance in Descendants<MeshInstance3D>(ballNode))
            {
                if (meshInstance.Mesh is SphereMesh sphere)
                {
                    // Fold node scale like every other collider read (max horizontal axis).
                    SN.Vector3 scale = ToSN(meshInstance.GlobalBasis.Scale);
                    return ColliderScaleFold.SphereRadius(sphere.Radius, scale);
                }
            }

            return BallRadius;
        }

        private static IEnumerable<T> Descendants<T>(Node node) where T : Node
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is T match)
                {
                    yield return match;
                }

                foreach (T descendant in Descendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private static Camera3D? FindCamera(Node node)
        {
            if (node is Camera3D camera)
            {
                return camera;
            }

            foreach (Node child in node.GetChildren())
            {
                Camera3D? found = FindCamera(child);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private static SN.Vector3 ToSN(Vector3 v) => new(v.X, v.Y, v.Z);
        private static SN.Quaternion ToSN(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
        private static Vector3 ToGodot(SN.Vector3 v) => new(v.X, v.Y, v.Z);
        private static Quaternion ToGodot(SN.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
