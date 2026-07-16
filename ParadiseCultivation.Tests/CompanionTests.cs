using Paradise.ECS;

namespace ParadiseCultivation.Tests;

/// <summary>Dao companions: the mutual-affection + realm-gap proposal gates, the
/// dual-cultivation bonus at the companion's side, severance costs, widowhood on the
/// companion's death, and the v6 save fields.</summary>
public class CompanionTests
{
    // Component pokes live in non-async helpers — ref locals are not allowed in async tests.

    private static void SetMutualAffection(CultivationRunner runner, Entity npc, float value)
    {
        ref var state = ref runner.Current.GetComponent<NpcState>(npc);
        state.AffectionToPlayer = value;
        state.PlayerAffection = value;
    }

    private static void SetNpcRealm(CultivationRunner runner, Entity npc, int realmIndex) =>
        runner.Current.GetComponent<Cultivator>(npc).RealmIndex = realmIndex;

    private static void KillNpc(CultivationRunner runner, Entity npc)
    {
        ref var state = ref runner.Current.GetComponent<NpcState>(npc);
        state.Alive = 0;
        state.JustDied = 1;
    }

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    private static (CultivationRunner Runner, Entity Npc) BondedRunner(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        var npc = Fixture.FirstNpcAtPlayerSite(runner);
        SetMutualAffection(runner, npc, Fixture.Config.Companion.MinAffectionBoth + 50);
        runner.RequestProposeCompanion(npc);
        Fixture.RunUntilIdle(runner);
        if (Player(runner).CompanionNpcId < 0) throw new InvalidOperationException("bonding failed");
        return (runner, npc);
    }

    [Test]
    public async Task the_proposal_gates_on_mutual_affection()
    {
        using var runner = Fixture.NewRunner();
        var npc = Fixture.FirstNpcAtPlayerSite(runner);
        var threshold = Fixture.Config.Companion.MinAffectionBoth;

        // Their heart is willing, yours (their side of you) is not — refused.
        ref var state = ref runner.Current.GetComponent<NpcState>(npc);
        state.AffectionToPlayer = threshold + 10;
        state.PlayerAffection = threshold - 10;
        runner.RequestProposeCompanion(npc);
        runner.TickOnce();

        await Assert.That(runner.LastReply)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionRefuseAffectionMsg));
        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(-1);
        await Assert.That(runner.Busy).IsFalse(); // refusals cost no time

        SetMutualAffection(runner, npc, threshold);
        runner.RequestProposeCompanion(npc);
        Fixture.RunUntilIdle(runner);

        var npcId = runner.Current.GetComponent<NpcState>(npc).NpcId;
        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(npcId);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionBondLog)))).IsTrue();
        await Assert.That(runner.MemoriesOf(npc).Any(memory => memory.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionBondMemory)))).IsTrue();
    }

    [Test]
    public async Task a_realm_gap_beyond_the_config_blocks_the_bond()
    {
        using var runner = Fixture.NewRunner();
        var npc = Fixture.FirstNpcAtPlayerSite(runner);
        SetMutualAffection(runner, npc, Fixture.Config.Companion.MinAffectionBoth + 10);
        SetNpcRealm(runner, npc, Fixture.Config.Companion.MaxRealmGap + 1); // player is realm 0

        runner.RequestProposeCompanion(npc);
        runner.TickOnce();

        await Assert.That(runner.LastReply)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionRefuseRealmMsg));
        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(-1);
    }

    [Test]
    public async Task only_one_companion_at_a_time()
    {
        var (runner, npc) = BondedRunner();
        using var _ = runner;
        var other = runner.Npcs.First(entity => entity != npc &&
            runner.Current.GetComponent<NpcState>(entity).Alive != 0);
        SetMutualAffection(runner, other, Fixture.Config.Companion.MinAffectionBoth + 50);

        runner.RequestProposeCompanion(other);
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.LastReply)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionAlreadyMsg));
        var npcId = runner.Current.GetComponent<NpcState>(npc).NpcId;
        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(npcId);
    }

    [Test]
    public async Task dual_cultivation_boosts_gain_at_the_companions_side()
    {
        var (runner, _) = BondedRunner();
        using var __ = runner;
        var world = runner.Current;
        var player = Player(runner);
        var cultivator = world.GetComponent<Cultivator>(runner.Player);

        // Bonded at the home town, standing there: dual cultivation is active.
        await Assert.That(runner.CompanionPresent(world, in player)).IsTrue();

        var quietDay = 0L; // month 0 hosts no world event (firstEventMonth = 1)
        var alone = CultivationRules.MonthlyCultivationGain(
            Fixture.Config, runner.Map, in cultivator, in player, quietDay);
        var together = CultivationRules.MonthlyCultivationGain(
            Fixture.Config, runner.Map, in cultivator, in player, quietDay, companionPresent: true);
        await Assert.That(together / alone)
            .IsEqualTo(1.0 + Fixture.Config.Companion.DualCultivationBonus).Within(1e-4);
    }

    [Test]
    public async Task severance_costs_both_hearts_and_clears_the_bond()
    {
        var (runner, npc) = BondedRunner();
        using var _ = runner;
        var before = runner.Current.GetComponent<NpcState>(npc);

        runner.RequestLeaveCompanion();
        Fixture.RunUntilIdle(runner);

        var after = runner.Current.GetComponent<NpcState>(npc);
        var penalty = Fixture.Config.Companion.LeaveAffectionPenalty;
        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(-1);
        await Assert.That(after.AffectionToPlayer).IsEqualTo(before.AffectionToPlayer + penalty).Within(1e-3f);
        await Assert.That(after.PlayerAffection).IsEqualTo(before.PlayerAffection + penalty).Within(1e-3f);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionLeaveLog)))).IsTrue();
    }

    [Test]
    public async Task the_companions_death_widows_the_player_and_enters_the_chronicle()
    {
        var (runner, npc) = BondedRunner();
        using var _ = runner;

        KillNpc(runner, npc);
        runner.TickOnce(); // the post-pass consumes the flag

        await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(-1);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.CompanionDeathLog)))).IsTrue();
    }

    [Test]
    public async Task the_bond_survives_a_save_round_trip()
    {
        var (runner, npc) = BondedRunner();
        using var _ = runner;
        var npcId = runner.Current.GetComponent<NpcState>(npc).NpcId;
        var path = Path.Combine(Path.GetTempPath(), $"cultivation-companion-{Guid.NewGuid():N}.json");
        try
        {
            runner.RequestSave(path);
            runner.TickOnce();
            runner.RequestLeaveCompanion(); // diverge, then load back
            Fixture.RunUntilIdle(runner);
            await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(-1);

            runner.RequestLoad(path);
            runner.TickOnce();
            await Assert.That(Player(runner).CompanionNpcId).IsEqualTo(npcId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
