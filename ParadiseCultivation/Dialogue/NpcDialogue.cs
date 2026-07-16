using System.Text;

namespace ParadiseCultivation;

/// <summary>What the intelligence layer knows about a conversation — a plain view assembled
/// by the runner from components + config pools, so proposers never touch the ECS.</summary>
public readonly record struct DialogueContext(
    string NpcName,
    string Personality,
    int NpcRealmIndex,
    float AffectionToPlayer,
    int MemoryCount,
    int NpcId,
    string PlayerName,
    int PlayerRealmIndex);

/// <summary>A structured interaction proposal — the ONLY thing the intelligence layer may
/// return (high-concept v2.0 AI principles: LLMs propose, the rules layer decides).
/// <see cref="AffectionDeltaSuggestion"/> is a SUGGESTION the rules layer clamps to the
/// config budget and then scales by charm; <see cref="MemorySummary"/> is the line written
/// into the NPC's log (null = the rules layer's default record).</summary>
public sealed record InteractionProposal(
    string ReplyText,
    float AffectionDeltaSuggestion,
    string? MemorySummary = null);

/// <summary>The intelligence-layer seam for free-text NPC interaction. Implementations may
/// be an LLM gateway later; every proposal passes <see cref="ProposalRules"/> validation
/// before anything touches authoritative state, and the deterministic
/// <see cref="TemplateProposer"/> is the always-available offline fallback — the core loop
/// must never require network access.</summary>
public interface INpcInteractionProposer
{
    InteractionProposal Propose(CultivationConfig config, in DialogueContext context, string playerLine);
}

/// <summary>Rule-layer validation of interaction proposals: the schema is the C# type; this
/// applies the budget (affection-delta clamp) and safety (reply sanitization) passes. The
/// rules layer remains the only writer of authoritative state.</summary>
public static class ProposalRules
{
    /// <summary>Clamp the suggestion into ±<c>interaction.maxProposedAffectionDelta</c>.</summary>
    public static float ClampAffectionDelta(CultivationConfig config, float suggestion)
    {
        var budget = config.Interaction.MaxProposedAffectionDelta;
        if (float.IsNaN(suggestion)) return 0f;
        return Math.Clamp(suggestion, -budget, budget);
    }

    /// <summary>Length-clamp and strip control characters (keeps newlines out of one-line
    /// logs). Empty/whitespace replies fall back to <paramref name="fallback"/>.</summary>
    public static string SanitizeReply(CultivationConfig config, string? reply, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reply)) return fallback;
        var sb = new StringBuilder(Math.Min(reply.Length, config.Interaction.MaxReplyLength));
        foreach (var c in reply)
        {
            if (sb.Length >= config.Interaction.MaxReplyLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}

/// <summary>Deterministic offline proposer: keyword intents first (trade / sect joining /
/// dual cultivation hooks from the design doc), otherwise a phrase pool keyed by the NPC's
/// affection tier. Selection hashes (line, npc, MEMORY COUNT) — deterministic per state, so
/// runs replay identically, yet asking the same question again gives a different answer as
/// the shared history grows, and different NPCs answer differently. Suggests the rule-book
/// affection delta, so validated behavior is identical with or without a smarter proposer
/// upstream.</summary>
public sealed class TemplateProposer : INpcInteractionProposer
{
    public InteractionProposal Propose(CultivationConfig config, in DialogueContext context, string playerLine)
    {
        return new InteractionProposal(
            ReplyText: ComposeReply(config, in context, playerLine),
            AffectionDeltaSuggestion: config.Interaction.ChatAffection,
            MemorySummary: null);
    }

    /// <summary>State-derived selection salt: the line, the NPC, and HOW MUCH history they
    /// share. Deterministic for replays/saves, varied across repeated identical questions.</summary>
    private static uint Salt(in DialogueContext context, string line) =>
        StableHash(line) ^ (uint)(context.NpcId * 2654435761) ^ ((uint)context.MemoryCount * 2246822519u);

    private static string ComposeReply(CultivationConfig config, in DialogueContext context, string playerLine)
    {
        var line = playerLine.Trim();
        var lower = line.ToLowerInvariant();
        var salt = Salt(in context, lower);

        foreach (var keyword in config.Dialogue.KeywordReplies)
        {
            foreach (var word in keyword.Keywords)
            {
                if (lower.Contains(word, StringComparison.Ordinal))
                {
                    var variant = (int)(salt % (uint)keyword.Replies.Length);
                    return Expand(keyword.Replies[variant], config, in context);
                }
            }
        }

        // A slice of replies comes from the NPC's personality pool — different temperaments
        // answer the same question differently (hash-deterministic, like everything here).
        if (salt % 100u < config.Dialogue.PersonalityReplyPercent)
        {
            foreach (var pool in config.Dialogue.PersonalityReplies)
            {
                if (pool.Personality == context.Personality && pool.Replies.Length > 0)
                {
                    return Expand(pool.Replies[(int)(salt % (uint)pool.Replies.Length)], config, in context);
                }
            }
        }

        var bucket = config.Dialogue.Buckets[0];
        foreach (var candidate in config.Dialogue.Buckets)
        {
            if (context.AffectionToPlayer >= candidate.MinAffection) bucket = candidate;
        }

        var replies = bucket.Replies;
        return Expand(replies[(int)(salt % (uint)replies.Length)], config, in context);
    }

    private static string Expand(string template, CultivationConfig config, in DialogueContext context)
    {
        var sb = new StringBuilder(template);
        sb.Replace("{npc}", context.NpcName);
        sb.Replace("{player}", context.PlayerName);
        sb.Replace("{personality}", context.Personality);
        sb.Replace("{npcRealm}", config.Realms[context.NpcRealmIndex].Name);
        sb.Replace("{playerRealm}", config.Realms[context.PlayerRealmIndex].Name);
        sb.Replace("{memories}", context.MemoryCount.ToString());
        return sb.ToString();
    }

    /// <summary>FNV-1a — string.GetHashCode is randomized per process and would break
    /// determinism.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }
}
