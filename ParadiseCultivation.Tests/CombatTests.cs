using System.Text.Json.Nodes;

namespace ParadiseCultivation.Tests;

/// <summary>The semi-auto combat encounter (P2): explore can be interrupted by a beast, the
/// standoff blocks every other action, fighting auto-resolves into loot or injury, fleeing
/// rolls against the realm gap, the standoff rides the v5 save, and v4 saves load calm.</summary>
public class CombatTests
{
    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    /// <summary>Explore until a beast interrupts (bounded; the authored chance is 22%, so
    /// 64 tries not triggering would be a broken trigger, not bad luck).</summary>
    private static CultivationRunner RunnerInStandoff(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        for (var i = 0; i < 64 && runner.PendingBeast is null; i++)
        {
            runner.RequestExplore();
            Fixture.RunUntilIdle(runner);
        }
        if (runner.PendingBeast is null)
        {
            runner.Dispose();
            throw new InvalidOperationException("no encounter in 64 explores");
        }
        return runner;
    }

    [Test]
    public async Task a_beast_interrupts_exploration_and_blocks_everything_else()
    {
        using var runner = RunnerInStandoff();
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.EncounterMsg));

        // Every ordinary action is refused mid-standoff.
        var blocked = Fixture.Config.Text.Messages.EncounterBlocksMsg;
        runner.RequestCultivate(1);
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(blocked);
        runner.RequestTravelStep(1, 0);
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(blocked);
        runner.RequestExplore();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(blocked);
        await Assert.That(runner.PendingBeast is not null).IsTrue();
    }

    [Test]
    public async Task fighting_resolves_into_loot_or_injury_and_clears_the_standoff()
    {
        using var runner = RunnerInStandoff();
        var beast = runner.PendingBeast!.Value;
        var before = Player(runner);

        runner.RequestFight();
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.PendingBeast is null).IsTrue();
        var messages = Fixture.Config.Text.Messages;
        var cfg = Fixture.Config.Combat;
        var after = Player(runner);
        if (runner.LastActionResult.Contains(Fixture.Skeleton(messages.FightWinMsg)))
        {
            await Assert.That(after.SpiritStones).IsGreaterThan(before.SpiritStones);
            await Assert.That(after.Herbs - before.Herbs)
                .IsEqualTo((beast.RealmIndex + 1) * cfg.LootHerbsPerRealm);
        }
        else
        {
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(messages.FightLoseMsg));
            await Assert.That(after.InjuryMonths - before.InjuryMonths)
                .IsEqualTo(Math.Max(1, (beast.RealmIndex + 1) * cfg.LossInjuryMonthsPerRealm));
        }
    }

    [Test]
    public async Task fleeing_resolves_and_clears_the_standoff()
    {
        using var runner = RunnerInStandoff();
        var before = Player(runner);

        runner.RequestFlee();
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.PendingBeast is null).IsTrue();
        var messages = Fixture.Config.Text.Messages;
        if (runner.LastActionResult.Contains(Fixture.Skeleton(messages.FleeOkMsg)))
        {
            await Assert.That(Player(runner).InjuryMonths).IsEqualTo(before.InjuryMonths);
        }
        else
        {
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(messages.FleeFailMsg));
            await Assert.That(Player(runner).InjuryMonths)
                .IsEqualTo(before.InjuryMonths + Fixture.Config.Combat.FleeFailInjuryMonths);
        }
    }

    [Test]
    public async Task the_standoff_rides_the_save_and_v4_loads_calm()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cultfight_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = RunnerInStandoff();
            var beast = runner.PendingBeast!.Value;

            // Saving mid-standoff is allowed (knowledge is never hostage) and honest.
            runner.RequestSave(path);
            runner.TickOnce();
            await Assert.That(runner.LastActionResult)
                .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.SaveDoneMsg));

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();
            await Assert.That(restored.PendingBeast).IsEqualTo(beast);

            // The restored standoff still gates commands.
            restored.RequestCultivate(1);
            restored.TickOnce();
            await Assert.That(restored.LastActionResult)
                .IsEqualTo(Fixture.Config.Text.Messages.EncounterBlocksMsg);

            // Strip the v5 fields: a v4 save loads with no beast in sight.
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["version"] = 4;
            node.Remove("encounterNameIndex");
            node.Remove("encounterRealmIndex");
            File.WriteAllText(path, node.ToJsonString());

            using var v4 = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            v4.RequestLoad(path);
            v4.TickOnce();
            await Assert.That(v4.PendingBeast is null).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task fight_and_flee_are_noops_without_a_standoff()
    {
        using var runner = Fixture.NewRunner();
        runner.RequestFight();
        runner.RequestFlee();
        runner.TickOnce();
        await Assert.That(runner.PendingBeast is null).IsTrue();
        await Assert.That(runner.Busy).IsFalse();
        await Assert.That(runner.LastActionResult).IsEqualTo(string.Empty);
    }
}
