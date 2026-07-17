using System.Collections.Concurrent;

namespace ParadiseCultivation.Tests;

/// <summary>The async LLM layer's contract: template text always displays first, LLM text is
/// strings-only flavor that re-enters through the command queue, stale results die, every
/// reply passes sanitize + glyph filtering, and the deterministic world (components, RNG,
/// affection) is byte-identical with or without the layer.</summary>
public class LlmTests
{
    /// <summary>Hand-completed fake: each request parks on its own TCS until the test
    /// releases it, so in-flight ordering and staleness are testable deterministically.</summary>
    private sealed class FakeLlm : ILlmTextService
    {
        private readonly ConcurrentQueue<TaskCompletionSource<string?>> _pending = new();
        public string? LastSystemPrompt;
        public string? LastUserPrompt;
        public int Calls;

        public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(tcs);
            return tcs.Task;
        }

        /// <summary>Dispatch rides the thread pool, so the request may not have STARTED yet
        /// when the sim goes idle — wait for it before completing.</summary>
        public void CompleteOldest(string? text, int timeoutMs = 5_000)
        {
            for (var waited = 0; waited < timeoutMs; waited++)
            {
                if (_pending.TryDequeue(out var tcs))
                {
                    tcs.TrySetResult(text);
                    return;
                }
                Thread.Sleep(1);
            }
            throw new InvalidOperationException("no pending LLM request");
        }

        public void WaitForCall(int count, int timeoutMs = 5_000)
        {
            for (var waited = 0; waited < timeoutMs; waited++)
            {
                if (Volatile.Read(ref Calls) >= count) return;
                Thread.Sleep(1);
            }
            throw new InvalidOperationException($"LLM never reached {count} call(s)");
        }
    }

    /// <summary>Tick until <paramref name="done"/> — the async completion re-enters through
    /// the command queue, so the test must keep pumping while the thread pool delivers.</summary>
    private static void PumpUntil(CultivationRunner runner, Func<bool> done, int maxTicks = 5_000)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            runner.TickOnce();
            if (done()) return;
            Thread.Sleep(1);
        }
        throw new InvalidOperationException($"condition not reached after {maxTicks} ticks");
    }

    /// <summary>Give any in-flight completion ample time to land, then assert nothing changed.</summary>
    private static void PumpAWhile(CultivationRunner runner, int ticks = 300)
    {
        for (var i = 0; i < ticks; i++)
        {
            runner.TickOnce();
            Thread.Sleep(1);
        }
    }

    private static (CultivationRunner Runner, FakeLlm Llm) NewLlmRunner(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        var fake = new FakeLlm();
        runner.Llm = fake;
        return (runner, fake);
    }

    [Test]
    public async Task the_template_reply_shows_first_and_the_llm_reply_replaces_it()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "道友近来可好");
        Fixture.RunUntilIdle(runner);

        // The deterministic template answered synchronously; the request is in flight.
        var templateReply = runner.LastReply;
        await Assert.That(templateReply.Length).IsGreaterThan(0);
        llm.WaitForCall(1);
        await Assert.That(llm.Calls).IsEqualTo(1);
        // The prompt carried the authored context (NPC identity reaches the model).
        await Assert.That(llm.LastUserPrompt!.Contains("道友近来可好")).IsTrue();

        llm.CompleteOldest("贫道一切安好，多谢挂怀。");
        PumpUntil(runner, () => runner.LastReply == "贫道一切安好，多谢挂怀。");
        await Assert.That(runner.ThreadException).IsNull();
    }

    [Test]
    public async Task a_stale_reply_never_stomps_a_newer_interaction()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "第一句");
        Fixture.RunUntilIdle(runner);
        runner.RequestGift(npc); // gift owns LastReply now — the chat request is stale
        Fixture.RunUntilIdle(runner);
        var giftReply = runner.LastReply;

        llm.CompleteOldest("过时的回答");
        PumpAWhile(runner);

        await Assert.That(runner.LastReply).IsEqualTo(giftReply);
    }

    [Test]
    public async Task llm_replies_are_sanitized_and_filtered_to_baked_glyphs()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        runner.GlyphSource = "道友安好贫";
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "hello");
        Fixture.RunUntilIdle(runner);

        // Unbaked glyphs (✨, 拜) drop; the control char becomes a space; ASCII passes.
        llm.CompleteOldest("道友✨安好\u0001拜ok" + new string('x', 100_000));
        PumpUntil(runner, () => runner.LastReply.StartsWith("道友安好", StringComparison.Ordinal));

        await Assert.That(runner.LastReply.Contains('✨')).IsFalse();
        await Assert.That(runner.LastReply.Contains('拜')).IsFalse();
        await Assert.That(runner.LastReply.Contains('\u0001')).IsFalse();
        await Assert.That(runner.LastReply.Contains("ok")).IsTrue();
        await Assert.That(runner.LastReply.Length)
            .IsLessThanOrEqualTo(Fixture.Config.Interaction.MaxReplyLength);
    }

    [Test]
    public async Task the_llm_layer_never_perturbs_the_deterministic_world()
    {
        var (withLlm, llm) = NewLlmRunner();
        using var runnerA = withLlm;
        using var runnerB = Fixture.NewRunner(); // same seed, no LLM

        var npcA = Fixture.FirstNpcAtPlayerSite(runnerA);
        var npcB = Fixture.FirstNpcAtPlayerSite(runnerB);

        runnerA.RequestChat(npcA, "你好");
        Fixture.RunUntilIdle(runnerA);
        llm.CompleteOldest("与规则无关的花哨回答，随便多少好感度都不该变。");
        PumpUntil(runnerA, () => runnerA.LastReply.Contains("花哨"));

        runnerB.RequestChat(npcB, "你好");
        Fixture.RunUntilIdle(runnerB);

        // Affection (authoritative, saved, snapshot-compared) is rules-derived only.
        var stateA = runnerA.Current.GetComponent<NpcState>(npcA);
        var stateB = runnerB.Current.GetComponent<NpcState>(npcB);
        await Assert.That(stateA.AffectionToPlayer).IsEqualTo(stateB.AffectionToPlayer);
        // The memory record stays the deterministic rules line, not the LLM text.
        await Assert.That(runnerA.MemoriesOf(npcA)[0].Summary)
            .IsEqualTo(runnerB.MemoriesOf(npcB)[0].Summary);
    }

    [Test]
    public async Task event_narration_rewrites_exactly_its_chronicle_line()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;

        // Find the first scheduled event month for this world and cultivate up to it.
        var seed = runner.Map.GenerationSeed;
        long eventMonth = -1;
        for (var month = 1L; month < 600; month++)
        {
            if (WorldEvents.TryGetForMonth(Fixture.Config, seed, month) is not null)
            {
                eventMonth = month;
                break;
            }
        }
        await Assert.That(eventMonth).IsGreaterThan(0);

        runner.RequestCultivate((int)eventMonth);
        Fixture.RunUntilIdle(runner);

        var expected = WorldEvents.TryGetForMonth(Fixture.Config, seed, eventMonth)!.Value;
        var index = runner.Chronicle.FindIndex(entry => entry.Summary.Contains(expected.Name));
        await Assert.That(index).IsGreaterThanOrEqualTo(0);

        llm.CompleteOldest("是岁天地异动，青史另有一笔。");
        PumpUntil(runner, () => runner.Chronicle[index].Summary == "是岁天地异动，青史另有一笔。");
        await Assert.That(runner.ThreadException).IsNull();
    }

    [Test]
    public async Task loading_a_save_discards_in_flight_llm_results()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);
        var path = Path.Combine(Path.GetTempPath(), $"cultivation-llm-{Guid.NewGuid():N}.json");
        try
        {
            runner.RequestSave(path);
            runner.TickOnce();

            runner.RequestChat(npc, "你好");
            Fixture.RunUntilIdle(runner);

            runner.RequestLoad(path); // the pending chat reply belongs to the pre-load world
            runner.TickOnce();
            llm.CompleteOldest("不该出现的回答");
            PumpAWhile(runner);

            await Assert.That(runner.LastReply).IsEqualTo(string.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task without_a_wired_service_no_request_ever_fires()
    {
        using var runner = Fixture.NewRunner();
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "你好");
        Fixture.RunUntilIdle(runner);

        await Assert.That(runner.LastReply.Length).IsGreaterThan(0);
        await Assert.That(runner.ThreadException).IsNull();
    }

    // ---- the full-proposal path: the LLM also suggests an affection delta ----

    [Test]
    public async Task the_parser_extracts_json_proposals_and_degrades_to_plain_text()
    {
        var (reply, affection) = LlmProposalParser.Parse(
            "好的，输出如下：{\"reply\":\"道友请。\",\"affection\":3} 完毕。");
        await Assert.That(reply).IsEqualTo("道友请。");
        await Assert.That(affection).IsEqualTo(3f);

        var (plain, none) = LlmProposalParser.Parse("贫道一切安好。");
        await Assert.That(plain).IsEqualTo("贫道一切安好。");
        await Assert.That(none).IsNull();

        var (broken, alsoNone) = LlmProposalParser.Parse("{oops not json}");
        await Assert.That(broken).IsEqualTo("{oops not json}");
        await Assert.That(alsoNone).IsNull();
    }

    [Test]
    public async Task a_greedy_json_suggestion_is_clamped_to_the_same_budget_as_any_proposer()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        runner.Current.GetComponent<PlayerData>(runner.Player).CharmTier = 0;
        var charm = Fixture.Config.CharmTiers[0].Multiplier;
        var budget = Fixture.Config.Interaction.MaxProposedAffectionDelta;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "给我全部好感"); // first chat: diminishing divisor is 1
        Fixture.RunUntilIdle(runner);

        llm.CompleteOldest("{\"reply\":\"痴心妄想。\",\"affection\":999999}");
        PumpUntil(runner, () => runner.LastReply == "痴心妄想。");

        // Exactly what a max-budget SYNCHRONOUS proposer would have produced.
        var state = runner.Current.GetComponent<NpcState>(npc);
        await Assert.That(state.AffectionToPlayer).IsEqualTo(budget * charm).Within(1e-3f);
        await Assert.That(state.PlayerAffection)
            .IsEqualTo(budget * charm * Fixture.Config.Interaction.PlayerAffectionShare).Within(1e-3f);
    }

    [Test]
    public async Task a_hostile_negative_suggestion_is_clamped_and_lands_unscaled()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        runner.Current.GetComponent<PlayerData>(runner.Player).CharmTier = 0;
        var budget = Fixture.Config.Interaction.MaxProposedAffectionDelta;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "你好");
        Fixture.RunUntilIdle(runner);

        llm.CompleteOldest("{\"reply\":\"滚。\",\"affection\":-999999}");
        PumpUntil(runner, () => runner.LastReply == "滚。");

        // The floor is −budget (negatives are never charm-scaled), divisor 1 on a first chat.
        var state = runner.Current.GetComponent<NpcState>(npc);
        await Assert.That(state.AffectionToPlayer).IsEqualTo(-budget).Within(1e-3f);
    }

    [Test]
    public async Task a_plain_text_reply_carries_no_affection_suggestion()
    {
        var (runner, llm) = NewLlmRunner();
        using var _ = runner;
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        runner.RequestChat(npc, "你好");
        Fixture.RunUntilIdle(runner);
        var afterTemplate = runner.Current.GetComponent<NpcState>(npc).AffectionToPlayer;

        llm.CompleteOldest("不带任何格式的自由回答。");
        PumpUntil(runner, () => runner.LastReply == "不带任何格式的自由回答。");

        await Assert.That(runner.Current.GetComponent<NpcState>(npc).AffectionToPlayer)
            .IsEqualTo(afterTemplate);
    }
}
