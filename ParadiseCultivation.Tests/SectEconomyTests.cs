using Paradise.ECS;

namespace ParadiseCultivation.Tests;

/// <summary>The sect contribution economy: the hash-scheduled monthly mission board (one
/// attempt per month, at one's own mountain gate), the exchange (pills / healing), and the
/// contribution reset on leaving.</summary>
public class SectEconomyTests
{
    // Component pokes live in non-async helpers — ref locals are not allowed in async tests.

    private static int SectSiteIndex(CultivationRunner runner)
    {
        for (var i = 0; i < runner.Map.Sites.Count; i++)
        {
            if (runner.Map.Sites[i].Kind == SiteKind.Sect) return i;
        }
        throw new InvalidOperationException("no sect in this world");
    }

    private static CultivationRunner JoinedRunner(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        var site = runner.Map.Sites[SectSiteIndex(runner)];
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        player.X = site.X;
        player.Y = site.Y;
        var leader = runner.FindSectLeader(runner.Current, SectSiteIndex(runner))
            ?? throw new InvalidOperationException("no leader");
        runner.Current.GetComponent<NpcState>(leader).AffectionToPlayer =
            Fixture.Config.Sect.JoinMinLeaderAffection + 50;
        runner.RequestJoinSect();
        Fixture.RunUntilIdle(runner);
        if (runner.Current.GetComponent<PlayerData>(runner.Player).SectSiteIndex < 0)
        {
            throw new InvalidOperationException("joining failed");
        }
        return runner;
    }

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    private static void Poke(CultivationRunner runner, int contribution = -1, int injuryMonths = -1)
    {
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        if (contribution >= 0) player.SectContribution = contribution;
        if (injuryMonths >= 0) player.InjuryMonths = injuryMonths;
    }

    [Test]
    public async Task the_mission_board_is_deterministic_per_world_and_month()
    {
        var missions = Fixture.Config.Sect.Missions;
        await Assert.That(missions.Length).IsGreaterThan(0);
        for (var month = 0L; month < 24; month++)
        {
            var a = CultivationRules.SectMissionIndex(Fixture.Config, 777, 3, month);
            var b = CultivationRules.SectMissionIndex(Fixture.Config, 777, 3, month);
            await Assert.That(a).IsEqualTo(b);
            await Assert.That(a).IsGreaterThanOrEqualTo(0);
            await Assert.That(a).IsLessThan(missions.Length);
        }
    }

    [Test]
    public async Task missions_require_ones_own_mountain_gate()
    {
        using var runner = Fixture.NewRunner(); // sectless, at the home town
        runner.RequestSectMission();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult)
            .IsEqualTo(Fixture.Config.Text.Messages.MissionNotHereMsg);
        await Assert.That(runner.Busy).IsFalse();
    }

    [Test]
    public async Task one_mission_attempt_per_month_win_or_lose()
    {
        using var runner = JoinedRunner();
        var month = runner.Day / Fixture.Config.Time.DaysPerMonth;
        var missionIndex = CultivationRules.SectMissionIndex(
            Fixture.Config, runner.Map.GenerationSeed, Player(runner).SectSiteIndex, month);
        var mission = Fixture.Config.Sect.Missions[missionIndex];
        var injuryBefore = Player(runner).InjuryMonths;

        runner.RequestSectMission();
        Fixture.RunUntilIdle(runner);

        // Either outcome names the mission; the ATTEMPT spends the month.
        await Assert.That(runner.LastActionResult.Contains(mission.Name)).IsTrue();
        await Assert.That(Player(runner).LastMissionMonth).IsEqualTo(month);
        var succeeded = Player(runner).SectContribution == mission.ContributionReward;
        var failed = Player(runner).InjuryMonths == injuryBefore + mission.FailureInjuryMonths;
        await Assert.That(succeeded || failed).IsTrue();

        runner.RequestSectMission();
        Fixture.RunUntilIdle(runner); // the mission's days may still cross no month boundary
        if (runner.Day / Fixture.Config.Time.DaysPerMonth == month)
        {
            await Assert.That(runner.LastActionResult)
                .IsEqualTo(Fixture.Config.Text.Messages.MissionDoneThisMonthMsg);
        }
    }

    [Test]
    public async Task the_exchange_trades_contribution_for_pills_and_healing()
    {
        using var runner = JoinedRunner();
        var pillCost = Fixture.Config.Sect.ExchangePillContribution;
        var healCost = Fixture.Config.Sect.ExchangeHealContribution;

        // Insufficient contribution is refused.
        runner.RequestExchangePill();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.ExchangeNeedMsg));

        Poke(runner, contribution: pillCost + healCost, injuryMonths: 3);
        runner.RequestExchangePill();
        Fixture.RunUntilIdle(runner);
        await Assert.That(Player(runner).Pills).IsEqualTo(1);
        await Assert.That(Player(runner).SectContribution).IsEqualTo(healCost);

        runner.RequestExchangeHeal();
        Fixture.RunUntilIdle(runner);
        await Assert.That(Player(runner).InjuryMonths).IsEqualTo(0);
        await Assert.That(Player(runner).SectContribution).IsEqualTo(0);

        // No injury → the healing pill is refused (no contribution wasted).
        Poke(runner, contribution: healCost);
        runner.RequestExchangeHeal();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult)
            .IsEqualTo(Fixture.Config.Text.Messages.ExchangeNoInjuryMsg);
        await Assert.That(Player(runner).SectContribution).IsEqualTo(healCost);
    }

    [Test]
    public async Task leaving_the_sect_forfeits_contribution()
    {
        using var runner = JoinedRunner();
        Poke(runner, contribution: 100);

        runner.RequestLeaveSect();
        Fixture.RunUntilIdle(runner);

        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(-1);
        await Assert.That(Player(runner).SectContribution).IsEqualTo(0);
    }

    [Test]
    public async Task contribution_survives_a_save_round_trip()
    {
        using var runner = JoinedRunner();
        Poke(runner, contribution: 77);
        var path = Path.Combine(Path.GetTempPath(), $"cultivation-economy-{Guid.NewGuid():N}.json");
        try
        {
            runner.RequestSave(path);
            runner.TickOnce();
            Poke(runner, contribution: 0);

            runner.RequestLoad(path);
            runner.TickOnce();
            await Assert.That(Player(runner).SectContribution).IsEqualTo(77);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
