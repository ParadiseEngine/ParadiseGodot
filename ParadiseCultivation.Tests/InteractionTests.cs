namespace ParadiseCultivation.Tests;

/// <summary>Memory-driven NPC relationships through the command queue: two-way affection
/// with charm scaling and diminishing chat returns, persistent memory logs (sim-thread side
/// store), deterministic template dialogue, and the affection tier table from the design
/// doc.</summary>
public class InteractionTests
{
    [Test]
    public async Task chatting_raises_two_way_affection_and_writes_memory()
    {
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(entity, "Greetings, fellow daoist.");
        Fixture.RunUntilIdle(runner);

        var npc = runner.Current.GetComponent<NpcState>(entity);
        await Assert.That(runner.LastReply.Length).IsGreaterThan(0);
        await Assert.That(npc.AffectionToPlayer).IsGreaterThan(0f);
        await Assert.That(npc.PlayerAffection).IsGreaterThan(0f);
        var memories = runner.MemoriesOf(entity);
        await Assert.That(memories.Count).IsEqualTo(1);
        var playerName = CultivationRules.PlayerName(
            Fixture.Config, runner.Current.GetComponent<PlayerData>(runner.Player));
        await Assert.That(memories[0].Summary).Contains(playerName);
    }

    [Test]
    public async Task repeated_chat_in_one_month_has_diminishing_returns()
    {
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(entity, "hello");
        Fixture.RunUntilIdle(runner);
        var afterFirst = runner.Current.GetComponent<NpcState>(entity).AffectionToPlayer;

        runner.RequestChat(entity, "hello again");
        Fixture.RunUntilIdle(runner);
        var secondGain = runner.Current.GetComponent<NpcState>(entity).AffectionToPlayer - afterFirst;

        await Assert.That(secondGain).IsLessThan(afterFirst);
        await Assert.That(secondGain).IsGreaterThan(0f);
    }

    [Test]
    public async Task charm_multiplies_positive_affection_gains()
    {
        using var plain = Fixture.NewRunner();
        using var charming = Fixture.NewRunner();
        plain.Current.GetComponent<PlayerData>(plain.Player).CharmTier = 0;
        charming.Current.GetComponent<PlayerData>(charming.Player).CharmTier =
            Fixture.Config.CharmTiers.Length - 1;

        var plainNpc = Fixture.FirstNpcAtPlayerSite(plain);
        var charmingNpc = Fixture.FirstNpcAtPlayerSite(charming);
        plain.RequestChat(plainNpc, "hello");
        charming.RequestChat(charmingNpc, "hello");
        Fixture.RunUntilIdle(plain);
        Fixture.RunUntilIdle(charming);

        var expected = Fixture.Config.CharmTiers[^1].Multiplier / Fixture.Config.CharmTiers[0].Multiplier;
        var ratio = charming.Current.GetComponent<NpcState>(charmingNpc).AffectionToPlayer
            / plain.Current.GetComponent<NpcState>(plainNpc).AffectionToPlayer;
        await Assert.That(Math.Abs(ratio - expected)).IsLessThan(0.001f);
    }

    [Test]
    public async Task gifting_costs_stones_and_raises_affection()
    {
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);
        var stonesBefore = runner.Current.GetComponent<PlayerData>(runner.Player).SpiritStones;

        runner.RequestGift(entity);
        Fixture.RunUntilIdle(runner);

        var npc = runner.Current.GetComponent<NpcState>(entity);
        await Assert.That(runner.Current.GetComponent<PlayerData>(runner.Player).SpiritStones)
            .IsEqualTo(stonesBefore - Fixture.Config.Interaction.GiftSpiritStones);
        await Assert.That(npc.AffectionToPlayer).IsGreaterThan(0f);
        await Assert.That(runner.LastReply).Contains(CultivationRules.NpcName(Fixture.Config, in npc));

        runner.Current.GetComponent<PlayerData>(runner.Player).SpiritStones = 0;
        runner.RequestGift(entity);
        Fixture.RunUntilIdle(runner);
        await Assert.That(runner.LastReply).Contains("need");
    }

    [Test]
    public async Task sparring_is_consequence_free_and_builds_respect()
    {
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestSpar(entity);
        Fixture.RunUntilIdle(runner);

        var npc = runner.Current.GetComponent<NpcState>(entity);
        await Assert.That(npc.AffectionToPlayer).IsGreaterThan(0f);
        await Assert.That(npc.Alive).IsEqualTo((byte)1);
        await Assert.That(runner.MemoriesOf(entity).Count).IsEqualTo(1);
    }

    [Test]
    public async Task affection_tier_table_matches_the_design_doc()
    {
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, -450f)).IsEqualTo("Mortal Enemy");
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, 0f)).IsEqualTo("Stranger");
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, 150f)).IsEqualTo("Acquaintance");
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, 350f)).IsEqualTo("Friend");
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, 650f)).IsEqualTo("Confidant");
        await Assert.That(CultivationRules.AffectionTierName(Fixture.Config, 950f)).IsEqualTo("Dao Partner");
    }

    [Test]
    public async Task dialogue_is_deterministic_and_expands_placeholders()
    {
        var dialogue = new TemplateDialogue();
        var context = new DialogueContext(
            "Su Lan", "aloof", 1, 50f, 3, 7, "Mo Yan", 0);

        var a = dialogue.Reply(Fixture.Config, in context, "How fares the sect this season?");
        var b = dialogue.Reply(Fixture.Config, in context, "How fares the sect this season?");

        await Assert.That(b).IsEqualTo(a);
        await Assert.That(a.Contains("{npc}")).IsFalse(); // placeholders must expand
    }

    [Test]
    public async Task keyword_intents_route_to_their_reply()
    {
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(entity, "I want to buy that sword.");
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.LastReply).Contains("Trade");
    }

    [Test]
    public async Task npc_memory_survives_long_seclusion()
    {
        // The pitch demo beat: talk, vanish for years, return — the log is still there.
        using var runner = Fixture.NewRunner();
        var entity = Fixture.FirstNpcAtPlayerSite(runner);
        runner.RequestChat(entity, "Remember me.");
        Fixture.RunUntilIdle(runner);

        runner.RequestSeclude(20);
        Fixture.RunUntilIdle(runner);

        var memories = runner.MemoriesOf(entity);
        await Assert.That(memories.Count).IsEqualTo(1);
        await Assert.That(memories[0].Summary).Contains("Remember me.");
    }
}
