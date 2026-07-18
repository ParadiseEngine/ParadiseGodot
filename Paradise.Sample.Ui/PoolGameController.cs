using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Game;

namespace Paradise.Sample.Ui;

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
/// Lives in Paradise.Sample.Ui (not the engine-agnostic Paradise.Sample.Game) because <see cref="DrawPanel"/> draws
/// with ImGui; the sim types come from Paradise.Sample.Game, which Paradise.Sample.Ui references.
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

    // Cue-spot widget geometry (pixels) — the cue-ball disc you click for contact point.
    private const float CueSpotRadius = 34f;

    // Spin mapping: a contact offset of 1 (edge of ball) at 1 m/s produces this much angular
    // velocity (rad/s). Grounded in ω = (5·speed/2r)·offset for a solid sphere; folds the ~1/r.
    private const float SpinPerContactOffset = 12f;
    // Elevated-cue downward pop → jump/hop, as a fraction of strike speed per unit elevation.
    private const float JumpScale = 0.5f;

    private readonly SimulationRunner _runner;
    private readonly Entity _cueBall;
    private readonly IPoolCameraProjection _camera;
    private readonly Action? _onStrike; // fired when a strike is applied immediately (not staged) — host audio hook

    private readonly List<RewoundBall> _scrubScratch = new();
    private (Vector3 Impulse, Vector3 Angular)? _staged;   // strike captured while paused, applied on resume
    private bool _aiming;
    private Vector3 _aimGroundPoint;
    private volatile bool _aimVisible;
    private Vector2 _aimBallScreen;
    private Vector2 _aimPointScreen;
    private volatile int _rewindScrub; // frames back shown while paused (0 = present)

    // Cue contact spot (−1..1 each): X = side english, Y = top(+)/back(−) spin; plus cue elevation
    // (0..1) for masse/jump. Set by the widget on the sim thread, read by the aim methods on the
    // render thread — floats can't be volatile, so each is stored as int bits behind volatile fields.
    private volatile int _spotXBits;
    private volatile int _spotYBits;
    private volatile int _elevationBits;

    /// <summary>Horizontal cue contact offset — side "english", −1 (left) … +1 (right).</summary>
    public float SpotX { get => Bits(_spotXBits); set => _spotXBits = ClampBits(value, -1f, 1f); }
    /// <summary>Vertical cue contact offset — top(+, follow) … bottom(−, draw).</summary>
    public float SpotY { get => Bits(_spotYBits); set => _spotYBits = ClampBits(value, -1f, 1f); }
    /// <summary>Cue elevation 0..1 — masse curve + jump/hop. Settable by a host (headless strike).</summary>
    public float Elevation { get => Bits(_elevationBits); set => _elevationBits = ClampBits(value, 0f, 1f); }

    private static float Bits(int b) => BitConverter.Int32BitsToSingle(b);
    private static int ClampBits(float v, float lo, float hi) => BitConverter.SingleToInt32Bits(Math.Clamp(v, lo, hi));

    // Predicted cue-ball trail: world points rolled out on the render thread, projected to screen
    // pixels, and drawn by the sim-thread panel. Same volatile-guard pattern as the aim endpoints.
    private readonly List<Vector3> _trailWorld = new(MaxTrailPoints);
    private readonly Vector2[] _trailScreen = new Vector2[MaxTrailPoints];
    private volatile int _trailCount;
    private volatile bool _trailVisible;

    // Throttle for the lock-held rollout: re-run PredictCueBallPath only when the aim actually
    // moved; otherwise reuse the cached world path and just re-project it (cheap, no lock) so a
    // camera move still tracks. Reset at TryBeginAim so a fresh aim always recomputes.
    private Vector3 _lastPredictGround;
    private Vector3 _lastPredictSpot;
    private int _lastPredictFrames = -1;

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
    public Vector3? StagedAngular => _staged?.Angular;
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
                    _runner.EnqueueBallImpulse(_cueBall, staged.Impulse, staged.Angular);
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

    /// <summary>The 2D cue-spot widget: a cue-ball disc you click/drag anywhere to set the contact
    /// point — horizontal = side english, vertical = top(follow)/bottom(draw) — plus an elevation
    /// slider for masse/jump. Right-click recenters. Immediate-mode ImGui like the panel's
    /// Checkbox/SliderInt, so it works from the sim-thread draw callback.</summary>
    private void DrawCueSpot()
    {
        ImGuiNET.ImGui.Text("Cue contact (spin)");
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        float diameter = CueSpotRadius * 2f;
        ImGuiNET.ImGui.InvisibleButton("##cuespot", new Vector2(diameter, diameter));
        var center = new Vector2(origin.X + CueSpotRadius, origin.Y + CueSpotRadius);
        float reach = CueSpotRadius - 6f;

        float sx = SpotX, sy = SpotY;
        if (ImGuiNET.ImGui.IsItemActive()) // click or drag anywhere on the disc
        {
            Vector2 m = ImGuiNET.ImGui.GetMousePos();
            sx = Math.Clamp((m.X - center.X) / reach, -1f, 1f);
            sy = Math.Clamp((center.Y - m.Y) / reach, -1f, 1f); // screen-up = +Y = top spin
            SpotX = sx;
            SpotY = sy;
        }
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiNET.ImGuiMouseButton.Right))
        {
            sx = 0f; sy = 0f;
            SpotX = 0f; SpotY = 0f;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, CueSpotRadius, 0xFFE8E8E8u); // cue ball
        dl.AddCircle(center, CueSpotRadius, 0xFF404040u, 0, 2f); // outline
        dl.AddLine(new Vector2(center.X - CueSpotRadius + 3f, center.Y),
                   new Vector2(center.X + CueSpotRadius - 3f, center.Y), 0x30404040u, 1f);
        dl.AddLine(new Vector2(center.X, center.Y - CueSpotRadius + 3f),
                   new Vector2(center.X, center.Y + CueSpotRadius - 3f), 0x30404040u, 1f);
        var spot = new Vector2(center.X + sx * reach, center.Y - sy * reach);
        dl.AddCircleFilled(spot, 6f, 0xFFF04040u); // contact spot
        ImGuiNET.ImGui.Text($"spin x:{sx:+0.0;-0.0; 0.0} y:{sy:+0.0;-0.0; 0.0}");

        float elev = Elevation;
        if (ImGuiNET.ImGui.SliderFloat("Elevation", ref elev, 0f, 1f, "%.2f"))
        {
            Elevation = elev;
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
        _lastPredictFrames = -1; // force a fresh rollout: the world may have moved since the last aim
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
        if (!TryComputeStrike(out var previewImpulse, out var previewAngular))
        {
            _trailVisible = false;
            return;
        }
        // While paused-and-scrubbed, predict from the scrubbed frame so the preview matches the
        // staged strike that will apply on resume (0 = predict from the live present).
        int framesBack = _runner.Paused && _rewindScrub > 0 ? _rewindScrub : 0;
        Vector3 spot = new(SpotX, SpotY, Elevation);
        // Only pay the lock-held rollout when the aim genuinely changed (~1 cm of ground point,
        // or spot/elevation/scrub). Otherwise reuse the cached world path — projection below still
        // runs every call, so camera motion tracks without re-simulating.
        bool changed = framesBack != _lastPredictFrames
            || Vector3.DistanceSquared(spot, _lastPredictSpot) > 2.5e-5f
            || Vector3.DistanceSquared(_aimGroundPoint, _lastPredictGround) > 1e-4f;
        if (changed)
        {
            if (!_runner.PredictCueBallPath(_cueBall, previewImpulse, previewAngular, _trailWorld, MaxPredictSteps, framesBack))
            {
                _trailVisible = false;
                return;
            }
            _lastPredictGround = _aimGroundPoint;
            _lastPredictSpot = spot;
            _lastPredictFrames = framesBack;
        }
        else if (_trailWorld.Count < 2)
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
    private bool TryComputeStrike(out Vector3 impulse, out Vector3 angular)
    {
        impulse = default;
        angular = default;
        var ballPos = DisplayedCuePosition();
        var pull = ballPos - _aimGroundPoint;
        pull.Y = 0;
        var speed = MathF.Min(pull.Length() * StrikePowerScale, StrikeMaxSpeed);
        if (speed < 0.2f) return false;
        Vector3 d = Vector3.Normalize(pull); // horizontal travel direction (fires OPPOSITE the drag)
        impulse = d * speed;

        // Cue contact point → spin: side offset spins about vertical (english); vertical offset
        // spins about Up×d (top=follow, bottom=draw). ω = SpinPerContactOffset·speed·offset.
        float sx = SpotX, sy = SpotY, elev = Elevation;
        Vector3 up = Vector3.UnitY;
        Vector3 topAxis = Vector3.Cross(up, d);
        angular = SpinPerContactOffset * speed * (sx * up + sy * topAxis);
        if (elev > 0f)
        {
            // Elevated cue: english becomes a curve about the travel axis (masse) + a downward pop
            // (jump/hop) — both then play out through gravity + friction in the solver.
            angular += (SpinPerContactOffset * speed * elev * sx) * d;
            impulse -= up * (elev * speed * JumpScale);
        }
        return true;
    }

    /// <summary>Slingshot release: the cue fires OPPOSITE the drag, speed scaled by drag length,
    /// carrying the dialed-in spin (english/draw/follow/masse/jump). Applied immediately while
    /// running, staged while paused.</summary>
    public void ReleaseAim()
    {
        if (!_aiming) return;
        _aiming = false;
        _aimVisible = false;
        _trailVisible = false;
        if (!TryComputeStrike(out var impulse, out var angular)) return;
        if (_runner.Paused)
        {
            _staged = (impulse, angular);
        }
        else
        {
            _runner.EnqueueBallImpulse(_cueBall, impulse, angular);
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
