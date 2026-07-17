using System.Text.Json.Nodes;

namespace ParadiseCultivation.Tests;

/// <summary>The town-market slice (P2 trade): herbs sell for stones at the town's price,
/// breakthrough pills gate on stock AND stones, shelves restock on month crossings, the pill
/// is consumed by the breakthrough attempt, and the trade state rides the v2 save — with v1
/// saves still loading through defaults.</summary>
public class TradeTests
{
    // Component pokes live in non-async helpers — ref locals are not allowed in async tests.

    private static void SetHerbs(CultivationRunner runner, int herbs) =>
        runner.Current.GetComponent<PlayerData>(runner.Player).Herbs = herbs;

    private static void SetStones(CultivationRunner runner, int stones) =>
        runner.Current.GetComponent<PlayerData>(runner.Player).SpiritStones = stones;

    private static void SetPills(CultivationRunner runner, int pills) =>
        runner.Current.GetComponent<PlayerData>(runner.Player).Pills = pills;

    private static void SetPosition(CultivationRunner runner, int x, int y)
    {
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        player.X = x;
        player.Y = y;
    }

    private static void MakeBreakthroughReady(CultivationRunner runner)
    {
        ref var cultivator = ref runner.Current.GetComponent<Cultivator>(runner.Player);
        cultivator.SubStage = Fixture.Config.SubStages.Length - 1;
        cultivator.CultivationPoints = Fixture.Config.Realms[cultivator.RealmIndex].PointsPerSubStage;
    }

    private static int PlayerSiteIndex(CultivationRunner runner)
    {
        var player = runner.Current.GetComponent<PlayerData>(runner.Player);
        return runner.Map.TileAt(player.X, player.Y).SiteIndex;
    }

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    [Test]
    public async Task herbs_sell_at_the_town_price()
    {
        using var runner = Fixture.NewRunner(); // spawns at the home town
        SetHerbs(runner, 10);
        var siteIndex = PlayerSiteIndex(runner);
        var price = CultivationRules.HerbSellStones(Fixture.Config, runner.Map, siteIndex, runner.Day);
        var stonesBefore = Player(runner).SpiritStones;

        runner.RequestSellHerbs(10);
        Fixture.RunUntilIdle(runner);

        await Assert.That(Player(runner).Herbs).IsEqualTo(0);
        await Assert.That(Player(runner).SpiritStones).IsEqualTo(stonesBefore + 10 * price);
        await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.SellDoneMsg));
    }

    [Test]
    public async Task selling_with_an_empty_pouch_is_refused()
    {
        using var runner = Fixture.NewRunner();
        SetHerbs(runner, 0);

        runner.RequestSellHerbs(5);
        runner.TickOnce();

        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.SellNothingMsg);
        await Assert.That(runner.Busy).IsFalse(); // a refusal costs no time
    }

    [Test]
    public async Task trading_needs_a_town_underfoot()
    {
        using var runner = Fixture.NewRunner();
        // Find any walkable non-site tile and stand there.
        var map = runner.Map;
        var moved = false;
        for (var y = 0; y < map.Height && !moved; y++)
        {
            for (var x = 0; x < map.Width && !moved; x++)
            {
                if (map.TileAt(x, y).SiteIndex < 0 && Pathfinding.IsWalkable(Fixture.Config, map.TileAt(x, y)))
                {
                    SetPosition(runner, x, y);
                    moved = true;
                }
            }
        }
        await Assert.That(moved).IsTrue();
        SetHerbs(runner, 5);

        runner.RequestSellHerbs(5);
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.TradeNoMarketMsg);

        runner.RequestBuyPill();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.TradeNoMarketMsg);
        await Assert.That(Player(runner).Herbs).IsEqualTo(5);
    }

    [Test]
    public async Task pills_gate_on_stones_and_stock_and_shelves_restock_monthly()
    {
        using var runner = Fixture.NewRunner();
        var siteIndex = PlayerSiteIndex(runner);
        var price = CultivationRules.PillCostStones(Fixture.Config, runner.Map, siteIndex, runner.Day);
        var stock = Fixture.Config.Trade.PillStockPerTown;

        // Too poor: the need-stones refusal names the price.
        SetStones(runner, price - 1);
        runner.RequestBuyPill();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.BuyPillNeedStonesMsg));
        await Assert.That(Player(runner).Pills).IsEqualTo(0);

        // Rich enough: buy the whole shelf, then hit the out-of-stock refusal.
        SetStones(runner, price * (stock + 1));
        for (var i = 0; i < stock; i++)
        {
            runner.RequestBuyPill();
            Fixture.RunUntilIdle(runner);
        }
        await Assert.That(Player(runner).Pills).IsEqualTo(stock);
        await Assert.That(runner.TownPillStock[siteIndex]).IsEqualTo(0);

        runner.RequestBuyPill();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.BuyPillNoStockMsg);

        // A month passes — the shelf refills.
        runner.RequestCultivate(1);
        Fixture.RunUntilIdle(runner);
        await Assert.That(runner.TownPillStock[siteIndex]).IsEqualTo(stock);
    }

    [Test]
    public async Task the_breakthrough_attempt_consumes_the_pill()
    {
        using var runner = Fixture.NewRunner();
        MakeBreakthroughReady(runner);
        SetPills(runner, 1);

        runner.RequestBreakthrough();
        Fixture.RunUntilIdle(runner);

        await Assert.That(Player(runner).Pills).IsEqualTo(0);
        await Assert.That(runner.LastActionResult)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.PillUsedNote));
    }

    [Test]
    public async Task trade_state_rides_the_save_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"culttrade_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = Fixture.NewRunner();
            var siteIndex = PlayerSiteIndex(runner);
            var price = CultivationRules.PillCostStones(Fixture.Config, runner.Map, siteIndex, runner.Day);
            SetStones(runner, price);
            runner.RequestBuyPill();
            Fixture.RunUntilIdle(runner);
            var stockAfterBuy = runner.TownPillStock[siteIndex];

            runner.RequestSave(path);
            runner.TickOnce();

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            await Assert.That(Player(restored).Pills).IsEqualTo(1);
            await Assert.That(restored.TownPillStock[siteIndex]).IsEqualTo(stockAfterBuy);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task v1_saves_load_with_default_trade_state()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cultv1_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = Fixture.NewRunner();
            runner.RequestSave(path);
            runner.TickOnce();

            // Rewrite the save as a v1 file: no trade fields, version 1.
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["version"] = 1;
            node.Remove("townPillStock");
            node["player"]!.AsObject().Remove("pills");
            File.WriteAllText(path, node.ToJsonString());

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            await Assert.That(restored.LastActionResult)
                .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadDoneMsg));
            await Assert.That(Player(restored).Pills).IsEqualTo(0);
            var siteIndex = PlayerSiteIndex(restored);
            await Assert.That(restored.TownPillStock[siteIndex])
                .IsEqualTo(Fixture.Config.Trade.PillStockPerTown);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task town_prices_are_deterministic_and_spread_bounded()
    {
        var (map, _) = WorldGenerator.Generate(Fixture.Config, seed: 20260716, presetIndex: 0);
        var spread = Fixture.Config.Trade.PriceSpreadPercent / 100f;
        for (var i = 0; i < map.Sites.Count; i++)
        {
            var factor = CultivationRules.TownPriceMultiplier(Fixture.Config, map, i);
            await Assert.That(factor).IsEqualTo(CultivationRules.TownPriceMultiplier(Fixture.Config, map, i));
            await Assert.That(factor >= 1f - spread - 1e-4f && factor <= 1f + spread + 1e-4f).IsTrue();
            await Assert.That(CultivationRules.HerbSellStones(Fixture.Config, map, i, 0)).IsGreaterThanOrEqualTo(1);
        }
    }
}
