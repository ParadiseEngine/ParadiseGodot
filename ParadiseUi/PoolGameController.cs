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

    // Aim-trail preview (controller-local like the strike tuning above): how far ahead to roll
    // the deterministic sim (120 ticks = 2 s at 60 Hz) and the max points cached for the polyline.
    private const int MaxPredictSteps = 120;
    private const int MaxTrailPoints = MaxPredictSteps + 1;

    // Cue-spot widget geometry (pixels) — the little cue-ball disc you click to set english.
    private const float CueSpotRadius = 34f;

    private readonly SimulationRunner _runner;
    private readonly Entity _cueBall;
    private readonly IPoolCameraProjection _camera;
    private readonly Action? _onStrike; // fired when a strike is applied immediately (not staged) — host audio hook

    private readonly List<RewoundBall> _scrubScratch = new();
    private (Vector3 Impulse, float Spin)? _staged;   // strike captured while paused, applied on resume
    private bool _aiming;
    private Vector3 _aimGroundPoint;
    private volatile bool _aimVisible;
    private Vector2 _aimBallScreen;
    private Vector2 _aimPointScreen;
    private volatile int _rewindScrub; // frames back shown while paused (0 = present)

    // Sidespin ("english"), −1 (left) … +1 (right). Written by the cue-spot widget on the sim
    // thread (DrawPanel), read by the aim methods on the render thread — floats can't be volatile,
    // so it's stored as int bits behind a volatile field.
    private volatile int _englishBits;

    /// <summary>Sidespin ("english") applied to the next strike, −1 (left) … +1 (right). Set by
    /// the cue-spot widget; also settable by a host (e.g. a headless auto-strike). Clamped.</summary>
    public float English
    {
        get => BitConverter.Int32BitsToSingle(_englishBits);
        set => _englishBits = BitConverter.SingleToInt32Bits(Math.Clamp(value, -1f, 1f));
    }

    // Predicted cue-ball trail: world points rolled out on the render thread, projected to screen
    // pixels, and drawn by the sim-thread panel. Same volatile-guard pattern as the aim endpoints.
    private readonly List<Vector3> _trailWorld = new(MaxTrailPoints);
    private readonly Vector2[] _trailScreen = new Vector2[MaxTrailPoints];
    private volatile int _trailCount;
    private volatile bool _trailVisible;

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
    public Vector3? StagedImpulse => _staged?.Impulse;
    public float? StagedSpin => _staged?.Spin;
    public void ClearStagedImpulse() => _staged = null;

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
                if (_staged is { } staged)
                {
                    _runner.EnqueueBallImpulse(_cueBall, staged.Impulse, staged.Spin);
                    _staged = null;
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
            ImGuiNET.ImGui.TextWrapped(_staged is { } s
                ? $"staged strike: {s.Impulse.Length():F1} m/s — resumes with it"
                : "drag from the white ball to stage a strike");
        }
        else
        {
            ImGuiNET.ImGui.TextWrapped("drag from the white ball to strike; pause to rewind");
        }

        DrawCueSpot();

        if (SunkCount > 0)
        {
            ImGuiNET.ImGui.Text($"pocketed: {SunkCount}");
        }
        ImGuiNET.ImGui.End();

        // Predicted cue-ball trail: thin translucent polyline + a ghost circle where it ends,
        // drawn UNDER the crisp white aim line so the aim direction still reads clearly.
        if (_trailVisible)
        {
            var draw = ImGuiNET.ImGui.GetForegroundDrawList();
            int count = _trailCount;
            for (int i = 1; i < count; i++)
            {
                draw.AddLine(_trailScreen[i - 1], _trailScreen[i], 0x90FFE04Eu, 1.6f); // translucent cyan
            }
            for (int i = 0; i < count; i += 6) // dotted feel
            {
                draw.AddCircleFilled(_trailScreen[i], 1.8f, 0xC0FFE04Eu);
            }
            if (count > 0)
            {
                draw.AddCircle(_trailScreen[count - 1], 6f, 0xC0FFE04Eu, 0, 2f); // ghost ball
            }
        }

        if (_aimVisible)
        {
            var draw = ImGuiNET.ImGui.GetForegroundDrawList();
            draw.AddLine(_aimBallScreen, _aimPointScreen, 0xE0FFFFFFu, 2.5f);
            draw.AddCircleFilled(_aimPointScreen, 5f, 0xE04E82FFu);
        }
    }

    /// <summary>The 2D cue-spot widget: a cue-ball disc you click/drag left↔right to set english
    /// (horizontal offset only — this is sidespin). Right-click recenters. An immediate-mode ImGui
    /// widget like the panel's Checkbox/SliderInt, so it works from the sim-thread draw callback.</summary>
    private void DrawCueSpot()
    {
        ImGuiNET.ImGui.Text("English (sidespin)");
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        float diameter = CueSpotRadius * 2f;
        ImGuiNET.ImGui.InvisibleButton("##cuespot", new Vector2(diameter, diameter));
        var center = new Vector2(origin.X + CueSpotRadius, origin.Y + CueSpotRadius);

        float english = English;
        if (ImGuiNET.ImGui.IsItemActive()) // click or drag on the disc
        {
            float mouseX = ImGuiNET.ImGui.GetMousePos().X;
            english = Math.Clamp((mouseX - center.X) / (CueSpotRadius - 6f), -1f, 1f);
            English = english;
        }
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiNET.ImGuiMouseButton.Right))
        {
            english = 0f;
            English = 0f;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, CueSpotRadius, 0xFFE8E8E8u);          // cue ball
        dl.AddCircle(center, CueSpotRadius, 0xFF404040u, 0, 2f);          // outline
        dl.AddLine(new Vector2(center.X, center.Y - CueSpotRadius + 3f),  // vertical center guide
                   new Vector2(center.X, center.Y + CueSpotRadius - 3f), 0x30404040u, 1f);
        var spot = new Vector2(center.X + english * (CueSpotRadius - 6f), center.Y);
        dl.AddCircleFilled(spot, 6f, 0xFFF04040u);                        // contact spot (blue-ish in ABGR)
        ImGuiNET.ImGui.Text($"english: {english:+0.00;-0.00; 0.00}");
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

        UpdatePredictedTrail();
    }

    /// <summary>Roll the tentative strike forward through the real solver and cache the cue's
    /// predicted path as screen pixels for <see cref="DrawPanel"/>. Render thread (camera access).</summary>
    private void UpdatePredictedTrail()
    {
        if (!TryComputeStrike(out var previewImpulse))
        {
            _trailVisible = false;
            return;
        }
        // While paused-and-scrubbed, predict from the scrubbed frame so the preview matches the
        // staged strike that will apply on resume (0 = predict from the live present).
        int framesBack = _runner.Paused && _rewindScrub > 0 ? _rewindScrub : 0;
        if (!_runner.PredictCueBallPath(_cueBall, previewImpulse, English, _trailWorld, MaxPredictSteps, framesBack))
        {
            _trailVisible = false;
            return;
        }
        int count = Math.Min(_trailWorld.Count, MaxTrailPoints);
        for (int i = 0; i < count; i++)
        {
            _trailScreen[i] = _camera.WorldToScreen(_trailWorld[i]);
        }
        _trailCount = count;
        _trailVisible = true;
    }

    /// <summary>The slingshot impulse from the current aim (cue fires OPPOSITE the drag, speed
    /// scaled by pull length and clamped). False inside the dead-zone. Shared by the trail
    /// preview and the actual release so both agree exactly.</summary>
    private bool TryComputeStrike(out Vector3 impulse)
    {
        impulse = default;
        var ballPos = DisplayedCuePosition();
        var pull = ballPos - _aimGroundPoint;
        pull.Y = 0;
        var speed = MathF.Min(pull.Length() * StrikePowerScale, StrikeMaxSpeed);
        if (speed < 0.2f) return false;
        impulse = Vector3.Normalize(pull) * speed;
        return true;
    }

    /// <summary>Slingshot release: the cue fires OPPOSITE the drag, speed scaled by drag
    /// length, carrying the dialed-in english. Applied immediately while running, staged while paused.</summary>
    public void ReleaseAim()
    {
        if (!_aiming) return;
        _aiming = false;
        _aimVisible = false;
        _trailVisible = false;
        if (!TryComputeStrike(out var impulse)) return;
        var spin = English;
        if (_runner.Paused)
        {
            _staged = (impulse, spin);
        }
        else
        {
            _runner.EnqueueBallImpulse(_cueBall, impulse, spin);
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
