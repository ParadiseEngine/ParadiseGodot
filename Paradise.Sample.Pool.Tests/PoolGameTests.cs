using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;
using Paradise.Windowing;

namespace Paradise.Sample.Pool.Tests;

/// <summary>The pool-game sim mechanics, driven synchronously through TickOnce: strike
/// impulses move the cue ball, ball↔ball hits light the glow which decays and dies with the
/// motion, and the rewind buffer can restore a past frame whose future then diverges under a
/// different strike.</summary>
public class PoolGameTests
{
    /// <summary>Deadline-poll for threaded-loop conditions. CI runners stall sim threads far
    /// beyond any tuned sleep; the pause tests assert ordering, never latency.</summary>
    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Thread.Sleep(10);
        }
    }

    private static Vector3 PositionOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<Position>(entity).Value;
    }

    private static float GlowOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<BallGlow>(entity).Intensity;
    }

    [Test]
    public async Task strike_impulse_moves_the_cue_ball()
    {
        using var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);

        runner.EnqueueBallImpulse(cue, new Vector3(3f, 0f, 0f));
        for (var i = 0; i < 30; i++) runner.TickOnce();

        await Assert.That(PositionOf(runner, cue).X).IsGreaterThan(5.5f);
    }

    [Test]
    public async Task strike_sets_spin_but_a_plain_impulse_leaves_it_untouched()
    {
        using var runner = new SimulationRunner(); // no statics → no contact/damping churn
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f, angularDamping: 0f);

        runner.EnqueueBallImpulse(cue, new Vector3(2f, 0f, 0f), new Vector3(0f, 1f, 0f)); // a strike sets spin
        runner.TickOnce();
        runner.TrySampleInterpolation(double.MaxValue, out var afterStrike, out _, out _);
        await Assert.That(afterStrike.GetComponent<AngularVelocity>(cue).Value.Y).IsEqualTo(1f);

        runner.EnqueueBallImpulse(cue, new Vector3(0f, 0f, 1f)); // a plain nudge (null spin) must not clobber it
        runner.TickOnce();
        runner.TrySampleInterpolation(double.MaxValue, out var afterNudge, out _, out _);
        await Assert.That(afterNudge.GetComponent<AngularVelocity>(cue).Value.Y).IsEqualTo(1f);
    }

    [Test]
    public async Task collision_lights_the_glow_and_it_dies_with_the_motion()
    {
        using var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);
        var target = runner.SpawnBall(new Vector3(7, 0.85f, 5), Quaternion.Identity, radius: 0.35f);

        await Assert.That(GlowOf(runner, target)).IsEqualTo(0f);

        runner.EnqueueBallImpulse(cue, new Vector3(6f, 0f, 0f));
        var peak = 0f;
        for (var i = 0; i < 90; i++)
        {
            runner.TickOnce();
            peak = MathF.Max(peak, GlowOf(runner, target));
        }
        await Assert.That(peak).IsGreaterThan(0.3f); // the hit lit the light

        // Let everything damp out; once still, the glow must be fully off.
        for (var i = 0; i < 900; i++) runner.TickOnce();
        await Assert.That(GlowOf(runner, target)).IsEqualTo(0f);
    }

    [Test]
    public async Task rewind_restores_a_past_frame_and_a_new_strike_diverges_the_future()
    {
        using var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);

        // Original timeline: strike +X, run 120 ticks, remember where the cue ended up.
        runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 120; i++) runner.TickOnce();
        var originalEnd = PositionOf(runner, cue);
        await Assert.That(runner.RewindFrameCount).IsEqualTo(120);

        // Scrub display: 100 frames back the cue was still near the start.
        var states = new List<RewoundBall>();
        await Assert.That(runner.TryGetRewindFrame(100, states)).IsTrue();
        var rewound = states.Find(s => s.Entity == cue);
        await Assert.That(rewound.Position.X).IsLessThan(originalEnd.X);

        // Restore that frame (paused, like the UI does), re-strike toward +Z, resume.
        runner.Paused = true;
        runner.RestoreFromRewind(100);
        await Assert.That(PositionOf(runner, cue).X - rewound.Position.X).IsLessThan(1e-4f);
        // History after the restore point is gone.
        await Assert.That(runner.RewindFrameCount).IsEqualTo(20);

        runner.EnqueueBallImpulse(cue, new Vector3(0f, 0f, 4f));
        runner.Paused = false;
        for (var i = 0; i < 120; i++) runner.TickOnce();

        var divergedEnd = PositionOf(runner, cue);
        // The new future differs from the recorded one: the +Z strike bends the trajectory
        // (the restored +X velocity keeps carrying, so X is NOT expected to shrink).
        await Assert.That(divergedEnd.Z).IsGreaterThan(originalEnd.Z + 0.5f);
        await Assert.That(Vector3.Distance(divergedEnd, originalEnd)).IsGreaterThan(0.5f);
    }

    [Test]
    public async Task rewind_restore_reclaims_unpinned_snapshots_when_the_pool_is_starved()
    {
        // Regression (PR #64 review): TickOnce prunes unpinned snapshots when the pool runs
        // dry, but RestoreFromRewind bailed without pruning — a paused resume could silently
        // skip the rewind while the caller believed it applied. The restore must reclaim
        // exactly like a tick, and report false only when every world is genuinely pinned.
        using var runner = new SimulationRunner();
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);
        runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));
        for (var i = 0; i < 60; i++) runner.TickOnce();

        // Renderer pins the newest snapshot, the sim publishes past it, then the renderer
        // moves on: the old pair is now unpinned but stays in the live window until the next
        // publish prunes — and no publish is coming while paused.
        runner.TrySampleInterpolation(double.MaxValue, out _, out _, out _);
        runner.TickOnce();
        runner.TickOnce();
        runner.TrySampleInterpolation(double.MaxValue, out _, out _, out _);

        // Starve the pool, as a long render stall would (not reachable through the public
        // API with a single reader, so reach in). The pooled World type is a
        // source-generated alias private to the Paradise.Sample.Pool assembly — drive the stack
        // reflectively rather than naming it.
        var poolField = typeof(SimulationRunner).GetField("_pool",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pool = poolField.GetValue(runner)!;
        var poolCount = pool.GetType().GetProperty("Count")!;
        var poolPop = pool.GetType().GetMethod("Pop")!;
        var poolPush = pool.GetType().GetMethod("Push")!;
        var stash = new List<object?>();
        void Starve() { while ((int)poolCount.GetValue(pool)! > 0) stash.Add(poolPop.Invoke(pool, null)); }
        Starve();

        runner.Paused = true;
        var scrubbed = new List<RewoundBall>();
        await Assert.That(runner.TryGetRewindFrame(30, scrubbed)).IsTrue();
        var expected = scrubbed.Find(s => s.Entity == cue).Position;

        // Starved but reclaimable: the restore must prune, apply, and report success.
        await Assert.That(runner.RestoreFromRewind(30)).IsTrue();
        await Assert.That(Vector3.Distance(PositionOf(runner, cue), expected)).IsLessThan(1e-4f);

        // Starve again with nothing left to reclaim (only the pinned pair and the newest
        // survive): the restore must refuse — callers keep their scrub instead of losing it.
        Starve();
        await Assert.That(runner.RestoreFromRewind(10)).IsFalse();

        foreach (var world in stash) poolPush.Invoke(pool, new[] { world });
    }

    [Test]
    public async Task pause_keeps_the_ui_pump_alive()
    {
        // Regression: pausing froze TickOnce AND the UI drain with it, so the pause panel
        // could never be interacted with again. Pause must freeze the world, not the UI.
        int handledWhilePaused, ticksBefore, ticksAfterWait;
        using (var runner = new SimulationRunner())
        {
            var ui = new RecordingUi();
            runner.UiInput = ui;
            runner.Start();
            // Deadline-polling, not fixed sleeps — CI runners can stall the sim thread far
            // beyond any tuned window; the assertions are about ordering, not latency.
            WaitUntil(() => ui.Ticks.Count > 0, "first UI tick");
            runner.Paused = true;
            Thread.Sleep(80); // let an in-flight tick drain
            ticksBefore = ui.Ticks.Count;
            var handledBefore = ui.Handled.Count;
            runner.EnqueueUiEvent(WindowEvent.PointerMove(10, 10));
            runner.EnqueueUiEvent(WindowEvent.Mouse(PointerButton.Left, pressed: false, 10, 10));
            WaitUntil(() => ui.Handled.Count >= handledBefore + 2, "paused UI events to drain");
            handledWhilePaused = ui.Handled.Count - handledBefore;
            WaitUntil(() => ui.Ticks.Count > ticksBefore, "UI time to keep flowing while paused");
            ticksAfterWait = ui.Ticks.Count;
            runner.Stop();
        }

        await Assert.That(handledWhilePaused).IsEqualTo(2);   // events still reach the UI
        await Assert.That(ticksAfterWait).IsGreaterThan(ticksBefore); // UI time keeps flowing
    }

    private sealed class RecordingUi : Paradise.Ui.IUiInput
    {
        public readonly List<WindowEvent> Handled = new();
        public readonly List<double> Ticks = new();
        public bool Handle(in WindowEvent uiEvent) { Handled.Add(uiEvent); return false; }
        public void Tick(double simTimeSeconds) => Ticks.Add(simTimeSeconds);
    }

    [Test]
    public async Task pause_freezes_the_threaded_loop()
    {
        // Collected synchronously (Thread.Sleep, no awaits): SharedWorld.Dispose is
        // thread-affine to the constructing thread, and awaits hop the continuation.
        Vector3 frozen, afterPause;
        int frozenFrames, afterPauseFrames, afterResumeFrames;
        using (var runner = new SimulationRunner())
        {
            var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);
            runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));

            runner.Start();
            WaitUntil(() => runner.RewindFrameCount > 0, "sim to start ticking");
            runner.Paused = true;
            // Quiesce: an in-flight tick may still land after Paused flips — wait until the
            // frame count holds still for a full window before sampling the frozen state.
            int settled = runner.RewindFrameCount;
            var quiesce = System.Diagnostics.Stopwatch.StartNew();
            while (quiesce.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(100);
                int now = runner.RewindFrameCount;
                if (now == settled) break;
                settled = now;
            }
            frozen = PositionOf(runner, cue);
            frozenFrames = runner.RewindFrameCount;
            Thread.Sleep(250); // the paused loop must not advance across a real-time window
            afterPause = PositionOf(runner, cue);
            afterPauseFrames = runner.RewindFrameCount;
            runner.Paused = false;
            WaitUntil(() => runner.RewindFrameCount > frozenFrames, "sim to resume after unpause");
            afterResumeFrames = runner.RewindFrameCount;
            runner.Stop();
        }

        await Assert.That(afterPause).IsEqualTo(frozen);
        await Assert.That(afterPauseFrames).IsEqualTo(frozenFrames);
        await Assert.That(afterResumeFrames).IsGreaterThan(frozenFrames);
    }

    // The pool rack authors ball centres ~0.402 apart (pool.tscn). The sim ball radius MUST come
    // from the collider (0.5 shape * 0.4 node scale = 0.2 → diameter 0.4, just under the spacing),
    // which is what the .NET host spawns. The Godot bridge used to hit a 0.35 SphereMesh-fallback
    // (the balls are an imported glb) → diameter 0.7, so every ball deeply overlapped its neighbours
    // and the sim depenetrated them explosively at t=0 (the rack visibly "split"). These pin that
    // the collider radius is stable at the authored spacing and the oversized fallback is not.
    private static readonly Vector3[] RackTriad =
    {
        new(1.3f, 0.85f, 0f),
        new(1.648f, 0.85f, 0.201f),
        new(1.648f, 0.85f, -0.201f),
    };

    private static float MaxHorizontalDrift(float radius)
    {
        using var runner = new SimulationRunner();
        var balls = new List<Entity>();
        foreach (var p in RackTriad) balls.Add(runner.SpawnBall(p, Quaternion.Identity, radius));
        for (var i = 0; i < 60; i++) runner.TickOnce();

        var drift = 0f;
        for (var i = 0; i < balls.Count; i++)
        {
            var pos = PositionOf(runner, balls[i]);
            var dx = pos.X - RackTriad[i].X;
            var dz = pos.Z - RackTriad[i].Z;
            drift = System.MathF.Max(drift, System.MathF.Sqrt(dx * dx + dz * dz));
        }
        return drift;
    }

    [Test]
    public async Task rack_at_authored_spacing_is_stable_with_collider_radius()
    {
        await Assert.That(MaxHorizontalDrift(0.2f)).IsLessThan(0.05f);
    }

    [Test]
    public async Task oversized_ball_radius_scatters_the_rack()
    {
        await Assert.That(MaxHorizontalDrift(0.35f)).IsGreaterThan(0.15f);
    }
}
