using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Game;
using Paradise.Sample.Game.Navigation.Detour;

namespace Paradise.Sample.Game.Tests;

// Diagnostic for the reported "PC jumps position when reversing WASD direction (W → S)".
// Measures per-tick sim displacement and renderer-style interpolated displacement across the
// reversal; any step larger than moveSpeed * dt (+slack) is a genuine position jump.
public class WasdReversalDiagnosticTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(20, 0, 0), new(20, 0, 20), new(0, 0, 20) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return new DetourNavigationMesh(verts, tris);
    }

    [Test]
    public async Task reversal_does_not_jump_sim_position()
    {
        const float moveSpeed = 3.5f;
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(10, 0, 10), Quaternion.Identity, moveSpeed, arriveRadius: 0.25f);

        float maxStep = 0f;
        int maxStepTick = -1;
        Vector3 previous = new(10, 0, 10);
        var log = new List<string>();

        void TickAndRecord(int tick)
        {
            runner.TickOnce();
            runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
            Vector3 position = latest.GetComponent<LocalTransform>(agent).Position;
            float step = (position - previous).Length();
            if (tick is >= 28 and <= 34)
            {
                log.Add($"tick {tick}: pos={position} step={step:F4}");
            }
            if (step > maxStep)
            {
                maxStep = step;
                maxStepTick = tick;
            }
            previous = position;
        }

        runner.SetMoveInput(agent, new Vector3(0, 0, -1)); // W: forward (−Z)
        for (int t = 0; t < 30; t++) TickAndRecord(t);
        runner.SetMoveInput(agent, new Vector3(0, 0, 1)); // S: instant reversal
        for (int t = 30; t < 60; t++) TickAndRecord(t);

        Console.WriteLine(string.Join("\n", log));
        Console.WriteLine($"max per-tick step = {maxStep:F4} at tick {maxStepTick} (budget {moveSpeed / 60f:F4})");
        await Assert.That(maxStep).IsLessThanOrEqualTo(moveSpeed * (float)SimulationRunner.FixedDeltaSeconds + 1e-3f);
    }

    [Test]
    public async Task reversal_does_not_jump_interpolated_render_position()
    {
        const float moveSpeed = 3.5f;
        using var runner = new SimulationRunner(FlatGround());
        Entity agent = runner.SpawnAgent(new Vector3(10, 0, 10), Quaternion.Identity, moveSpeed, arriveRadius: 0.25f);

        // Renderer samples at (simTime − 2/60) with ~60 fps cadence; emulate that against the
        // synchronous tick loop, reversing input at tick 30.
        float maxStep = 0f;
        double maxStepTime = -1;
        Vector3? previous = null;

        for (int t = 0; t < 60; t++)
        {
            runner.SetMoveInput(agent, t < 30 ? new Vector3(0, 0, -1) : new Vector3(0, 0, 1));
            runner.TickOnce();
            double renderTime = t * SimulationRunner.FixedDeltaSeconds - 2.0 / 60.0;
            if (renderTime < 0) continue;
            if (!runner.TrySampleInterpolation(renderTime, out var a, out var b, out float alpha)) continue;
            if (!a.IsAlive(agent) || !b.IsAlive(agent)) continue;
            Vector3 position = Vector3.Lerp(
                a.GetComponent<LocalTransform>(agent).Position,
                b.GetComponent<LocalTransform>(agent).Position,
                Math.Clamp(alpha, 0f, 1f));
            if (previous is { } prev)
            {
                float step = (position - prev).Length();
                if (step > maxStep)
                {
                    maxStep = step;
                    maxStepTime = renderTime;
                }
            }
            previous = position;
        }

        Console.WriteLine($"max per-frame interpolated step = {maxStep:F4} at t={maxStepTime:F3} (budget {moveSpeed / 60f:F4})");
        await Assert.That(maxStep).IsLessThanOrEqualTo(moveSpeed * (float)SimulationRunner.FixedDeltaSeconds + 1e-3f);
    }
}
