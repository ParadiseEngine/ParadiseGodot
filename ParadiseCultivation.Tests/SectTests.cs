using System.Text.Json.Nodes;
using Paradise.ECS;

namespace ParadiseCultivation.Tests;

/// <summary>Sect membership (P2): apprenticeship gated on the leader's affection and the
/// spirit root, rank promotion + stipend at monthly settlement, the member cultivation bonus
/// at one's own sect, leaving in person with the affection penalty, and the v3 save fields
/// (with v2 saves still loading sectless).</summary>
public class SectTests
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

    private static void MoveToSect(CultivationRunner runner)
    {
        var site = runner.Map.Sites[SectSiteIndex(runner)];
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        player.X = site.X;
        player.Y = site.Y;
    }

    private static Entity Leader(CultivationRunner runner) =>
        runner.FindSectLeader(runner.Current, SectSiteIndex(runner))
        ?? throw new InvalidOperationException("sect has no living leader");

    private static void SetLeaderAffection(CultivationRunner runner, float affection) =>
        runner.Current.GetComponent<NpcState>(Leader(runner)).AffectionToPlayer = affection;

    private static float LeaderAffection(CultivationRunner runner) =>
        runner.Current.GetComponent<NpcState>(Leader(runner)).AffectionToPlayer;

    private static void SetRealm(CultivationRunner runner, int realmIndex) =>
        runner.Current.GetComponent<Cultivator>(runner.Player).RealmIndex = realmIndex;

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    private static CultivationRunner JoinedRunner(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        MoveToSect(runner);
        SetLeaderAffection(runner, Fixture.Config.Sect.JoinMinLeaderAffection + 50);
        runner.RequestJoinSect();
        Fixture.RunUntilIdle(runner);
        return runner;
    }

    [Test]
    public async Task apprenticeship_gates_on_the_leaders_regard()
    {
        using var runner = Fixture.NewRunner();
        MoveToSect(runner);
        SetLeaderAffection(runner, Fixture.Config.Sect.JoinMinLeaderAffection - 1);

        runner.RequestJoinSect();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.JoinNeedAffectionMsg));
        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(-1);
        await Assert.That(runner.Busy).IsFalse(); // refusals cost no time
    }

    [Test]
    public async Task joining_needs_a_sect_underfoot()
    {
        using var runner = Fixture.NewRunner(); // spawns at the home town
        runner.RequestJoinSect();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.JoinNoSectMsg);
        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task the_spirit_root_gate_can_refuse_apprenticeship()
    {
        var strictConfig = Fixture.Config with
        {
            Sect = Fixture.Config.Sect with { JoinMinSpiritRootGrade = 99 },
        };
        using var runner = new CultivationRunner(strictConfig, seed: 12345);
        runner.RequestBeginJourney();
        runner.TickOnce();
        MoveToSect(runner);
        SetLeaderAffection(runner, strictConfig.Sect.JoinMinLeaderAffection + 50);

        runner.RequestJoinSect();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.JoinNeedRootMsg);
        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task a_qualified_disciple_joins_at_the_gate_rank()
    {
        using var runner = JoinedRunner();

        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(SectSiteIndex(runner));
        await Assert.That(Player(runner).SectRank).IsEqualTo(0);
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.JoinDoneMsg));
        // The ceremony is chronicled AND remembered by the master.
        var log = Fixture.Skeleton(Fixture.Config.Text.Messages.JoinSectLog);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(log))).IsTrue();
        await Assert.That(runner.MemoriesOf(Leader(runner)).Any(m => m.Summary.Contains(log))).IsTrue();

        // One sect at a time: petitioning again is refused by name.
        runner.RequestJoinSect();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.JoinAlreadyMemberMsg));
    }

    [Test]
    public async Task monthly_settlement_pays_the_stipend_and_promotes_by_realm()
    {
        using var runner = JoinedRunner();
        var ranks = Fixture.Config.Sect.Ranks;

        // A plain month at the gate rank: exactly the outer-disciple stipend arrives.
        var stonesBefore = Player(runner).SpiritStones;
        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);
        await Assert.That(Player(runner).SpiritStones)
            .IsEqualTo(stonesBefore + ranks[0].MonthlyStipendStones);
        await Assert.That(Player(runner).SectRank).IsEqualTo(0);

        // Realm jumps past two rank thresholds: one settlement promotes through BOTH and
        // pays the new rank's stipend.
        SetRealm(runner, ranks[2].MinRealmIndex);
        stonesBefore = Player(runner).SpiritStones;
        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);
        await Assert.That(Player(runner).SectRank).IsEqualTo(2);
        await Assert.That(Player(runner).SpiritStones)
            .IsEqualTo(stonesBefore + ranks[2].MonthlyStipendStones);
        var promote = Fixture.Skeleton(Fixture.Config.Text.Messages.SectPromoteLog);
        await Assert.That(runner.Chronicle.Count(e => e.Summary.Contains(promote))).IsEqualTo(2);
    }

    [Test]
    public async Task training_at_ones_own_sect_cultivates_faster()
    {
        using var runner = JoinedRunner();
        var world = runner.Current;
        var cultivator = world.GetComponent<Cultivator>(runner.Player);
        var member = world.GetComponent<PlayerData>(runner.Player);
        var outsider = member with { SectSiteIndex = -1 };

        var memberGain = CultivationRules.MonthlyCultivationGain(Fixture.Config, runner.Map, in cultivator, in member);
        var outsiderGain = CultivationRules.MonthlyCultivationGain(Fixture.Config, runner.Map, in cultivator, in outsider);

        // Ratio compare with a float-friendly tolerance — the gain pipeline rounds per step.
        var ratio = memberGain / outsiderGain;
        await Assert.That(Math.Abs(ratio - (1.0 + Fixture.Config.Sect.MemberCultivationBonus)) < 1e-3).IsTrue();
    }

    [Test]
    public async Task leaving_must_happen_at_the_gate_and_costs_the_leaders_regard()
    {
        using var runner = JoinedRunner();

        // Away from the mountain: the goodbye must be said in person.
        SetPositionAway(runner);
        runner.RequestLeaveSect();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.LeaveNotHereMsg);
        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(SectSiteIndex(runner));

        // At the gate: membership ends and the penalty lands un-scaled on the leader.
        MoveToSect(runner);
        var affectionBefore = LeaderAffection(runner);
        runner.RequestLeaveSect();
        runner.TickOnce();
        await Assert.That(Player(runner).SectSiteIndex).IsEqualTo(-1);
        await Assert.That(LeaderAffection(runner))
            .IsEqualTo(affectionBefore + Fixture.Config.Sect.LeaveAffectionPenalty);
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LeaveDoneMsg));
    }

    private static void SetPositionAway(CultivationRunner runner)
    {
        var map = runner.Map;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (map.TileAt(x, y).SiteIndex < 0 && Pathfinding.IsWalkable(Fixture.Config, map.TileAt(x, y)))
                {
                    ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
                    player.X = x;
                    player.Y = y;
                    return;
                }
            }
        }
        throw new InvalidOperationException("no wilderness tile found");
    }

    [Test]
    public async Task membership_rides_the_save_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cultsect_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = JoinedRunner();
            runner.RequestSave(path);
            runner.TickOnce();

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            await Assert.That(Player(restored).SectSiteIndex).IsEqualTo(SectSiteIndex(runner));
            await Assert.That(Player(restored).SectRank).IsEqualTo(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task v2_saves_load_sectless()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cultv2_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = JoinedRunner();
            runner.RequestSave(path);
            runner.TickOnce();

            // Rewrite as a v2 file: no sect fields. If absent-field handling regressed to the
            // int default, SectSiteIndex would read 0 — a VALID site — not -1.
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["version"] = 2;
            node["player"]!.AsObject().Remove("sectSiteIndex");
            node["player"]!.AsObject().Remove("sectRank");
            File.WriteAllText(path, node.ToJsonString());

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            await Assert.That(restored.LastActionResult)
                .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadDoneMsg));
            await Assert.That(Player(restored).SectSiteIndex).IsEqualTo(-1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
