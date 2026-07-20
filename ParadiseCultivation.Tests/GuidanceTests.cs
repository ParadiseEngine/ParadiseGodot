namespace ParadiseCultivation.Tests;

/// <summary>The onboarding guidance ladder: the first UNMET goal is derived from current
/// state (never stored), walks the authored order as milestones fall, and ends at -1 only
/// once the run is won.</summary>
public class GuidanceTests
{
    private static int Next(GamePhase phase, int realmIndex, int sectSiteIndex, int companionNpcId)
    {
        var cultivator = new Cultivator { RealmIndex = realmIndex };
        var player = new PlayerData { SectSiteIndex = sectSiteIndex, CompanionNpcId = companionNpcId };
        return CultivationRules.NextGuidanceGoal(Fixture.Config, phase, in cultivator, in player);
    }

    [Test]
    public async Task the_ladder_walks_the_authored_order_as_milestones_fall()
    {
        var goals = Fixture.Config.Guidance.Goals;
        await Assert.That(goals.Length).IsGreaterThan(0);

        // A fresh mortal stands before the first goal.
        var start = Next(GamePhase.Playing, realmIndex: 0, sectSiteIndex: -1, companionNpcId: -1);
        await Assert.That(start).IsEqualTo(0);

        // Meeting every condition except ascension leaves exactly the ascend goal.
        var lastRealm = Fixture.Config.Realms.Length - 1;
        var beforeAscension = Next(GamePhase.Playing, lastRealm, sectSiteIndex: 0, companionNpcId: 3);
        await Assert.That(beforeAscension).IsEqualTo(goals.Length - 1);
        await Assert.That(goals[beforeAscension].Kind).IsEqualTo(GuidanceKind.Ascend);

        // The won run has no next step.
        var done = Next(GamePhase.Ascended, lastRealm, sectSiteIndex: 0, companionNpcId: 3);
        await Assert.That(done).IsEqualTo(-1);
    }

    [Test]
    public async Task every_goal_is_reachable_in_order()
    {
        // Walking the ladder front to back: satisfying goals 0..i-1 must point at exactly i
        // (the authored order is consistent — no goal is unreachable behind a later one).
        var goals = Fixture.Config.Guidance.Goals;
        var realm = 0;
        var sect = -1;
        var companion = -1;
        for (var i = 0; i < goals.Length; i++)
        {
            await Assert.That(Next(GamePhase.Playing, realm, sect, companion)).IsEqualTo(i);
            switch (goals[i].Kind)
            {
                case GuidanceKind.Realm:
                    realm = Math.Max(realm, goals[i].Value);
                    break;
                case GuidanceKind.Sect:
                    sect = 0;
                    break;
                case GuidanceKind.Companion:
                    companion = 1;
                    break;
                case GuidanceKind.Ascend:
                    break; // the last rung — only winning satisfies it
            }
        }
    }

    [Test]
    public async Task progress_in_a_live_runner_moves_the_pointer()
    {
        using var runner = Fixture.NewRunner();
        var world = runner.Current;
        var before = CultivationRules.NextGuidanceGoal(Fixture.Config, runner.Phase,
            world.GetComponent<Cultivator>(runner.Player), world.GetComponent<PlayerData>(runner.Player));
        await Assert.That(before).IsEqualTo(0);

        // 筑基 reached: the pointer moves off the first realm goal.
        world.GetComponent<Cultivator>(runner.Player).RealmIndex = Fixture.Config.Guidance.Goals[0].Value;
        var after = CultivationRules.NextGuidanceGoal(Fixture.Config, runner.Phase,
            world.GetComponent<Cultivator>(runner.Player), world.GetComponent<PlayerData>(runner.Player));
        await Assert.That(after).IsGreaterThan(0);
    }
}
