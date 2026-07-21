using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Ui.Tests;

/// <summary>The host-agnostic pool controller shared by the .NET runtime and the Godot bridge.
/// These pin the slingshot math + pause-staging that both hosts now depend on, using a fake
/// projection so no camera/renderer is needed. The controller sees the cue at the origin
/// (no snapshots ticked), so aim ground points are relative to (0,0,0).</summary>
public class PoolGameControllerTests
{
    /// <summary>A ray straight down from (pixel.X, 5, pixel.Y) hits the y=0 plane at
    /// (pixel.X, 0, pixel.Y) — so a screen pixel maps directly to a ground point. World→screen
    /// projects onto the X/Z plane. Aiming at pixel (0,0) lands exactly on the cue at the origin.</summary>
    private sealed class FakeCamera : IPoolCameraProjection
    {
        public bool TryScreenPointToRay(Vector2 pixel, out Vector3 origin, out Vector3 direction)
        {
            origin = new Vector3(pixel.X, 5f, pixel.Y);
            direction = new Vector3(0f, -1f, 0f);
            return true;
        }

        public Vector2 WorldToScreen(Vector3 world) => new(world.X, world.Z);
    }

    private static (SimulationRunner runner, PoolGameController pool, Entity cue) NewGame(Action? onStrike = null)
    {
        var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(0, 0, 0), Quaternion.Identity, radius: 0.35f);
        var pool = new PoolGameController(runner, cue, new FakeCamera(), onStrike);
        return (runner, pool, cue);
    }

    [Test]
    public async Task strike_fires_opposite_the_drag_with_scaled_speed()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.Paused = true; // stage so the impulse is observable via StagedImpulse
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(3, 0)); // pull the aim point to +x
            pool.ReleaseAim();

            var staged = pool.StagedImpulse;
            await Assert.That(staged.HasValue).IsTrue();
            await Assert.That(staged!.Value.X).IsLessThan(0f); // fires toward -x, OPPOSITE the drag
            // speed = min(|pull|·2.2, 9) = min(3·2.2, 9) = 6.6
            await Assert.That(MathF.Abs(staged.Value.Length() - 6.6f)).IsLessThan(0.01f);
        }
    }

    [Test]
    public async Task strike_speed_is_clamped_to_the_max()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.Paused = true;
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(10, 0)); // |pull|·2.2 = 22 → clamped
            pool.ReleaseAim();

            var staged = pool.StagedImpulse;
            await Assert.That(staged.HasValue).IsTrue();
            await Assert.That(MathF.Abs(staged!.Value.Length() - 9f)).IsLessThan(0.01f); // StrikeMaxSpeed
        }
    }

    [Test]
    public async Task tiny_drag_below_threshold_is_ignored()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.Paused = true;
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(0.05f, 0)); // speed 0.05·2.2 = 0.11 < 0.2
            pool.ReleaseAim();

            await Assert.That(pool.StagedImpulse.HasValue).IsFalse();
        }
    }

    [Test]
    public async Task strike_staged_while_paused_is_applied_on_resume()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.Paused = true;
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(3, 0));
            pool.ReleaseAim();
            await Assert.That(pool.StagedImpulse.HasValue).IsTrue(); // held, not applied

            pool.Paused = false; // resume enqueues the staged strike and clears it
            await Assert.That(pool.StagedImpulse.HasValue).IsFalse();
        }
    }

    [Test]
    public async Task english_is_carried_into_the_staged_strike()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.SpotX = 0.6f; // right english → spin about +Y
            pool.Paused = true;
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(3, 0));
            pool.ReleaseAim();

            await Assert.That(pool.StagedAngular.HasValue).IsTrue();
            await Assert.That(pool.StagedAngular!.Value.Y).IsGreaterThan(1f); // english present as ω.y
        }
    }

    [Test]
    public async Task cue_spot_is_clamped_to_unit_range()
    {
        var (runner, pool, _) = NewGame();
        using (runner)
        {
            pool.SpotX = 5f;
            await Assert.That(pool.SpotX).IsEqualTo(1f);
            pool.SpotY = -5f;
            await Assert.That(pool.SpotY).IsEqualTo(-1f);
            pool.Elevation = 5f;
            await Assert.That(pool.Elevation).IsEqualTo(1f);
        }
    }

    [Test]
    public async Task predicted_path_starts_at_the_cue_and_advances_along_the_strike()
    {
        var (runner, pool, cue) = NewGame();
        using (runner)
        {
            // A leftward strike (aim point to +x → fires toward −x), no collision world: the
            // predicted cue path should start at the cue origin and march in −x.
            var points = new List<Vector3>();
            var impulse = new Vector3(-4f, 0f, 0f);
            var ok = runner.PredictCueBallPath(cue, impulse, System.Numerics.Vector3.Zero, points, maxSteps: 60);

            await Assert.That(ok).IsTrue();
            await Assert.That(points.Count).IsGreaterThan(1);
            await Assert.That(points[0].Length()).IsLessThan(1e-3f);          // starts at the cue (origin)
            await Assert.That(points[^1].X).IsLessThan(-0.05f);                // rolled toward −x
            await Assert.That(MathF.Abs(points[^1].Z)).IsLessThan(1e-3f);      // straight (no english, no walls)
        }
    }

    [Test]
    public async Task immediate_strike_fires_the_audio_hook_and_is_not_staged()
    {
        var struck = false;
        var (runner, pool, _) = NewGame(onStrike: () => struck = true);
        using (runner)
        {
            // Not paused: the strike enqueues immediately and fires the host audio hook.
            await Assert.That(pool.TryBeginAim(new Vector2(0, 0))).IsTrue();
            pool.UpdateAim(new Vector2(3, 0));
            pool.ReleaseAim();

            await Assert.That(struck).IsTrue();
            await Assert.That(pool.StagedImpulse.HasValue).IsFalse();
        }
    }
}
