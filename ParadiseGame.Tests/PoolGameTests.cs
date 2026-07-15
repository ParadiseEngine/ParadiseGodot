using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using ParadiseGame;
using ParadiseGame.Navigation.Detour;

namespace ParadiseGame.Tests;

/// <summary>The pool-game sim mechanics, driven synchronously through TickOnce: strike
/// impulses move the cue ball, ball↔ball hits light the glow which decays and dies with the
/// motion, and the rewind buffer can restore a past frame whose future then diverges under a
/// different strike.</summary>
public class PoolGameTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(30, 0, 0), new(30, 0, 30), new(0, 0, 30) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return new DetourNavigationMesh(verts, tris);
    }

    private static Vector3 PositionOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<LocalTransform>(entity).Position;
    }

    private static float GlowOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<BallGlow>(entity).Intensity;
    }

    [Test]
    public async Task strike_impulse_moves_the_cue_ball()
    {
        using var runner = new SimulationRunner(FlatGround());
        var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);

        runner.EnqueueBallImpulse(cue, new Vector3(3f, 0f, 0f));
        for (var i = 0; i < 30; i++) runner.TickOnce();

        await Assert.That(PositionOf(runner, cue).X).IsGreaterThan(5.5f);
    }

    [Test]
    public async Task collision_lights_the_glow_and_it_dies_with_the_motion()
    {
        using var runner = new SimulationRunner(FlatGround());
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
        using var runner = new SimulationRunner(FlatGround());
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
    public async Task pause_freezes_the_threaded_loop()
    {
        // Collected synchronously (Thread.Sleep, no awaits): SharedWorld.Dispose is
        // thread-affine to the constructing thread, and awaits hop the continuation.
        Vector3 frozen, afterPause;
        int frozenFrames, afterPauseFrames, afterResumeFrames;
        using (var runner = new SimulationRunner(FlatGround()))
        {
            var cue = runner.SpawnBall(new Vector3(5, 0.85f, 5), Quaternion.Identity, radius: 0.35f);
            runner.EnqueueBallImpulse(cue, new Vector3(4f, 0f, 0f));

            runner.Start();
            Thread.Sleep(200);
            runner.Paused = true;
            Thread.Sleep(100); // let an in-flight tick drain
            frozen = PositionOf(runner, cue);
            frozenFrames = runner.RewindFrameCount;
            Thread.Sleep(250);
            afterPause = PositionOf(runner, cue);
            afterPauseFrames = runner.RewindFrameCount;
            runner.Paused = false;
            Thread.Sleep(200);
            afterResumeFrames = runner.RewindFrameCount;
            runner.Stop();
        }

        await Assert.That(afterPause).IsEqualTo(frozen);
        await Assert.That(afterPauseFrames).IsEqualTo(frozenFrames);
        await Assert.That(afterResumeFrames).IsGreaterThan(frozenFrames);
    }
}
