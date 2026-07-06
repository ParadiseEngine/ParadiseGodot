using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using ParadiseGame;
using ParadiseGame.Physics;

namespace ParadiseRuntime;

/// <summary>The frame loop shared by windowed and headless modes: samples the 60 Hz sim
/// thread's snapshot pair at (now − 2/60), Lerp/Slerps every simulated instance, and submits
/// one PBR frame. The constants and catch-up rule are copied from EcsSceneBridge — this class
/// is its SDL/engine-renderer twin.</summary>
public sealed class RuntimeLoop : IDisposable
{
    private const double RenderDelaySeconds = 2.0 / 60.0;
    private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;

    private readonly SimulationRunner _runner;
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly CameraRig _camera;
    private readonly List<RuntimeInstance> _instances;
    private readonly Entity? _player;
    private readonly CollisionWorld? _collisionWorld;
    private double _renderSampleTime;
    private uint _width;
    private uint _height;

    public RuntimeLoop(
        RuntimeLevel level, WebGpuRenderer renderer, uint width, uint height, bool orthographic, float fovDegrees)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _collisionWorld = SceneAssembler.BuildCollisionWorld(level.Level);
        _runner = new SimulationRunner(level.NavigationMesh, _collisionWorld);
        _pbr = new PbrRenderer(
            renderer, _width, _height,
            maxAnisotropy: (ushort)Math.Clamp(level.RenderSettings.AnisotropicLevel, 1, 16));
        _pbr.SetSpecularAa(level.RenderSettings.SpecularAaVariance, level.RenderSettings.SpecularAaClamp);

        var assembled = SceneAssembler.Assemble(level, _runner, _pbr);
        _instances = assembled.Instances;
        _player = assembled.Player;
        foreach (var instance in _instances)
        {
            _scene.Instances.Add(instance.Render);
        }

        _camera = new CameraRig(level.Level.Camera, orthographic, fovDegrees);
        SceneAssembler.PopulateLighting(level, _scene);
        if (level.Level.Camera?.BackgroundColor is { } clear)
        {
            _scene.ClearColor = new ColorRgba(clear.R, clear.G, clear.B, 1f);
        }
    }

    public CollisionWorld? CollisionWorld => _collisionWorld;
    public bool HasPlayer => _player is not null;
    public int InstanceCount => _instances.Count;

    /// <summary>Start the 60 Hz simulation thread (spawns already happened on this thread).</summary>
    public void Start() => _runner.Start();

    public void SetMoveInput(Vector3 planarDirection)
    {
        if (_player is { } player) _runner.SetMoveInput(player, planarDirection);
    }

    /// <summary>Click-to-move: unproject the pixel, ray-cast the static world on the click
    /// filter, path the player to the hit point. The EcsSceneBridge flow minus Godot.</summary>
    public bool TryClickMove(Vector2 screenPixel)
    {
        if (_player is not { } player || _collisionWorld is null) return false;

        var camera = _camera.Build(_width / (float)_height);
        var viewProjection = PbrMath.ViewProjection(camera.View, camera.Projection);
        if (!PbrMath.TryScreenPointToRay(screenPixel, new Vector2(_width, _height), viewProjection, out var origin, out var direction))
            return false;

        var input = new RaycastInput
        {
            Start = origin,
            End = origin + direction * 1000f,
            Filter = PhysicsLayers.ClickRay,
        };
        if (!_collisionWorld.CastRay(input, out var hit)) return false;

        _runner.EnqueueMoveTo(player, hit.Position);
        return true;
    }

    public (Vector3 Forward, Vector3 Right) PlanarBasis() => _camera.PlanarBasis();

    public void Resize(uint width, uint height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr.Resize(_width, _height);
    }

    /// <summary>One render frame; throws if the sim thread died (its exception rethrown).</summary>
    public void RenderFrame(double frameDelta)
    {
        if (_runner.ThreadException is { } fault)
            throw new InvalidOperationException("Simulation thread faulted.", fault);

        if (_runner.HasSnapshots)
        {
            var target = Math.Min(_runner.Now - RenderDelaySeconds, _runner.LatestSnapshotTime);
            _renderSampleTime = _renderSampleTime <= 0.0 ? target : Math.Min(_renderSampleTime + frameDelta, target);
            if (target - _renderSampleTime > MaxRenderSampleLagSeconds)
            {
                _renderSampleTime = target;
            }

            if (_runner.TrySampleInterpolation(_renderSampleTime, out var worldA, out var worldB, out var alpha))
            {
                alpha = Math.Clamp(alpha, 0f, 1f);
                foreach (var instance in _instances)
                {
                    if (instance.SimEntity is not { } entity) continue;
                    if (!worldA.IsAlive(entity) || !worldB.IsAlive(entity)) continue;
                    var a = worldA.GetComponent<LocalTransform>(entity);
                    var b = worldB.GetComponent<LocalTransform>(entity);
                    var position = Vector3.Lerp(a.Position, b.Position, alpha);
                    var rotation = Quaternion.Slerp(a.Rotation, b.Rotation, alpha);
                    instance.Render.Model =
                        Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                }
            }
        }

        _scene.Camera = _camera.Build(_width / (float)_height);
        _pbr.RenderFrame(_scene);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _pbr.Dispose();
        _collisionWorld?.Dispose();
    }
}
