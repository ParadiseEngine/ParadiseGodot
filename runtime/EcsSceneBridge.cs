using System;
using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using ParadiseGame.Core;
using ParadiseGame.Navigation.Detour;
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
        [Export(PropertyHint.File, "*.bin")] public string NavMeshFile { get; set; } = "";
        [Export] public float MoveSpeed { get; set; } = 3.5f;
        [Export] public float AngularSpeed { get; set; } = 720f;
        [Export] public float ArriveRadius { get; set; } = 0.25f;

        // For headless / no-input smoke runs: if set, the player is sent here on _Ready.
        [Export] public bool AutoDemo { get; set; }
        [Export] public Vector3 AutoDemoTarget { get; set; }

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

            _runner = new SimulationRunner(DetourNavMeshLoader.LoadFromBytes(navBytes));
            _camera = FindCamera(root);

            foreach (Node3D node in EntityNodes(root))
            {
                SN.Vector3 pos = ToSN(node.GlobalPosition);
                SN.Quaternion rot = ToSN(node.GlobalBasis.GetRotationQuaternion());

                if (node.IsInGroup(PlayerGroup))
                {
                    _player = _runner.SpawnAgent(pos, rot, MoveSpeed, AngularSpeed, ArriveRadius);
                    _hasPlayer = true;
                    _agents.Add((node, _player));
                }
                else
                {
                    _runner.SpawnStatic(pos, rot);
                }
            }

            _runner.Start();
            GD.Print($"[EcsSceneBridge] Simulation thread started — {_agents.Count} agent(s). Click the ground to move.");

            if (AutoDemo && _hasPlayer)
            {
                _runner.EnqueueMoveTo(_player, ToSN(AutoDemoTarget));
            }
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_runner is null || _camera is null || !_hasPlayer)
            {
                return;
            }

            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse)
            {
                Vector3 origin = _camera.ProjectRayOrigin(mouse.Position);
                Vector3 direction = _camera.ProjectRayNormal(mouse.Position);
                var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * 1000f);
                Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
                if (hit.Count > 0)
                {
                    var point = (Vector3)hit["position"];
                    _runner.EnqueueMoveTo(_player, ToSN(point));
                }
            }
        }

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
