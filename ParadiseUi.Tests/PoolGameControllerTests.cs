using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using ParadiseGame;
using ParadiseGame.Navigation;
using ParadiseUi;

namespace ParadiseUi.Tests;

/// <summary>The host-agnostic pool controller shared by the .NET runtime and the Godot bridge.
/// These pin the slingshot math + pause-staging that both hosts now depend on, using a fake
/// projection so no camera/renderer is needed. The controller sees the cue at the origin
/// (no snapshots ticked), so aim ground points are relative to (0,0,0).</summary>
public class PoolGameControllerTests
{
    // Navmesh is never exercised (no agents) — a stub keeps ParadiseUi.Tests off the Detour package.
    private sealed class NoNavMesh : INavigationMesh
    {
        public IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to) => Array.Empty<Vector3>();
    }

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
        var runner = new SimulationRunner(new NoNavMesh());
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
