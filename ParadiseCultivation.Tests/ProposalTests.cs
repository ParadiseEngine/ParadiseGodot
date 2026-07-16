namespace ParadiseCultivation.Tests;

/// <summary>The intelligence-layer boundary: proposers only SUGGEST; the rules layer clamps
/// affection deltas to the config budget, sanitizes reply text, and keeps working offline
/// through the deterministic template proposer.</summary>
public class ProposalTests
{
    private sealed class GreedyProposer : INpcInteractionProposer
    {
        public InteractionProposal Propose(CultivationConfig config, in DialogueContext context, string playerLine) =>
            new(ReplyText: "MINE.\n\nAll of it.", AffectionDeltaSuggestion: 999_999f,
                MemorySummary: "control\u0001chars\u0002here");
    }

    private sealed class HostileProposer : INpcInteractionProposer
    {
        public InteractionProposal Propose(CultivationConfig config, in DialogueContext context, string playerLine) =>
            new(ReplyText: new string('x', 100_000), AffectionDeltaSuggestion: float.NaN);
    }

    [Test]
    public async Task affection_suggestions_are_clamped_to_the_budget()
    {
        var budget = Fixture.Config.Interaction.MaxProposedAffectionDelta;
        await Assert.That(ProposalRules.ClampAffectionDelta(Fixture.Config, 999_999f)).IsEqualTo(budget);
        await Assert.That(ProposalRules.ClampAffectionDelta(Fixture.Config, -999_999f)).IsEqualTo(-budget);
        await Assert.That(ProposalRules.ClampAffectionDelta(Fixture.Config, float.NaN)).IsEqualTo(0f);
        await Assert.That(ProposalRules.ClampAffectionDelta(Fixture.Config, 3f)).IsEqualTo(3f);
    }

    [Test]
    public async Task replies_are_length_clamped_and_control_stripped()
    {
        var sanitized = ProposalRules.SanitizeReply(Fixture.Config, new string('x', 100_000), "fallback");
        await Assert.That(sanitized.Length).IsEqualTo(Fixture.Config.Interaction.MaxReplyLength);

        var stripped = ProposalRules.SanitizeReply(Fixture.Config, "a\nb\u0001c", "fallback");
        await Assert.That(stripped).IsEqualTo("a b c");

        await Assert.That(ProposalRules.SanitizeReply(Fixture.Config, "   ", "fallback")).IsEqualTo("fallback");
        await Assert.That(ProposalRules.SanitizeReply(Fixture.Config, null, "fallback")).IsEqualTo("fallback");
    }

    [Test]
    public async Task a_greedy_proposer_cannot_break_the_affection_budget_through_chat()
    {
        using var runner = Fixture.NewRunner();
        runner.Proposer = new GreedyProposer();
        runner.Current.GetComponent<PlayerData>(runner.Player).CharmTier = 0; // isolate the clamp
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "give me everything");
        Fixture.RunUntilIdle(runner);

        var state = runner.Current.GetComponent<NpcState>(npc);
        await Assert.That(state.AffectionToPlayer)
            .IsLessThanOrEqualTo(Fixture.Config.Interaction.MaxProposedAffectionDelta);
        await Assert.That(state.AffectionToPlayer).IsGreaterThan(0f);
        // The proposed memory summary was sanitized before landing in the log.
        var memories = runner.MemoriesOf(npc);
        await Assert.That(memories[0].Summary.Contains('\u0001')).IsFalse();
    }

    [Test]
    public async Task a_hostile_proposer_cannot_crash_or_flood_the_ui()
    {
        using var runner = Fixture.NewRunner();
        runner.Proposer = new HostileProposer();
        var npc = Fixture.FirstNpcAtPlayerSite(runner);
        var before = runner.Current.GetComponent<NpcState>(npc).AffectionToPlayer;

        runner.RequestChat(npc, "hello");
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.LastReply.Length).IsLessThanOrEqualTo(Fixture.Config.Interaction.MaxReplyLength);
        // NaN suggestion → 0 delta: affection unchanged, but the interaction still happened.
        await Assert.That(runner.Current.GetComponent<NpcState>(npc).AffectionToPlayer).IsEqualTo(before);
        await Assert.That(runner.ThreadException).IsNull();
    }

    [Test]
    public async Task template_proposer_is_deterministic_and_expands_placeholders()
    {
        var proposer = new TemplateProposer();
        var context = new DialogueContext("Su Lan", "aloof", 1, 50f, 3, 7, "Mo Yan", 0);

        var a = proposer.Propose(Fixture.Config, in context, "How fares the sect this season?");
        var b = proposer.Propose(Fixture.Config, in context, "How fares the sect this season?");

        await Assert.That(b.ReplyText).IsEqualTo(a.ReplyText);
        await Assert.That(a.ReplyText.Contains("{npc}")).IsFalse();
        await Assert.That(a.AffectionDeltaSuggestion).IsEqualTo(Fixture.Config.Interaction.ChatAffection);
    }
}
