using System;
using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Export.Geometry;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Physics;
using Paradise.Ui;
using Paradise.Ui.Noesis.Host;
using ParadiseGodot.Runtime.Ui;
using Paradise.Sample.Ui;
using SN = System.Numerics;
// The source-generated `World` alias only exists inside Paradise.Sample.Pool (per-assembly generator
// output); this assembly names the closed generic type explicitly.
using SimWorld = Paradise.ECS.World<Paradise.ECS.SmallBitSet<uint>, Paradise.Sample.Pool.GameConfig>;

namespace ParadiseGodot.Runtime
{
    /// <summary>
    /// Runtime bridge between the threaded simulation and Godot. At <c>_Ready</c> it turns the live scene's
    /// marked entities into ECS entities in a <see cref="SimulationRunner"/> and starts the sim thread
    /// (fixed 60 Hz). Godot renders on its own thread by <b>interpolating between the two latest immutable
    /// world snapshots</b> in <c>_Process</c> — smooth regardless of the render frame rate. Left-click drag
    /// aims and strikes the cue ball; pointer events are queued to the sim thread.
    ///
    /// Entities are identified by the runtime-safe <c>paradise_entity_guid</c> node metadata (the editor-only
    /// EntityExport script is absent at runtime). Right-handed throughout.
    /// Node writes happen only on Godot's main thread (<c>_Process</c>); the sim never touches Godot.
    /// </summary>
    public partial class EcsSceneBridge : Node3D
    {
        [Export] public string EntityGuidMeta { get; set; } = "paradise_entity_guid";
        [Export] public string BallGroup { get; set; } = "paradise_ball";

        // Fallback dynamic-ball params when a ball node has no SphereMesh to read.
        [Export] public float BallRadius { get; set; } = 0.35f;
        [Export] public float BallMass { get; set; } = 1f;

        [ExportGroup("UI")]
        // Dear ImGui debug panel (sim-thread immediate mode, rendered as canvas items).
        [Export] public bool EnableImGui { get; set; } = true;
        // NoesisGUI overlay XAML (empty = no Noesis). Rendered on a headless WebGPU device and
        // composited as a premultiplied-alpha texture overlay.
        //
        // Point this at the AUTHORING source (res://ui/…), not the exported copy: play mode loads
        // the file straight off disk via ProjectSettings.GlobalizePath, so it needs no export step
        // and picks up edits immediately. The export pipeline separately stages res://ui/** into
        // <data>/ui/ (UiAssetExporter) for the standalone runtime, which ships only data/.
        [Export(PropertyHint.File, "*.xaml")] public string UiXaml { get; set; } = "";

        // Render sampling: interpolate ~2 sim ticks behind the latest snapshot; skip ahead if we fall too far.
        private const double RenderDelaySeconds = 2.0 / 60.0;
        private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;

        private SimulationRunner? _runner;
        private Camera3D? _camera;
        // Scale is captured at spawn and re-applied every frame: the sim owns position + rotation
        // but NOT scale, so we must not let the snapshot transform wipe the node's authored scale.
        private readonly List<(Node3D Node, Entity Entity, Vector3 Scale)> _agents = new();
        private readonly List<Entity> _poolBallEntities = new(); // balls only, for the sunk-count read
        private readonly List<(Sprite3D Sprite, Entity Entity)> _sprites = new();
        private readonly List<ParticleEmitterView> _emitters = new();

        /// <summary>One emitter's render half: the MultiMesh the bridge refills from snapshot
        /// particle pools each frame, plus the presentation config (sizes, flipbook) that the
        /// sim deliberately does not carry.</summary>
        private sealed class ParticleEmitterView
        {
            public required MultiMeshInstance3D Node;
            public required Entity Entity;
            public required int Capacity;
            public required bool SpriteKind;
            public required float StartSize;
            public required float EndSize;
            public required int FrameCount;
            public required float Fps;
        }
        private PoolGameController? _pool; // shared cue-aim/strike/rewind + ImGui panel (when a CueBall exists)
        private double _renderSampleTime;
        private bool _faulted;
        private int _ballCount;
        private ImGuiUiCore? _imgui;
        private NoesisViewCore? _noesis;
        private NoesisTextureOverlay? _noesisOverlay;

        public override void _Ready()
        {
            Node root = Owner ?? GetParent() ?? this;

            // Static collision geometry for the sim's stateless CollisionWorld — every StaticBody3D
            // in the scene (the .NET host's `Rigidbody.BodyType == Static` harvest): the solid
            // geometry the balls rest on / bounce off (the pool table bed + cushions).
            Paradise.Physics.CollisionWorld? collisionWorld = BuildCollisionWorld(root);
            _runner = new SimulationRunner(collisionWorld);
            _camera = FindCamera(root);

            // Global solver tuning + static-surface bounce, read live from the SAME paradise/physics/*
            // project settings the .NET host exports — so our shared sim runs identical inputs in
            // both hosts. Static restitution mirrors SceneAssembler.StaticSurfaceRestitution (the
            // bounciest Obstacle-layer surface, e.g. the pool cushions).
            PhysicsTuning tuning = ReadPhysicsTuning();
            float staticRestitution = ReadStaticSurfaceRestitution(
                root, GetPhysicsSetting("paradise/physics/default_static_restitution", 0.4f));

            // Pocket capture regions (Area3D sphere triggers) → each ball gets a PoolBall via the
            // shared PoolRack.BuildBall, so balls sink in Godot exactly as in the .NET host.
            List<(SN.Vector3 Center, float Radius)> pockets = ExtractPockets(root);
            int trayIndex = 0;

            Entity? cueBall = null;
            foreach (Node3D node in EntityNodes(root))
            {
                SN.Vector3 pos = ToSN(node.GlobalPosition);
                SN.Quaternion rot = ToSN(node.GlobalBasis.GetRotationQuaternion());
                Vector3 scale = node.GlobalBasis.Scale;

                if (node.IsInGroup(BallGroup))
                {
                    // Feed our sim the AUTHORED physics params (EntityExport Body* fields, read
                    // dynamically since EntityExport is a tools-only type) exactly as the .NET host
                    // does via SceneAssembler: same mass/damping/restitution + global tuning + the
                    // pocket set → identical roll/bounce AND pocket capture in both hosts.
                    (float mass, float damping, float restitution) = ReadBallBody(node);
                    // The cue ball drives the pool controller (same "CueBall" id the .NET host uses).
                    bool isCue = string.Equals(node.Name, "CueBall", StringComparison.OrdinalIgnoreCase);
                    PocketConfig poolBall = PoolRack.BuildBall(pockets, isCue, pos, trayIndex++);
                    Entity ball = _runner.SpawnBall(pos, rot, ReadBallRadius(node), mass,
                        damping, restitution, staticRestitution, poolBall, tuning,
                        friction: GetFloat(node, "BodyFriction", 0.3f));
                    _agents.Add((node, ball, scale)); // dynamic: interpolated like the player
                    _poolBallEntities.Add(ball);
                    _ballCount++;
                    if (isCue)
                    {
                        cueBall = ball;
                    }
                }
                else
                {
                    // Sprite animations and particle emitters spawn their own sim entities
                    // (independent features — a node may author both); anything else is scenery.
                    bool special = false;
                    if (FirstSprite3D(node) is { } sprite)
                    {
                        _sprites.Add((sprite, _runner.SpawnSpriteAnimation(pos, rot,
                            ReadFloat(node, "SpriteFps", 10f),
                            ReadInt(node, "SpriteFrameCount", 0) is var authored && authored > 0
                                ? authored
                                : Math.Max(1, sprite.Hframes * sprite.Vframes),
                            ReadBool(node, "SpriteLoop", true))));
                        special = true;
                    }
                    if (ReadInt(node, "ParticleKind", 0) > 0)
                    {
                        SetupParticleEmitter(node, pos, rot);
                        special = true;
                    }
                    if (!special)
                    {
                        _runner.SpawnStatic(pos, rot);
                    }
                }
            }

            // Pool mini-game: the shared controller renders the identical aim line + panel as the
            // .NET host. Aim methods run on this main thread (camera access); DrawPanel on the sim
            // thread via ImGui. No audio hook (the bridge wires no AudioSink) → silent strike.
            if (cueBall is { } cue && _camera is not null)
            {
                _pool = new PoolGameController(_runner, cue, new Camera3DProjection(_camera));
            }

            SetupUi(); // UiInput must be composed before Start (the sim reads it each tick)

            _runner.Start();
            GD.Print($"[EcsSceneBridge] Simulation thread started — {_agents.Count} dynamic entit(ies).");

            AddUiOverlayNodes();
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
                    if (_pool is not null)
                    {
                        _imgui.AddDraw(_pool.DrawPanel); // shared "Pool" panel + aim line
                    }
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
                    // Pool HUD bindings: when the scene has a pool game, the overlay VM is the
                    // XAML's DataContext, refreshed on the SIM thread each tick (Noesis pins
                    // binding updates to the view's creation thread) from the same SunkCount /
                    // Paused the ImGui panel shows.
                    PoolGameController? pool = _pool;
                    PoolOverlayViewModel? vm = pool is null ? null : new PoolOverlayViewModel(_ballCount);
                    _noesis = new NoesisViewCore(
                        ProjectSettings.GlobalizePath(UiXaml), (uint)size.X, (uint)size.Y,
                        vm, vm is null ? null : () => vm.Refresh(pool!.SunkCount, pool.Paused));
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

        /// <summary>Mouse events become <see cref="UiEvent"/>s drained on the sim thread: UI
        /// gets first claim (ImGui panels, then Noesis). Pointer-downs carry the camera pick ray
        /// so the sim needs no camera state — Godot's physics server is Dummy, so picking runs
        /// against the sim's own immutable CollisionWorld.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_runner is null)
            {
                return;
            }

            switch (@event)
            {
                case InputEventMouseMotion motion:
                    // Aim update runs HERE on the main thread (camera access); the controller caches
                    // the screen-space endpoints its sim-thread DrawPanel reads.
                    _pool?.UpdateAim(new SN.Vector2(motion.Position.X, motion.Position.Y));
                    _runner.EnqueueUiEvent(UiEvent.PointerMove(motion.Position.X, motion.Position.Y));
                    break;

                case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(0f, wheelUp.Factor));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(0f, -wheelDown.Factor));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelRight, Pressed: true } wheelRight:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(wheelRight.Factor, 0f));
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelLeft, Pressed: true } wheelLeft:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(-wheelLeft.Factor, 0f));
                    break;

                // A precise pointing device (MacBook trackpad, Magic Mouse) never produces the
                // wheel buttons above: Godot's macOS backend routes any scroll that carries a
                // gesture phase to a pan gesture instead, with the delta negated relative to the
                // wheel axes (+delta.y is a scroll DOWN).
                case InputEventPanGesture pan:
                    _runner.EnqueueUiEvent(UiEvent.Scroll(pan.Delta.X, -pan.Delta.Y));
                    break;

                case InputEventMouseButton { Pressed: true } down when ToUiButton(down.ButtonIndex) is { } button:
                    // The cue ball claims a left-click first (start aiming); if it does, the click is
                    // consumed and does NOT fall through to the UI/click-move path (parity with Program).
                    if (button == UiPointerButton.Left &&
                        _pool?.TryBeginAim(new SN.Vector2(down.Position.X, down.Position.Y)) == true)
                    {
                        break;
                    }
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
                    if (button == UiPointerButton.Left)
                    {
                        _pool?.ReleaseAim(); // slingshot fire (or stage while paused)
                    }
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

            // Pocketed-ball count for the shared pool panel (the sim parks + flags sunk balls).
            if (_pool is not null && _poolBallEntities.Count > 0)
            {
                int sunk = 0;
                foreach (Entity ball in _poolBallEntities)
                {
                    if (worldB.IsAlive(ball) && worldB.GetComponent<BallSunk>(ball).Value != 0) sunk++;
                }
                _pool.SunkCount = sunk;
            }

            alpha = Math.Clamp(alpha, 0f, 1f);
            foreach ((Node3D node, Entity entity, Vector3 scale) in _agents)
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

                var wa = aliveA ? worldA : worldB;
                var wb = aliveB ? worldB : wa;

                SN.Vector3 pos = SN.Vector3.Lerp(
                    wa.GetComponent<Position>(entity).Value, wb.GetComponent<Position>(entity).Value, alpha);
                SN.Quaternion rot = SN.Quaternion.Slerp(
                    wa.GetComponent<Rotation>(entity).Value, wb.GetComponent<Rotation>(entity).Value, alpha);
                // Sim owns position + rotation only — re-apply the authored scale so the snapshot
                // transform doesn't reset the node to unit scale (which ballooned the pool balls).
                node.GlobalTransform = new Transform3D(new Basis(ToGodot(rot)).Scaled(scale), ToGodot(pos));
            }

            DriveSprites(worldA, worldB, alpha);
            DriveParticles(worldA, worldB, alpha);
        }

        public override void _ExitTree()
        {
            _runner?.Dispose();
            _runner = null;
        }

        // ---- sprite animations + particle emitters (sim-driven presentation) ----

        /// <summary>Spawn the emitter's sim entity and build its render half: a MultiMesh of
        /// unit quads (Sprite kind, particle-billboarded with flipbook animation) or unit boxes
        /// (Voxel kind), refilled from snapshot particle pools in <see cref="_Process"/>.
        /// Presentation config is read from the authored EntityExport properties (present in
        /// editor play mode; exported builds fall back to defaults, like MoveSpeed).</summary>
        private void SetupParticleEmitter(Node3D node, SN.Vector3 pos, SN.Quaternion rot)
        {
            bool spriteKind = ReadInt(node, "ParticleKind", 0) != 2;
            int capacity = Math.Clamp(ReadInt(node, "ParticleMaxCount", 64), 1, ParticleState.MaxParticles);
            int columns = Math.Max(1, ReadInt(node, "ParticleSheetColumns", 1));
            int rows = Math.Max(1, ReadInt(node, "ParticleSheetRows", 1));
            int authoredFrames = ReadInt(node, "ParticleSheetFrameCount", 0);
            int frameCount = Math.Clamp(authoredFrames <= 0 ? columns * rows : authoredFrames, 1, columns * rows);
            Color color = ReadColor(node, "ParticleColor", Colors.White);

            Entity entity = _runner!.SpawnParticleEmitter(pos, rot, new ParticleConfig(
                ReadFloat(node, "ParticleEmitRate", 8f),
                ReadFloat(node, "ParticleLifetime", 1.5f),
                ReadFloat(node, "ParticleSpeed", 2f),
                Mathf.DegToRad(ReadFloat(node, "ParticleSpreadDegrees", 25f)),
                ReadFloat(node, "ParticleGravity", -9.8f),
                ReadFloat(node, "ParticleDrag", 0f),
                capacity),
                unchecked((uint)ReadInt(node, "ParticleSeed", 1)));

            Material material;
            PrimitiveMesh mesh;
            if (spriteKind)
            {
                var standard = new StandardMaterial3D
                {
                    // Particle billboard: the shader faces the camera and reads INSTANCE_CUSTOM.z
                    // as the flipbook phase — the bridge writes (frame + 0.5) / frameCount so the
                    // shader lands exactly on the sim's frame index (loop already applied sim-side).
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    BillboardKeepScale = true,
                    ParticlesAnimHFrames = columns,
                    ParticlesAnimVFrames = rows,
                    ParticlesAnimLoop = false,
                    // Alpha blend, matching the .NET host's Blend material (its shader has no
                    // cutout/mask path).
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor = color,
                };
                string sheet = ReadString(node, "ParticleSheet", "");
                if (!string.IsNullOrEmpty(sheet) && ResourceLoader.Load<Texture2D>(sheet) is { } texture)
                {
                    standard.AlbedoTexture = texture;
                }
                material = standard;
                mesh = new QuadMesh { Size = Vector2.One };
            }
            else
            {
                material = new StandardMaterial3D { AlbedoColor = color };
                mesh = new BoxMesh { Size = Vector3.One };
            }
            mesh.Material = material;

            var instance = new MultiMeshInstance3D
            {
                Name = $"{node.Name}Particles",
                // Particle positions are WORLD space — opt out of any parent transform.
                TopLevel = true,
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseCustomData = spriteKind,
                    Mesh = mesh,
                    InstanceCount = capacity,
                    VisibleInstanceCount = 0,
                },
            };
            AddChild(instance);
            instance.GlobalTransform = Transform3D.Identity;

            _emitters.Add(new ParticleEmitterView
            {
                Node = instance,
                Entity = entity,
                Capacity = capacity,
                SpriteKind = spriteKind,
                StartSize = ReadFloat(node, "ParticleStartSize", 0.25f),
                EndSize = ReadFloat(node, "ParticleEndSize", 0.25f),
                FrameCount = frameCount,
                Fps = ReadFloat(node, "ParticleSheetFps", 0f),
            });
        }

        /// <summary>Flipbook frames from interpolated snapshot time — the SAME sampling rule the
        /// sim used (<see cref="SpriteAnimationSystem.SampleFrame"/>), so the shown frame can
        /// never disagree with the .NET host's.</summary>
        private void DriveSprites(SimWorld worldA, SimWorld worldB, float alpha)
        {
            foreach ((Sprite3D sprite, Entity entity) in _sprites)
            {
                if (!GodotObject.IsInstanceValid(sprite) || !worldB.IsAlive(entity))
                {
                    continue;
                }

                float bTime = worldB.GetComponent<SpriteTime>(entity).Value;
                SpriteConfig cfg = worldB.GetComponent<SpriteConfig>(entity);
                float time = worldA.IsAlive(entity)
                    ? float.Lerp(worldA.GetComponent<SpriteTime>(entity).Value, bTime, alpha)
                    : bTime;
                sprite.Frame = SpriteAnimationSystem.SampleFrame(time, cfg.Fps, cfg.FrameCount, cfg.Loop != 0);
            }
        }

        /// <summary>Refill each emitter's MultiMesh from the snapshot particle pools: live slots
        /// interpolate position/age between the pair (slot-stable buffers), sizes lerp over the
        /// particle's life, and Sprite-kind instances carry the flipbook phase in CUSTOM.z.</summary>
        private void DriveParticles(SimWorld worldA, SimWorld worldB, float alpha)
        {
            foreach (ParticleEmitterView view in _emitters)
            {
                if (!GodotObject.IsInstanceValid(view.Node) || !worldB.IsAlive(view.Entity))
                {
                    continue;
                }

                ParticleState a = worldA.IsAlive(view.Entity)
                    ? worldA.GetComponent<ParticleState>(view.Entity)
                    : worldB.GetComponent<ParticleState>(view.Entity);
                ParticleState b = worldB.GetComponent<ParticleState>(view.Entity);
                MultiMesh multimesh = view.Node.Multimesh;

                int visible = 0;
                for (int slot = 0; slot < view.Capacity; slot++)
                {
                    Particle pb = b.Particles[slot];
                    if (pb.Lifetime <= 0f)
                    {
                        continue;
                    }

                    // Slot reuse guard: if the slot died and respawned between the snapshots
                    // (older age in the LATER world), interpolating would sweep across the world.
                    Particle pa = a.Particles[slot];
                    SN.Vector3 position;
                    float age;
                    if (pa.Lifetime > 0f && pa.Age <= pb.Age)
                    {
                        position = SN.Vector3.Lerp(pa.Position, pb.Position, alpha);
                        age = float.Lerp(pa.Age, pb.Age, alpha);
                    }
                    else
                    {
                        position = pb.Position;
                        age = pb.Age;
                    }

                    float life01 = Math.Clamp(age / pb.Lifetime, 0f, 1f);
                    float size = float.Lerp(view.StartSize, view.EndSize, life01);
                    multimesh.SetInstanceTransform(visible,
                        new Transform3D(Basis.FromScale(new Vector3(size, size, size)), ToGodot(position)));
                    if (view.SpriteKind)
                    {
                        int frame = SpriteAnimationSystem.SampleParticleFrame(
                            age, pb.Lifetime, view.Fps, view.FrameCount);
                        multimesh.SetInstanceCustomData(visible,
                            new Color(0f, 0f, (frame + 0.5f) / view.FrameCount, 0f));
                    }
                    visible++;
                }

                multimesh.VisibleInstanceCount = visible;
            }
        }

        private Sprite3D? FirstSprite3D(Node3D node)
        {
            foreach (Sprite3D sprite in Descendants<Sprite3D>(node))
            {
                return sprite;
            }

            return null;
        }

        // Authored EntityExport properties, read dynamically like ReadAuthoredMoveSpeed
        // (EntityExport is TOOLS-only; absent property → fallback).
        private static float ReadFloat(Node3D node, string property, float fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.Float && float.IsFinite((float)value.AsDouble())
                ? (float)value.AsDouble()
                : fallback;
        }

        private static int ReadInt(Node3D node, string property, int fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.Int ? value.AsInt32() : fallback;
        }

        private static bool ReadBool(Node3D node, string property, bool fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.Bool ? value.AsBool() : fallback;
        }

        private static Color ReadColor(Node3D node, string property, Color fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.Color ? value.AsColor() : fallback;
        }

        private static string ReadString(Node3D node, string property, string fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.String ? value.AsString() : fallback;
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

        /// <summary>Pocket capture regions: every Area3D sphere trigger → (world center, scaled
        /// radius). The Godot-native analog of SceneAssembler.ExtractPockets (which keys on the
        /// exported IsTrigger sphere colliders). Area3D is never a StaticBody3D, so pockets stay
        /// out of the solid collision world.</summary>
        private static List<(SN.Vector3 Center, float Radius)> ExtractPockets(Node root)
        {
            var pockets = new List<(SN.Vector3, float)>();
            foreach (Area3D area in Descendants<Area3D>(root))
            {
                foreach (CollisionShape3D shapeNode in Descendants<CollisionShape3D>(area))
                {
                    if (shapeNode.Disabled || shapeNode.Shape is not SphereShape3D sphere) continue;
                    SN.Vector3 scale = ToSN(shapeNode.GlobalBasis.Scale);
                    pockets.Add((ToSN(shapeNode.GlobalPosition),
                        ColliderScaleFold.SphereRadius(sphere.Radius, scale)));
                }
            }
            return pockets;
        }

        // ---- Static collision harvesting (every StaticBody3D → Paradise.Physics) ----

        private static Paradise.Physics.CollisionWorld? BuildCollisionWorld(Node root)
        {
            var colliders = new List<Collider>();
            var transforms = new List<RigidTransform>();

            // Every StaticBody3D — the Godot analog of the .NET host's `BodyType == Static` harvest.
            // NOT filtered by the navigation_source group: that group marks nav-BAKE surfaces (the
            // floor), but the pool table bed + cushions are solid StaticBody3D geometry too and must
            // collide even though they are not walkable. Area3D pockets are triggers → not StaticBody3D
            // → naturally excluded (matches the .NET IsTrigger skip).
            foreach (StaticBody3D body in Descendants<StaticBody3D>(root))
            {
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
                GD.PushWarning("[EcsSceneBridge] No StaticBody3D colliders found — solid collision disabled.");
                return null;
            }

            return Paradise.Physics.CollisionWorld.Build(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colliders),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(transforms));
        }

        private float ReadBallRadius(Node3D ballNode)
        {
            // Read the radius from the COLLISION shape (a SphereShape3D), folded by node scale —
            // exactly what the .NET host does (SceneAssembler: collider.Radius * LocalScale, e.g.
            // 0.5 * 0.4 = 0.2). The pool balls are an imported glb, NOT a SphereMesh, so the old
            // mesh path fell through to the 0.35 fallback: at the ~0.4 rack spacing that made every
            // ball overlap its neighbours, and the sim depenetrated them explosively at t=0 (the
            // rack "split" apart) — a scatter the .NET host never showed because it uses 0.2.
            foreach (CollisionShape3D shapeNode in Descendants<CollisionShape3D>(ballNode))
            {
                if (shapeNode.Shape is SphereShape3D sphere)
                {
                    SN.Vector3 scale = ToSN(shapeNode.GlobalBasis.Scale);
                    return ColliderScaleFold.SphereRadius(sphere.Radius, scale);
                }
            }

            return BallRadius;
        }

        /// <summary>Authored dynamic-ball physics (EntityExport Body* fields, read dynamically).
        /// Fallbacks match the EntityExport <c>[Export]</c> defaults, so an unset field behaves
        /// exactly as the exporter would write it. Mirrors the .NET host's Rigidbody read.</summary>
        private (float Mass, float Damping, float Restitution) ReadBallBody(Node3D ballNode) => (
            MathF.Max(0.01f, GetFloat(ballNode, "BodyMass", BallMass)),
            GetFloat(ballNode, "BodyLinearDamping", 0f),
            GetFloat(ballNode, "BodyRestitution", 0.2f));

        /// <summary>Global solver tuning from <c>paradise/physics/*</c> — the same project settings
        /// the .NET host exports. Missing keys fall back to <see cref="PhysicsTuning.Default"/>.</summary>
        private static PhysicsTuning ReadPhysicsTuning()
        {
            PhysicsTuning d = PhysicsTuning.Default;
            return new PhysicsTuning(
                GetPhysicsSetting("paradise/physics/min_speed", d.MinSpeed),
                GetPhysicsSetting("paradise/physics/skin", d.Skin),
                GetPhysicsSetting("paradise/physics/push_strength", d.PushStrength),
                new SN.Vector3(0f, GetPhysicsSetting("paradise/physics/gravity_y", d.Gravity.Y), 0f),
                GetPhysicsSetting("paradise/physics/static_friction", d.StaticFriction),
                GetPhysicsSetting("paradise/physics/min_angular_speed", d.MinAngularSpeed));
        }

        /// <summary>The bounciest Obstacle-layer static surface (the cushions/frames), else the
        /// fallback. Shares the max/fallback reduction with the .NET host via
        /// <see cref="StaticSurfaces.BounceRestitution"/>; this only gathers the surfaces from live
        /// nodes (restitution is authored on the body's owning EntityExport).</summary>
        private static float ReadStaticSurfaceRestitution(Node root, float fallback) =>
            StaticSurfaces.BounceRestitution(GatherStaticSurfaces(root), fallback);

        private static IEnumerable<StaticSurfaces.Surface> GatherStaticSurfaces(Node root)
        {
            foreach (StaticBody3D body in Descendants<StaticBody3D>(root))
            {
                if (body.GetParent() is Node owner)
                {
                    yield return new StaticSurfaces.Surface(
                        GetFloat(owner, "BodyRestitution", 0f), (uint)body.CollisionLayer);
                }
            }
        }

        private static float GetFloat(Node node, string property, float fallback)
        {
            Variant value = node.Get(property);
            return value.VariantType == Variant.Type.Float ? (float)value.AsDouble() : fallback;
        }

        private static float GetPhysicsSetting(string key, float fallback) =>
            (float)ProjectSettings.GetSetting(key, fallback).AsDouble();

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

        /// <summary>The pool controller's per-host projection seam, over a Godot <see cref="Camera3D"/>.
        /// Only ever called from the aim methods on the main thread (Godot objects aren't thread-safe).</summary>
        private sealed class Camera3DProjection : Paradise.Sample.Ui.IPoolCameraProjection
        {
            private readonly Camera3D _camera;
            public Camera3DProjection(Camera3D camera) => _camera = camera;

            public bool TryScreenPointToRay(SN.Vector2 screenPixel, out SN.Vector3 origin, out SN.Vector3 direction)
            {
                var pixel = new Vector2(screenPixel.X, screenPixel.Y);
                origin = ToSN(_camera.ProjectRayOrigin(pixel));
                direction = ToSN(_camera.ProjectRayNormal(pixel)); // already normalized by Godot
                return true;
            }

            public SN.Vector2 WorldToScreen(SN.Vector3 world)
            {
                var w = ToGodot(world);
                if (_camera.IsPositionBehind(w)) return SN.Vector2.Zero;
                var s = _camera.UnprojectPosition(w);
                return new SN.Vector2(s.X, s.Y);
            }
        }
    }
}
