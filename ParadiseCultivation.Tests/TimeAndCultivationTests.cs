namespace ParadiseCultivation.Tests;

/// <summary>Time as the core resource, driven through the runner's command queue + animated
/// time flow: actions advance the calendar, month crossings settle the player (managed) and
/// the NPC population (SettlementSystem), breakthroughs extend lifespan, and an exhausted
/// lifespan ends the run.</summary>
public class TimeAndCultivationTests
{
    [Test]
    public async Task cultivating_advances_the_calendar_exactly_and_gains_points()
    {
        using var runner = Fixture.NewRunner();
        var dayBefore = runner.Day;
        var ageBefore = runner.Current.GetComponent<Cultivator>(runner.Player).AgeDays;

        runner.RequestCultivate(3);
        Fixture.RunUntilIdle(runner);

        // The animated advance must land on the EXACT day (integer-target completion).
        await Assert.That(runner.Day).IsEqualTo(dayBefore + 3L * Fixture.Config.Time.DaysPerMonth);
        var cultivator = runner.Current.GetComponent<Cultivator>(runner.Player);
        await Assert.That(cultivator.AgeDays - ageBefore - 3.0 * Fixture.Config.Time.DaysPerMonth).IsLessThan(1e-6);
        await Assert.That(cultivator.CultivationPoints + cultivator.SubStage * 1000).IsGreaterThan(0.0);
        await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.CultivateDoneMsg));
    }

    [Test]
    public async Task actions_are_refused_while_time_is_flowing()
    {
        using var runner = Fixture.NewRunner();
        runner.RequestSeclude(2);
        runner.TickOnce(); // starts the advance
        await Assert.That(runner.Busy).IsTrue();

        runner.RequestExplore();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.Occupied);
        Fixture.RunUntilIdle(runner);
    }

    [Test]
    public async Task spirit_root_grade_scales_cultivation_speed()
    {
        // The design checklist: spirit roots affect cultivation speed.
        using var runner = Fixture.NewRunner();
        var world = runner.Current;
        var cultivator = world.GetComponent<Cultivator>(runner.Player);

        // No ref locals in async methods — mutate through the ref return, compare on copies.
        world.GetComponent<PlayerData>(runner.Player).SpiritRootGrade = 0;
        var common = world.GetComponent<PlayerData>(runner.Player);
        var baseline = CultivationRules.MonthlyCultivationGain(Fixture.Config, runner.Map, in cultivator, in common, runner.Day);
        world.GetComponent<PlayerData>(runner.Player).SpiritRootGrade = Fixture.Config.SpiritRoots.Grades.Length - 1;
        var heavenly = world.GetComponent<PlayerData>(runner.Player);
        var best = CultivationRules.MonthlyCultivationGain(Fixture.Config, runner.Map, in cultivator, in heavenly, runner.Day);

        var multiplier = Fixture.Config.SpiritRoots.Grades[^1].Multiplier;
        await Assert.That(Math.Abs(best - baseline * multiplier)).IsLessThan(0.001);
    }

    [Test]
    public async Task sub_stages_advance_automatically_up_to_perfected_only()
    {
        using var runner = Fixture.NewRunner();
        var realm = Fixture.Config.Realms[0];

        // Enough for every sub-stage several times over — must stop at Perfected.
        runner.Current.GetComponent<Cultivator>(runner.Player).CultivationPoints = realm.PointsPerSubStage * 100.0;
        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);

        var cultivator = runner.Current.GetComponent<Cultivator>(runner.Player);
        await Assert.That(cultivator.RealmIndex).IsEqualTo(0);
        await Assert.That(cultivator.SubStage).IsEqualTo(Fixture.Config.SubStages.Length - 1);
        await Assert.That(cultivator.CultivationPoints).IsLessThanOrEqualTo((double)realm.PointsPerSubStage);
        await Assert.That(CultivationRules.BreakthroughReady(Fixture.Config, in cultivator)).IsTrue();
    }

    [Test]
    public async Task breakthrough_advances_the_realm_and_extends_lifespan()
    {
        using var runner = Fixture.NewRunner();
        runner.Current.GetComponent<Cultivator>(runner.Player).CultivationPoints =
            Fixture.Config.Realms[0].PointsPerSubStage * 100.0;
        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);
        runner.Current.GetComponent<PlayerData>(runner.Player).Fortune = Fixture.Config.Fortune.Max;

        // Qi Refining's authored chance (0.9) + max fortune clamps to 0.98 — retry the rare
        // failure rather than flaking: failure is a valid outcome that costs an attempt only.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (runner.Current.GetComponent<Cultivator>(runner.Player).RealmIndex != 0) break;
            runner.Current.GetComponent<Cultivator>(runner.Player).CultivationPoints =
                Fixture.Config.Realms[0].PointsPerSubStage;
            runner.RequestBreakthrough();
            Fixture.RunUntilIdle(runner);
        }

        var cultivator = runner.Current.GetComponent<Cultivator>(runner.Player);
        var player = runner.Current.GetComponent<PlayerData>(runner.Player);
        await Assert.That(cultivator.RealmIndex).IsEqualTo(1);
        await Assert.That(cultivator.SubStage).IsEqualTo(0);
        await Assert.That(player.LifespanYears).IsEqualTo((double)Fixture.Config.Realms[1].LifespanYears);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.BreakthroughLog)))).IsTrue();
    }

    [Test]
    public async Task breakthrough_is_refused_before_perfected_stage()
    {
        using var runner = Fixture.NewRunner();

        runner.RequestBreakthrough();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.BreakthroughNotReady);
        await Assert.That(runner.Current.GetComponent<Cultivator>(runner.Player).RealmIndex).IsEqualTo(0);
        await Assert.That(runner.Busy).IsFalse(); // a refused attempt costs no time
    }

    [Test]
    public async Task lifespan_exhaustion_ends_the_run()
    {
        using var runner = Fixture.NewRunner();
        // Age the character to the brink, then let a long seclusion finish the job.
        var player = runner.Current.GetComponent<PlayerData>(runner.Player);
        runner.Current.GetComponent<Cultivator>(runner.Player).AgeDays =
            (player.LifespanYears - 1) * CultivationRules.DaysPerYear(Fixture.Config);

        runner.RequestSeclude(5);
        for (var i = 0; i < 20_000 && runner.Phase == GamePhase.Playing; i++) runner.TickOnce();

        await Assert.That(runner.Phase).IsEqualTo(GamePhase.Dead);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.DeathLog)))).IsTrue();
    }

    [Test]
    public async Task monthly_settlement_system_moves_the_world_while_the_player_secludes()
    {
        using var runner = Fixture.NewRunner();
        var world = runner.Current;
        var agesBefore = runner.Npcs.ToDictionary(e => e, e => world.GetComponent<Cultivator>(e).AgeDays);
        var strengthBefore = runner.Npcs.Sum(e =>
        {
            var c = world.GetComponent<Cultivator>(e);
            return c.RealmIndex * 1000.0 + c.SubStage * 100.0 + c.CultivationPoints;
        });

        runner.RequestSeclude(10);
        Fixture.RunUntilIdle(runner);

        var after = runner.Current;
        var survivors = runner.Npcs
            .Where(e => agesBefore.ContainsKey(e) && after.GetComponent<NpcState>(e).Alive != 0)
            .ToList();
        await Assert.That(survivors.Count).IsGreaterThan(0);
        foreach (var entity in survivors)
        {
            await Assert.That(after.GetComponent<Cultivator>(entity).AgeDays).IsGreaterThan(agesBefore[entity]);
        }
        var strengthAfter = runner.Npcs.Sum(e =>
        {
            var c = after.GetComponent<Cultivator>(e);
            return c.RealmIndex * 1000.0 + c.SubStage * 100.0 + c.CultivationPoints;
        });
        await Assert.That(strengthAfter).IsGreaterThan(strengthBefore);
    }

    [Test]
    public async Task dead_npcs_are_replaced_by_new_cultivators()
    {
        using var runner = Fixture.NewRunner();
        var doomed = runner.Npcs[0];
        var site = runner.Current.GetComponent<NpcState>(doomed).SiteIndex;
        int Population() => runner.Npcs.Count(e =>
        {
            var npc = runner.Current.GetComponent<NpcState>(e);
            return npc.Alive != 0 && npc.SiteIndex == site;
        });
        var populationBefore = Population();

        // Push the NPC past their realm's lifespan; the next crossed month reaps them.
        var realmIndex = runner.Current.GetComponent<Cultivator>(doomed).RealmIndex;
        runner.Current.GetComponent<Cultivator>(doomed).AgeDays =
            (Fixture.Config.Realms[realmIndex].LifespanYears + 1.0) * CultivationRules.DaysPerYear(Fixture.Config);

        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.Current.GetComponent<NpcState>(doomed).Alive).IsEqualTo((byte)0);
        await Assert.That(Population()).IsEqualTo(populationBefore);
    }

    // Travel mechanics moved to PathfindingTests (terrain-cost A*, flight, WASD steps).
}
