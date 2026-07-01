using System.Collections.Generic;
using Godot;
using Paradise.ECS;
using ParadiseGame.Core;
using ParadiseGame.Core.Navigation;
using ParadiseGame.Navigation.Detour;
using SN = System.Numerics;

namespace ParadiseGodot.Runtime
{
    /// <summary>
    /// Runtime bridge: at play time it turns the live scene's marked entities into ECS entities in the
    /// shared <see cref="GameSimulation"/> (ParadiseGame.Core), drives the navmesh-follow system each
    /// physics tick, and writes the results back onto the Godot nodes. Click the ground to move the
    /// player agent. The BankHeist <c>UnityClickToMoveController</c> analog.
    ///
    /// Presentation-only: it touches the shared ECS world DIRECTLY (raw <see cref="Entity"/> handles,
    /// <c>World.CreateEntity</c>, <c>World.GetComponent</c>, <see cref="NavigationPlanner"/>) — no
    /// wrapper. Not a <c>[Tool]</c> script and lives outside <c>addons/</c>, so it compiles into the
    /// game build and stays inert in the editor. Entities are identified by the runtime-safe
    /// <c>paradise_entity_guid</c> node metadata (the editor-only EntityExport script is absent at
    /// runtime); the player is identified by group membership. Right-handed throughout.
    /// </summary>
    public partial class EcsSceneBridge : Node3D
    {
        [Export] public string EntityGuidMeta { get; set; } = "paradise_entity_guid";
        [Export] public string PlayerGroup { get; set; } = "paradise_player";
        // Exported DotRecast navmesh (res:// path). Empty → derived from the scene name:
        // res://data/scenes/<Scene>.navmesh.bin.
        [Export(PropertyHint.File, "*.bin")] public string NavMeshFile { get; set; } = "";
        [Export] public float MoveSpeed { get; set; } = 3.5f;
        [Export] public float AngularSpeed { get; set; } = 720f;
        [Export] public float ArriveRadius { get; set; } = 0.25f;

        // For headless / no-input smoke runs: if set, the player is sent here on _Ready.
        [Export] public bool AutoDemo { get; set; }
        [Export] public Vector3 AutoDemoTarget { get; set; }

        private GameSimulation? _sim;
        private Camera3D? _camera;
        private readonly List<(Node3D Node, Entity Entity)> _agents = new();
        private Entity _player;
        private bool _hasPlayer;

        public override void _Ready()
        {
            Node root = Owner ?? GetParent() ?? this;

            // Load the exported DotRecast navmesh (.bin) directly — no dependency on Godot's
            // NavigationRegion3D. The engine runtime loads the same file the same way.
            string navPath = ResolveNavMeshPath(root);
            byte[] navBytes = Godot.FileAccess.GetFileAsBytes(navPath);
            if (navBytes.Length == 0)
            {
                GD.PushWarning($"[EcsSceneBridge] NavMesh '{navPath}' not found or empty (re-save the scene to export it) — agent movement disabled.");
                return;
            }

            _sim = new GameSimulation(DetourNavMeshLoader.LoadFromBytes(navBytes));
            _camera = FindCamera(root);

            foreach (Node3D node in EntityNodes(root))
            {
                SN.Vector3 pos = ToSN(node.GlobalPosition);
                SN.Quaternion rot = ToSN(node.GlobalBasis.GetRotationQuaternion());

                if (node.IsInGroup(PlayerGroup))
                {
                    Entity agent = _sim.World.CreateEntity(EntityBuilder.Create()
                        .Add(new LocalTransform(pos, rot))
                        .Add(new NavAgent(MoveSpeed, AngularSpeed, ArriveRadius))
                        .Add(new NavPath())
                        .Add(new SimulationContext())
                        .AddTag(default(PlayerControlled)));
                    _agents.Add((node, agent));
                    _player = agent;
                    _hasPlayer = true;
                }
                else
                {
                    _sim.World.CreateEntity(EntityBuilder.Create().Add(new LocalTransform(pos, rot)));
                }
            }

            GD.Print($"[EcsSceneBridge] Simulation ready — {_agents.Count} agent(s). Click the ground to move.");

            if (AutoDemo && _hasPlayer)
            {
                NavigationPlanner.PlanMoveTo(_sim.World, _player, ToSN(AutoDemoTarget), _sim.NavigationMesh);
            }
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_sim is null || _camera is null || !_hasPlayer)
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
                    NavigationPlanner.PlanMoveTo(_sim.World, _player, ToSN(point), _sim.NavigationMesh);
                }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_sim is null)
            {
                return;
            }

            _sim.Tick((float)delta);

            foreach ((Node3D node, Entity entity) in _agents)
            {
                if (!GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                LocalTransform transform = _sim.World.GetComponent<LocalTransform>(entity);
                node.GlobalTransform = new Transform3D(new Basis(ToGodot(transform.Rotation)), ToGodot(transform.Position));
            }
        }

        public override void _ExitTree()
        {
            _sim?.Dispose();
            _sim = null;
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
