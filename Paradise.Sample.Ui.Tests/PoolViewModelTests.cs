using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;
using Paradise.Sample.Pool.Navigation;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Ui.Tests;

/// <summary>Drives the MVVM <see cref="PoolViewModel"/> headlessly over a real snapshot
/// <see cref="SimulationRunner"/> — the same ViewModel the ImGui sample's View renders. Uses a
/// self-contained zero-gravity planar rack (no table/floor): an impulse slides an object ball across
/// XZ until its center crosses a pocket mouth, MovementSystem captures it (planar, floor-free), the
/// SystemEvents reactor (ScoreSystem) folds the BallPocketed one frame later, and Score climbs. A
/// managed GameReset (via <see cref="PoolViewModel.Reset"/>) zeroes it again.</summary>
public class PoolViewModelTests
{
    // No navmesh agents in this sample — a stub keeps the tests off the Detour package.
    private sealed class NoNavMesh : INavigationMesh
    {
        public IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to) => Array.Empty<Vector3>();
    }

    private static PhysicsTuning Planar => new(0.01f, 0.02f, 1.2f, gravity: Vector3.Zero);

    private static PocketConfig PocketAt(Vector2 center, Vector3 ballPos)
    {
        var pocket = new PocketConfig
        {
            PocketCount = 1,
            ParkPosition = new Vector3(center.X, ballPos.Y, center.Y + 0.75f),
            RespawnPosition = ballPos,
            IsCue = 0,
        };
        pocket.Pockets[0] = new Vector4(center.X, center.Y, 0.09f, 0f); // r² = 0.3²
        return pocket;
    }

    private static (SimulationRunner runner, PoolViewModel vm, Entity cue, Entity obj) NewGame()
    {
        var runner = new SimulationRunner(new NoNavMesh());
        var cue = runner.SpawnBall(new Vector3(0, 0, 1), Quaternion.Identity, radius: 0.12f, tuning: Planar);
        var obj = runner.SpawnBall(new Vector3(0, 0, 0), Quaternion.Identity, radius: 0.12f,
            linearDamping: 0.3f, pocket: PocketAt(new Vector2(0, -1), new Vector3(0, 0, 0)), tuning: Planar);
        var vm = new PoolViewModel(runner, cue, new[] { obj });
        return (runner, vm, cue, obj);
    }

    [Test]
    public async Task ball_count_and_initial_score_project_the_rack()
    {
        var (runner, vm, _, _) = NewGame();
        using (runner)
        {
            await Assert.That(vm.BallCount).IsEqualTo(2); // cue + one object ball
            await Assert.That(vm.Score).IsEqualTo(0);
            await Assert.That(vm.SunkCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task impulse_into_the_pocket_scores_via_the_reactor()
    {
        var (runner, vm, _, obj) = NewGame();
        using (runner)
        {
            runner.EnqueueBallImpulse(obj, new Vector3(0, 0, -3f)); // slide it toward its pocket at (0,-1)
            for (var i = 0; i < 180; i++) runner.TickOnce(); // capture + one-frame deferred fold

            await Assert.That(vm.Score).IsGreaterThanOrEqualTo(1);
            await Assert.That(vm.SunkCount).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task break_command_drives_the_score_up()
    {
        // Break() impulses the cue toward −Z; here the object ball sits between the cue and its
        // pocket, so the cue's momentum carries the object ball into the pocket.
        var runner = new SimulationRunner(new NoNavMesh());
        using (runner)
        {
            var cue = runner.SpawnBall(new Vector3(0, 0, 0.24f), Quaternion.Identity, radius: 0.12f,
                linearDamping: 0.3f, tuning: Planar);
            var obj = runner.SpawnBall(new Vector3(0, 0, 0), Quaternion.Identity, radius: 0.12f,
                linearDamping: 0.3f, pocket: PocketAt(new Vector2(0, -1), new Vector3(0, 0, 0)), tuning: Planar);
            var vm = new PoolViewModel(runner, cue, new[] { obj });

            vm.Break();
            for (var i = 0; i < 240; i++) runner.TickOnce();

            await Assert.That(vm.Score).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task reset_zeroes_the_score_via_managed_emit()
    {
        var (runner, vm, _, obj) = NewGame();
        using (runner)
        {
            runner.EnqueueBallImpulse(obj, new Vector3(0, 0, -3f));
            for (var i = 0; i < 180; i++) runner.TickOnce();
            await Assert.That(vm.Score).IsGreaterThanOrEqualTo(1);

            vm.Reset(); // managed GameReset emit; ScoreSystem folds it one tick later
            for (var i = 0; i < 3; i++) runner.TickOnce();

            await Assert.That(vm.Score).IsEqualTo(0);
        }
    }
}
