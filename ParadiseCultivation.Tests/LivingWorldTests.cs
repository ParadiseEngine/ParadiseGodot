using Paradise.ECS;

namespace ParadiseCultivation.Tests;

/// <summary>Living world v2: a fallen sect leader's mountain passes to the strongest
/// surviving disciple (a fresh outside leader only for an emptied sect), and hash-scheduled
/// world-life beats write small NPC stories into the chronicle deterministically.</summary>
public class LivingWorldTests
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

    private static void Kill(CultivationRunner runner, Entity npc)
    {
        ref var state = ref runner.Current.GetComponent<NpcState>(npc);
        state.Alive = 0;
        state.JustDied = 1;
    }

    private static List<Entity> LivingAt(CultivationRunner runner, int siteIndex)
    {
        var result = new List<Entity>();
        foreach (var entity in runner.Npcs)
        {
            var npc = runner.Current.GetComponent<NpcState>(entity);
            if (npc.Alive != 0 && npc.SiteIndex == siteIndex) result.Add(entity);
        }
        return result;
    }

    /// <summary>The implementation's succession order: realm, sub-stage, points, seniority.</summary>
    private static Entity StrongestNonLeader(CultivationRunner runner, int siteIndex)
    {
        Entity best = default;
        var found = false;
        (int Realm, int Stage, double Points, int NegId) bestKey = (-1, -1, double.MinValue, int.MinValue);
        foreach (var entity in LivingAt(runner, siteIndex))
        {
            var npc = runner.Current.GetComponent<NpcState>(entity);
            if (npc.IsLeader != 0) continue;
            var c = runner.Current.GetComponent<Cultivator>(entity);
            var key = (c.RealmIndex, c.SubStage, c.CultivationPoints, -npc.NpcId);
            if (!found || key.CompareTo(bestKey) > 0)
            {
                best = entity;
                bestKey = key;
                found = true;
            }
        }
        if (!found) throw new InvalidOperationException("no non-leader disciple at the sect");
        return best;
    }

    [Test]
    public async Task the_strongest_disciple_inherits_the_mountain()
    {
        using var runner = Fixture.NewRunner();
        var site = SectSiteIndex(runner);
        var leader = runner.FindSectLeader(runner.Current, site)
            ?? throw new InvalidOperationException("no leader");
        var heir = StrongestNonLeader(runner, site);

        Kill(runner, leader);
        runner.TickOnce(); // the post-pass consumes the flag

        await Assert.That(runner.Current.GetComponent<NpcState>(heir).IsLeader).IsEqualTo((byte)1);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.SectSuccessionLog)))).IsTrue();

        // Exactly one living leader — the replacement spawn joined as a plain member.
        var leaders = LivingAt(runner, site)
            .Count(entity => runner.Current.GetComponent<NpcState>(entity).IsLeader != 0);
        await Assert.That(leaders).IsEqualTo(1);
    }

    [Test]
    public async Task an_emptied_sect_receives_a_fresh_leader()
    {
        using var runner = Fixture.NewRunner();
        var site = SectSiteIndex(runner);
        foreach (var entity in LivingAt(runner, site))
        {
            Kill(runner, entity); // the whole mountain falls in one month
        }

        runner.TickOnce();

        // Replacements spawned; among them exactly one fresh leader (no heir survived).
        var leaders = LivingAt(runner, site)
            .Count(entity => runner.Current.GetComponent<NpcState>(entity).IsLeader != 0);
        await Assert.That(leaders).IsEqualTo(1);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.SectSuccessionLog)))).IsFalse();
    }

    [Test]
    public async Task world_life_beats_enter_the_chronicle_deterministically()
    {
        var skeletons = Fixture.Config.WorldLife.Beats.Select(Fixture.Skeleton).ToArray();

        static List<string> ChronicleAfterYears(int years)
        {
            using var runner = Fixture.NewRunner();
            runner.Current.GetComponent<PlayerData>(runner.Player).LifespanYears = 10_000;
            runner.RequestSeclude(years);
            Fixture.RunUntilIdle(runner);
            return runner.Chronicle.Select(entry => entry.Summary).ToList();
        }

        var first = ChronicleAfterYears(5);
        var second = ChronicleAfterYears(5);

        // Same seed + same commands → the world tells the same stories.
        await Assert.That(second.SequenceEqual(first)).IsTrue();
        // 60 months at the authored chance yields at least one beat.
        await Assert.That(first.Any(line => skeletons.Any(line.Contains))).IsTrue();
    }
}
