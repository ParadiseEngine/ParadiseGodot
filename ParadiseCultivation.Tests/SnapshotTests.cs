namespace ParadiseCultivation.Tests;

/// <summary>The snapshot machinery itself: published worlds are immutable copies with
/// preserved entity handles, sampling pins/releases correctly (respecting the pool-starvation
/// lesson: re-sample to release pins), and identical seeds + identical command sequences
/// produce identical worlds — the determinism the snapshot-read parallel schedule must not
/// break.</summary>
public class SnapshotTests
{
    [Test]
    public async Task published_snapshots_preserve_entity_handles_and_values()
    {
        using var runner = Fixture.NewRunner();
        runner.RequestCultivate(2);
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _)).IsTrue();

        // The same Entity handles read the same component values in the published snapshot
        // as in the live world (CopyFrom preserves handles — the interpolation contract).
        var live = runner.Current;
        var livePlayer = live.GetComponent<Cultivator>(runner.Player);
        var snapPlayer = latest.GetComponent<Cultivator>(runner.Player);
        await Assert.That(snapPlayer.CultivationPoints).IsEqualTo(livePlayer.CultivationPoints);
        await Assert.That(snapPlayer.AgeDays).IsEqualTo(livePlayer.AgeDays);

        foreach (var entity in runner.Npcs)
        {
            await Assert.That(latest.IsAlive(entity)).IsTrue();
            var liveNpc = live.GetComponent<NpcState>(entity);
            var snapNpc = latest.GetComponent<NpcState>(entity);
            await Assert.That(snapNpc.NpcId).IsEqualTo(liveNpc.NpcId);
        }

        // Release the pin (the TrySampleInterpolation-pins-until-next-call lesson).
        runner.TrySampleInterpolation(double.MaxValue, out _, out _, out _);
    }

    [Test]
    public async Task a_pinned_snapshot_is_never_recycled_while_ticking_continues()
    {
        using var runner = Fixture.NewRunner();
        runner.TickOnce();
        await Assert.That(runner.TrySampleInterpolation(double.MaxValue, out var pinned, out _, out _)).IsTrue();
        var pinnedDay = pinned.GetComponent<SimulationContext>(runner.Player).Day;

        // Tick well past the pool depth; the pinned world must keep its published values.
        // (Pool is 32 deep; publish-time pruning recycles unpinned frames, so this cannot
        // stall the sim — the lesson's freeze needs EVERY world pinned.)
        runner.RequestSeclude(1);
        Fixture.RunUntilIdle(runner);

        await Assert.That(pinned.GetComponent<SimulationContext>(runner.Player).Day).IsEqualTo(pinnedDay);
        runner.TrySampleInterpolation(double.MaxValue, out _, out _, out _); // release
    }

    [Test]
    public async Task same_seed_and_commands_produce_identical_worlds()
    {
        using var a = Fixture.NewRunner(seed: 777);
        using var b = Fixture.NewRunner(seed: 777);

        foreach (var runner in new[] { a, b })
        {
            runner.RequestCultivate(6);
            Fixture.RunUntilIdle(runner);
            runner.RequestExplore();
            Fixture.RunUntilIdle(runner);
            runner.RequestSeclude(3);
            Fixture.RunUntilIdle(runner);
        }

        await Assert.That(b.Day).IsEqualTo(a.Day);
        var pa = a.Current.GetComponent<Cultivator>(a.Player);
        var pb = b.Current.GetComponent<Cultivator>(b.Player);
        await Assert.That(pb.RealmIndex).IsEqualTo(pa.RealmIndex);
        await Assert.That(pb.SubStage).IsEqualTo(pa.SubStage);
        await Assert.That(pb.CultivationPoints).IsEqualTo(pa.CultivationPoints);

        await Assert.That(b.Npcs.Count).IsEqualTo(a.Npcs.Count);
        for (var i = 0; i < a.Npcs.Count; i++)
        {
            var na = a.Current.GetComponent<Cultivator>(a.Npcs[i]);
            var nb = b.Current.GetComponent<Cultivator>(b.Npcs[i]);
            await Assert.That((nb.RealmIndex, nb.SubStage, nb.CultivationPoints, nb.AgeDays))
                .IsEqualTo((na.RealmIndex, na.SubStage, na.CultivationPoints, na.AgeDays));
        }
        await Assert.That(b.Chronicle.Select(e => e.Summary)).IsEquivalentTo(a.Chronicle.Select(e => e.Summary));
    }

    [Test]
    public async Task settlement_hash_is_deterministic_and_stream_separated()
    {
        var a = SettlementSystem.Hash(1, 2, 3, 0);
        var b = SettlementSystem.Hash(1, 2, 3, 0);
        await Assert.That(b).IsEqualTo(a);
        await Assert.That(SettlementSystem.Hash(1, 2, 3, 1) == a).IsFalse();
        await Assert.That(SettlementSystem.Hash(2, 2, 3, 0) == a).IsFalse();

        var h01 = SettlementSystem.Hash01(9, 9, 9, 9);
        await Assert.That(h01).IsGreaterThanOrEqualTo(0f);
        await Assert.That(h01).IsLessThan(1f);
    }

    [Test]
    public async Task reroll_rebuilds_the_world_in_place()
    {
        using var runner = Fixture.NewRunner(seed: 1);
        var firstSeed = runner.Map.Seed;

        runner.RequestStartNew(2, 0);
        runner.TickOnce();

        await Assert.That(runner.Map.Seed).IsEqualTo(2);
        await Assert.That(runner.Map.Seed == firstSeed).IsFalse();
        await Assert.That(runner.Phase).IsEqualTo(GamePhase.NewGame);
        await Assert.That(runner.Day).IsEqualTo(0L);
        await Assert.That(runner.Npcs.Count).IsGreaterThan(0);
        await Assert.That(runner.Current.IsAlive(runner.Player)).IsTrue();
    }
}
