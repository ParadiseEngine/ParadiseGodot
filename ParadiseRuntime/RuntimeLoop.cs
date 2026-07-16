using System.Numerics;
using Paradise.ECS;
using Paradise.Physics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using ParadiseGame;
using ParadiseGame.Physics;
using ParadiseGame.Audio;
using ParadiseGame.Ui;
using ParadiseUi;

namespace ParadiseRuntime;

/// <summary>The frame loop shared by windowed and headless modes: samples the 60 Hz sim
/// thread's snapshot pair at (now − 2/60), Lerp/Slerps every simulated instance, and submits
/// one PBR frame. The constants and catch-up rule are copied from EcsSceneBridge — this class
/// is its SDL/engine-renderer twin.</summary>
public sealed class RuntimeLoop : IDisposable
{
    private const double RenderDelaySeconds = 2.0 / 60.0;
    // Move-confirmation sound; the default exists in the Wwise SampleProject banks. The click
    // plays from a dedicated positioned source at the clicked point, so 3D-authored events
    // (e.g. Play_Footsteps with the Positioning banks) pan and attenuate spatially.
    private static readonly string ClickAudioEvent =
        Environment.GetEnvironmentVariable("PARADISE_WWISE_CLICK_EVENT") ?? "Play_Hello";
    // One reused id is fine for a demo cue (a rapid re-click repositions the still-playing
    // instance); real positional SFX should rotate through an id pool for overlapping voices.
    private const ulong ClickAudioSource = 101;
    private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;

    private readonly SimulationRunner _runner;
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly CameraRig _camera;
    private readonly List<RuntimeInstance> _instances;
    private readonly Entity? _player;
    private readonly CollisionWorld? _collisionWorld;
    private readonly IAudioSystem? _audio;

    // ---- pool mini-game (active when the scene authors a "CueBall" dynamic ball) ----
    // Aim/strike/pause/rewind + the ImGui pool panel live in the shared PoolGameController
    // (ParadiseUi) so the .NET and Godot hosts render the identical UI. This class keeps only
    // the render-side pool bits: the per-ball glow lights, collision audio, and pocket count.
    private readonly Entity? _cueBall;
    private readonly PoolGameController? _pool;
    private readonly List<(Entity Entity, int InstanceIndex, int LightIndex, ulong AudioSource)> _poolBalls = new();
    private readonly List<RewoundBall> _scrubScratch = new(); // render-loop rewind scrub (glow); the controller keeps its own
    private readonly float[] _lastBallGlow;
    private readonly double[] _lastBallSoundAt;
    private double _renderClock;
    private readonly float? _animTime;
    private static readonly string CollisionAudioEvent =
        Environment.GetEnvironmentVariable("PARADISE_WWISE_COLLISION_EVENT") ?? "Play_Footsteps";
    private const float BallLightHeight = 0.55f;
    private const float BallLightIntensity = 3.2f;
    private double _renderSampleTime;
    private uint _width;
    private uint _height;

    public RuntimeLoop(
        RuntimeLevel level, WebGpuRenderer renderer, uint width, uint height, bool orthographic, float fovDegrees,
        float? animTime = null, IUiInput? uiInput = null, IAudioSystem? audio = null)
    {
        _audio = audio;
        _animTime = animTime;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _collisionWorld = SceneAssembler.BuildCollisionWorld(level.Level);
        _runner = new SimulationRunner(level.NavigationMesh, _collisionWorld);
        if (audio is not null)
        {
            _runner.Audio = audio.Sink;
        }
        if (uiInput is not null)
        {
            _runner.UiInput = uiInput;
            // Clicks the UI passes through land here ON THE SIM THREAD with the pick ray the
            // render thread attached — same job as TryClickMove, minus the unprojection.
            _runner.UiUnhandledPointerDown = OnUiUnhandledPointerDown;
        }
        _pbr = new PbrRenderer(
            renderer, _width, _height,
            maxAnisotropy: (ushort)Math.Clamp(level.RenderSettings.AnisotropicLevel, 1, 16));
        _pbr.SetSpecularAa(level.RenderSettings.SpecularAaVariance, level.RenderSettings.SpecularAaClamp);

        var assembled = SceneAssembler.Assemble(level, _runner, _pbr);
        _instances = assembled.Instances;
        _player = assembled.Player;
        _cueBall = assembled.CueBall;
        foreach (var instance in _instances)
        {
            _scene.Instances.Add(instance.Render);
            if (instance.Skinned is not null) instance.Skinned.TimeOverride = animTime;
        }

        _camera = new CameraRig(level.Level.Camera, orthographic, fovDegrees);
        // Camera background is the fallback clear; PopulateLighting overrides it with the exported
        // environment background (the sky tone) when one is present.
        if (level.Level.Camera?.BackgroundColor is { } clear)
        {
            _scene.ClearColor = new ColorRgba(clear.R, clear.G, clear.B, 1f);
        }
        SceneAssembler.PopulateLighting(level, _scene); // sets _scene.Bloom from the exported glow

        // Pool controller: shared cue-aim/strike/rewind + ImGui panel. Only when a cue exists.
        // Strikes play the click SFX from this host's audio (kept out of the engine-agnostic controller).
        _pool = _cueBall is { } cue
            ? new PoolGameController(_runner, cue, new CameraProjection(this),
                onStrike: () => _audio?.Sink.PostEvent(ClickAudioEvent, ClickAudioSource))
            : null;

        // Optional dev override of the exported bloom: PARADISE_BLOOM="threshold,knee,intensity".
        if (Environment.GetEnvironmentVariable("PARADISE_BLOOM") is { Length: > 0 } bloomEnv && bloomEnv != "0")
        {
            var p = bloomEnv.Split(',', ' ');
            static float F(string[] a, int i, float d) =>
                i < a.Length && float.TryParse(a[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
            _scene.Bloom = new PbrBloom
            {
                Enabled = true,
                Threshold = F(p, 1, _scene.Bloom.Threshold),
                Knee = F(p, 2, _scene.Bloom.Knee),
                Intensity = F(p, 3, _scene.Bloom.Intensity),
            };
        }

        // One point light per pool ball, driven by its BallGlow every frame (energy 0 = off).
        foreach (var (entity, instanceIndex) in assembled.PoolBalls)
        {
            _poolBalls.Add((entity, instanceIndex, _scene.Lights.Count, (ulong)(200 + _poolBalls.Count)));
            _scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point,
                Position = Vector3.Zero,
                Direction = Vector3.UnitY,
                Color = new Vector3(1f, 0.72f, 0.35f), // warm impact flash
                Intensity = 0f,
                Range = 3.5f,
            });
        }
        _lastBallGlow = new float[_poolBalls.Count];
        _lastBallSoundAt = new double[_poolBalls.Count];

        // Headless/demo hook: strike the cue at startup, e.g. PARADISE_POOL_AUTOSTRIKE="0,-6"
        // (velocity x,z in m/s) — lets screenshots and smoke runs exercise the break.
        if (_cueBall is { } autoCue &&
            Environment.GetEnvironmentVariable("PARADISE_POOL_AUTOSTRIKE") is { } auto)
        {
            var parts = auto.Split(',');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var vx) &&
                float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var vz))
            {
                _runner.EnqueueBallImpulse(autoCue, new Vector3(vx, 0f, vz));
            }
        }
    }

    public bool HasPoolGame => _pool is not null && _poolBalls.Count > 0;

    // ---- pool controller pass-throughs (shared PoolGameController; Program routes mouse here) ----
    // The aim methods run on this render thread (camera access); DrawPoolPanel is registered with
    // the sim-thread ImGui core. No-ops when the scene has no cue ball.
    public bool TryBeginAim(Vector2 screenPixel) => _pool?.TryBeginAim(screenPixel) ?? false;
    public void UpdateAim(Vector2 screenPixel) => _pool?.UpdateAim(screenPixel);
    public void ReleaseAim() => _pool?.ReleaseAim();
    public void DrawPoolPanel() => _pool?.DrawPanel();

    /// <summary>Adapts this loop's CameraRig unprojection to the shared controller's per-host seam.
    /// Reads the loop's live _width/_height/_camera, so it tracks window resizes.</summary>
    private sealed class CameraProjection(RuntimeLoop owner) : IPoolCameraProjection
    {
        public bool TryScreenPointToRay(Vector2 screenPixel, out Vector3 origin, out Vector3 direction)
            => owner.TryScreenRay(screenPixel, out origin, out direction);
        public Vector2 WorldToScreen(Vector3 world) => owner.WorldToScreen(world);
    }

    private bool TryScreenRay(Vector2 screenPixel, out Vector3 origin, out Vector3 direction)
    {
        var camera = _camera.Build(_width / (float)_height);
        var viewProjection = PbrMath.ViewProjection(camera.View, camera.Projection);
        return PbrMath.TryScreenPointToRay(screenPixel, new Vector2(_width, _height), viewProjection, out origin, out direction);
    }

    private Vector2 WorldToScreen(Vector3 world)
    {
        var camera = _camera.Build(_width / (float)_height);
        var viewProjection = PbrMath.ViewProjection(camera.View, camera.Projection);
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (MathF.Abs(clip.W) < 1e-6f) return Vector2.Zero;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return new Vector2((ndc.X * 0.5f + 0.5f) * _width, (1f - (ndc.Y * 0.5f + 0.5f)) * _height);
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
        PostClickSound(hit.Position);
        return true;
    }

    public (Vector3 Forward, Vector3 Right) PlanarBasis() => _camera.PlanarBasis();

    /// <summary>Queue a UI pointer/resize event for the sim thread. Pointer-downs get a world
    /// pick ray attached (computed here, on the render thread, where the camera lives) so the
    /// sim can route unconsumed clicks to world interaction.</summary>
    public void EnqueueUiEvent(UiEventKind kind, Vector2 pixel, UiPointerButton button = UiPointerButton.Left)
    {
        switch (kind)
        {
            case UiEventKind.PointerDown:
            {
                var camera = _camera.Build(_width / (float)_height);
                var viewProjection = PbrMath.ViewProjection(camera.View, camera.Projection);
                var hasRay = PbrMath.TryScreenPointToRay(
                    pixel, new Vector2(_width, _height), viewProjection, out var origin, out var direction);
                _runner.EnqueueUiEvent(hasRay
                    ? UiEvent.PointerDown(pixel.X, pixel.Y, button, origin, direction)
                    : new UiEvent(UiEventKind.PointerDown, pixel.X, pixel.Y, button, default, default, false));
                break;
            }
            case UiEventKind.PointerUp:
                _runner.EnqueueUiEvent(UiEvent.PointerUp(pixel.X, pixel.Y, button));
                break;
            case UiEventKind.Resize:
                _runner.EnqueueUiEvent(UiEvent.Resize(pixel.X, pixel.Y));
                break;
            default:
                _runner.EnqueueUiEvent(UiEvent.PointerMove(pixel.X, pixel.Y));
                break;
        }
    }

    private void PostClickSound(Vector3 worldPosition)
    {
        if (_audio is null) return;
        _audio.Sink.SetSourcePosition(ClickAudioSource, worldPosition);
        _audio.Sink.PostEvent(ClickAudioEvent, ClickAudioSource);
    }

    private void OnUiUnhandledPointerDown(UiEvent uiEvent)
    {
        if (_player is not { } player || _collisionWorld is null || uiEvent.Button != UiPointerButton.Left) return;
        var input = new RaycastInput
        {
            Start = uiEvent.WorldRayOrigin,
            End = uiEvent.WorldRayOrigin + uiEvent.WorldRayDirection * 1000f,
            Filter = PhysicsLayers.ClickRay,
        };
        if (_collisionWorld.CastRay(input, out var hit))
        {
            _runner.EnqueueMoveTo(player, hit.Position);
            PostClickSound(hit.Position);
        }
    }

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
                        Matrix4x4.CreateScale(instance.SimScale)
                        * Matrix4x4.CreateFromQuaternion(rotation)
                        * Matrix4x4.CreateTranslation(position);
                }
            }
        }

        _renderClock += frameDelta;

        // Pool: ball lights track their balls, glow drives light energy + collision audio;
        // while paused-and-scrubbed the recorded frame REPLACES the interpolated display.
        if (_poolBalls.Count > 0)
        {
            var scrub = _pool?.RewindScrub ?? 0;
            var scrubbed = _runner.Paused && scrub > 0 && _runner.TryGetRewindFrame(scrub, _scrubScratch);
            _runner.TrySampleInterpolation(_renderSampleTime, out var glowWorldA, out var glowWorldB, out var glowAlpha);
            var sunk = 0;
            for (var i = 0; i < _poolBalls.Count; i++)
            {
                var (entity, instanceIndex, lightIndex, audioSource) = _poolBalls[i];
                Vector3 position;
                float glow;
                if (scrubbed)
                {
                    var found = false;
                    position = default;
                    glow = 0f;
                    foreach (var ball in _scrubScratch)
                    {
                        if (ball.Entity != entity) continue;
                        position = ball.Position;
                        glow = ball.Glow;
                        _instances[instanceIndex].Render.Model =
                            Matrix4x4.CreateScale(_instances[instanceIndex].SimScale)
                            * Matrix4x4.CreateFromQuaternion(ball.Rotation)
                            * Matrix4x4.CreateTranslation(ball.Position);
                        found = true;
                        break;
                    }
                    if (!found) continue;
                }
                else
                {
                    if (glowWorldA is null || !glowWorldA.IsAlive(entity) || !glowWorldB.IsAlive(entity)) continue;
                    var a = glowWorldA.GetComponent<LocalTransform>(entity).Position;
                    var b = glowWorldB.GetComponent<LocalTransform>(entity).Position;
                    position = Vector3.Lerp(a, b, glowAlpha);
                    glow = float.Lerp(
                        glowWorldA.GetComponent<BallGlow>(entity).Intensity,
                        glowWorldB.GetComponent<BallGlow>(entity).Intensity,
                        glowAlpha);
                    if (glowWorldB.GetComponent<PoolBall>(entity).Sunk != 0) sunk++;
                }

                _scene.Lights[lightIndex] = _scene.Lights[lightIndex] with
                {
                    Position = position + new Vector3(0f, BallLightHeight, 0f),
                    Intensity = glow * BallLightIntensity,
                };

                // Rising glow edge = a fresh hit: positioned impact sound, rate-limited per
                // ball. Scrubbed history is display-only — replaying a recorded spike must not
                // re-fire the sound or pollute the edge tracker.
                if (!scrubbed)
                {
                    if (glow > _lastBallGlow[i] + 0.15f && _renderClock - _lastBallSoundAt[i] > 0.15 && _audio is not null)
                    {
                        _lastBallSoundAt[i] = _renderClock;
                        _audio.Sink.SetSourcePosition(audioSource, position);
                        _audio.Sink.PostEvent(CollisionAudioEvent, audioSource);
                    }
                    _lastBallGlow[i] = glow;
                }
            }
            if (!scrubbed && _pool is not null)
            {
                _pool.SunkCount = sunk;
            }
        }

        // CPU-skinned playback: advance each animated instance's clip and re-upload its
        // private vertex buffers before the frame renders.
        foreach (var instance in _instances)
        {
            instance.Skinned?.Advance(_pbr, (float)frameDelta);
        }

        _scene.Camera = _camera.Build(_width / (float)_height);
        // Drive time-animated procedural materials; pinned by --anim-time for deterministic captures.
        _scene.ElapsedSeconds = _animTime ?? (float)_renderClock;
        if (_audio is not null)
        {
            // The listener rides the camera; per-frame is plenty for a mostly-static rig.
            _audio.Sink.SetListenerPose(_camera.Position, _camera.Forward, Vector3.UnitY);
        }
        _pbr.RenderFrame(_scene);
        _audio?.Pump();
    }

    public void Dispose()
    {
        _runner.Dispose();
        _pbr.Dispose();
        _collisionWorld?.Dispose();
    }
}
