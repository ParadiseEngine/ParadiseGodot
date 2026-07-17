using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using ParadiseGame;

namespace ParadiseUi;

/// <summary>
/// Screen↔world projection — the ONLY per-host seam of the pool game. The .NET host implements it
/// over its <c>CameraRig</c> + <c>PbrMath</c>; the Godot host over <c>Camera3D</c>. Both are called
/// only from the aim methods, which run on the host's main/render thread (where the camera lives).
/// </summary>
public interface IPoolCameraProjection
{
    /// <summary>Unproject a screen pixel to a world ray. False if the pixel can't be unprojected.</summary>
    bool TryScreenPointToRay(Vector2 screenPixel, out Vector3 origin, out Vector3 direction);

    /// <summary>Project a world point to a screen pixel (top-left origin, y-down).</summary>
    Vector2 WorldToScreen(Vector3 world);
}

/// <summary>
/// Host-agnostic pool mini-game controller: cue pick, slingshot aim, strike, pause + rewind scrub,
/// and the "Pool" ImGui panel with the aim line. Shared verbatim between the .NET runtime and the
/// Godot play-mode bridge so both render the identical UI from one source — each host only supplies
/// an <see cref="IPoolCameraProjection"/> and routes pointer events + registers <see cref="DrawPanel"/>.
///
/// Lives in ParadiseUi (not the engine-agnostic ParadiseGame) because <see cref="DrawPanel"/> draws
/// with ImGui; the sim types come from ParadiseGame, which ParadiseUi references.
///
/// Threading: the aim methods run on the host main/render thread (camera access) and cache the aim
/// endpoints into volatile-guarded fields; <see cref="DrawPanel"/> runs on the sim thread and reads
/// only those cached points + the (thread-safe) <see cref="SimulationRunner"/> — never the camera.
/// </summary>
public sealed class PoolGameController
{
    // Slingshot tuning: pull length → cue speed, clamped. (Copied from the original RuntimeLoop.)
    private const float StrikePowerScale = 2.2f;
    private const float StrikeMaxSpeed = 9f;

    private readonly SimulationRunner _runner;
    private readonly Entity _cueBall;
    private readonly IPoolCameraProjection _camera;
    private readonly Action? _onStrike; // fired when a strike is applied immediately (not staged) — host audio hook

    private readonly List<RewoundBall> _scrubScratch = new();
    private Vector3? _stagedImpulse;   // strike captured while paused, applied on resume
    private bool _aiming;
    private Vector3 _aimGroundPoint;
    private volatile bool _aimVisible;
    private Vector2 _aimBallScreen;
    private Vector2 _aimPointScreen;
    private volatile int _rewindScrub; // frames back shown while paused (0 = present)

    public PoolGameController(SimulationRunner runner, Entity cueBall, IPoolCameraProjection camera, Action? onStrike = null)
    {
        _runner = runner;
        _cueBall = cueBall;
        _camera = camera;
        _onStrike = onStrike;
    }

    private volatile int _sunkCount;

    /// <summary>Pocketed-ball count, fed by the host that captures pockets (Godot leaves it 0).
    /// Written from the host render/_Process thread, read by the sim-thread panel — volatile so
    /// the "pocketed: N" label doesn't read a stale value (auto-properties can't be volatile).</summary>
    public int SunkCount { get => _sunkCount; set => _sunkCount = value; }

    public int RewindFrameCount => _runner.RewindFrameCount;
    public int RewindScrub { get => _rewindScrub; set => _rewindScrub = Math.Max(0, value); }
    public Vector3? StagedImpulse => _stagedImpulse;
    public void ClearStagedImpulse() => _stagedImpulse = null;

    /// <summary>Pause state; resuming applies a pending rewind-restore and staged strike
    /// (call from the sim thread — the ImGui panel does).</summary>
    public bool Paused
    {
        get => _runner.Paused;
        set
        {
            if (!value && _runner.Paused)
            {
                if (_rewindScrub > 0)
                {
                    if (!_runner.RestoreFromRewind(_rewindScrub))
                    {
                        // Nothing was rewound (transient pin pressure) — keep the scrub and the
                        // staged strike, stay paused, and let the player resume again.
                        Console.WriteLine("[Pool] rewind restore did not apply — try resuming again.");
                        return;
                    }
                    _rewindScrub = 0;
                }
                if (_stagedImpulse is { } staged)
                {
                    _runner.EnqueueBallImpulse(_cueBall, staged);
                    _stagedImpulse = null;
                }
            }
            _runner.Paused = value;
        }
    }

    /// <summary>The "Pool" ImGui panel + aim line — runs on the SIM thread via ImGui core AddDraw,
    /// so it reads and mutates sim state directly and only reads the cached aim endpoints.</summary>
    public void DrawPanel()
    {
        ImGuiNET.ImGui.Begin("Pool");
        var paused = Paused;
        if (ImGuiNET.ImGui.Checkbox("Paused", ref paused))
        {
            Paused = paused;
        }
        if (paused)
        {
            var scrub = _rewindScrub;
            var max = Math.Max(0, _runner.RewindFrameCount - 1);
            if (ImGuiNET.ImGui.SliderInt("Rewind", ref scrub, 0, max, scrub == 0 ? "now" : $"-{scrub} frames"))
            {
                _rewindScrub = Math.Clamp(scrub, 0, max);
            }
            ImGuiNET.ImGui.TextWrapped(_stagedImpulse is { } s
                ? $"staged strike: {s.Length():F1} m/s — resumes with it"
                : "drag from the white ball to stage a strike");
        }
        else
        {
            ImGuiNET.ImGui.TextWrapped("drag from the white ball to strike; pause to rewind");
        }
        if (SunkCount > 0)
        {
            ImGuiNET.ImGui.Text($"pocketed: {SunkCount}");
        }
        ImGuiNET.ImGui.End();

        if (_aimVisible)
        {
            var draw = ImGuiNET.ImGui.GetForegroundDrawList();
            draw.AddLine(_aimBallScreen, _aimPointScreen, 0xE0FFFFFF, 2.5f);
            draw.AddCircleFilled(_aimPointScreen, 5f, 0xE04E82FF);
        }
    }

    // ---- aiming (host main/render thread — camera access) ----

    /// <summary>Begin a strike if the pointer ray hits the cue ball (its DISPLAYED position,
    /// so aiming works mid-scrub too). True = consumed, skip UI/click-move routing.</summary>
    public bool TryBeginAim(Vector2 screenPixel)
    {
        if (!_camera.TryScreenPointToRay(screenPixel, out var origin, out var direction)) return false;
        var ballPos = DisplayedCuePosition();
        // Ray-sphere with a forgiving radius (pick comfort).
        var toBall = ballPos - origin;
        var along = Vector3.Dot(toBall, direction);
        if (along <= 0) return false;
        var closest = origin + direction * along;
        if (Vector3.Distance(closest, ballPos) > 0.6f) return false;
        _aiming = true;
        UpdateAim(screenPixel);
        return true;
    }

    public void UpdateAim(Vector2 screenPixel)
    {
        if (!_aiming) return;
        var ballPos = DisplayedCuePosition();
        if (_camera.TryScreenPointToRay(screenPixel, out var origin, out var direction) &&
            MathF.Abs(direction.Y) > 1e-5f)
        {
            var t = (ballPos.Y - origin.Y) / direction.Y;
            if (t > 0)
            {
                _aimGroundPoint = origin + direction * t;
            }
        }
        _aimBallScreen = _camera.WorldToScreen(ballPos);
        _aimPointScreen = _camera.WorldToScreen(_aimGroundPoint);
        _aimVisible = true;
    }

    /// <summary>Slingshot release: the cue fires OPPOSITE the drag, speed scaled by drag
    /// length. Applied immediately while running, staged while paused.</summary>
    public void ReleaseAim()
    {
        if (!_aiming) return;
        _aiming = false;
        _aimVisible = false;
        var ballPos = DisplayedCuePosition();
        var pull = ballPos - _aimGroundPoint;
        pull.Y = 0;
        var speed = MathF.Min(pull.Length() * StrikePowerScale, StrikeMaxSpeed);
        if (speed < 0.2f) return;
        var impulse = Vector3.Normalize(pull) * speed;
        if (_runner.Paused)
        {
            _stagedImpulse = impulse;
        }
        else
        {
            _runner.EnqueueBallImpulse(_cueBall, impulse);
            _onStrike?.Invoke();
        }
    }

    private Vector3 DisplayedCuePosition()
    {
        if (_runner.Paused && _rewindScrub > 0 && _runner.TryGetRewindFrame(_rewindScrub, _scrubScratch))
        {
            foreach (var ball in _scrubScratch)
            {
                if (ball.Entity == _cueBall) return ball.Position;
            }
        }
        if (_runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _) && latest.IsAlive(_cueBall))
        {
            return latest.GetComponent<LocalTransform>(_cueBall).Position;
        }
        return Vector3.Zero;
    }
}
