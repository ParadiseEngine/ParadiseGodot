using System;
using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using Paradise.Physics;
using ParadiseExport.Core.Geometry;
using ParadiseGame.Core;
using ParadiseGame.Core.Physics;
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
        [Export] public string BallGroup { get; set; } = "paradise_ball";
        [Export(PropertyHint.File, "*.bin")] public string NavMeshFile { get; set; } = "";
        [Export] public float MoveSpeed { get; set; } = 3.5f;
        [Export] public float AngularSpeed { get; set; } = 720f;
        [Export] public float ArriveRadius { get; set; } = 0.25f;

        // Fallback character capsule dims when the player node has no CapsuleShape3D to read.
        [Export] public float CharacterRadius { get; set; } = 0.4f;
        [Export] public float CharacterHeight { get; set; } = 1.8f;

        // Fallback dynamic-ball params when a ball node has no SphereMesh to read.
        [Export] public float BallRadius { get; set; } = 0.35f;
        [Export] public float BallMass { get; set; } = 1f;

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
                    _player = _runner.SpawnAgent(pos, rot, MoveSpeed, AngularSpeed, ArriveRadius, bodyRadius, bodyHalfLength);
                    _hasPlayer = true;
                    _agents.Add((node, _player));
                }
                else if (node.IsInGroup(BallGroup))
                {
                    Entity ball = _runner.SpawnBall(pos, rot, ReadBallRadius(node), BallMass);
                    _agents.Add((node, ball)); // dynamic: interpolated like the player
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
                // Ground picking runs against the sim's own CollisionWorld (immutable → safe from
                // this thread). Godot's physics server is Dummy, so DirectSpaceState is unavailable.
                if (_runner.CollisionWorld is not { } collision)
                {
                    return;
                }

                Vector3 origin = _camera.ProjectRayOrigin(mouse.Position);
                Vector3 direction = _camera.ProjectRayNormal(mouse.Position);
                var input = new RaycastInput
                {
                    Start = ToSN(origin),
                    End = ToSN(origin + direction * 1000f),
                    Filter = PhysicsLayers.ClickRay,
                };
                if (collision.CastRay(input, out RaycastHit hit))
                {
                    _runner.EnqueueMoveTo(_player, hit.Position);
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
