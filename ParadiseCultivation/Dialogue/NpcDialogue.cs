using System.Text;

namespace ParadiseCultivation;

/// <summary>What the dialogue layer knows about a conversation — a plain view assembled by
/// the runner from components + config pools, so implementations never touch the ECS.</summary>
public readonly record struct DialogueContext(
    string NpcName,
    string Personality,
    int NpcRealmIndex,
    float AffectionToPlayer,
    int MemoryCount,
    int NpcId,
    string PlayerName,
    int PlayerRealmIndex);

/// <summary>The dialogue seam. The design doc drives NPC conversation with an LLM; this slice
/// ships a deterministic template implementation behind the same interface so an LLM-backed
/// implementation can be swapped in later without touching game or UI code.</summary>
public interface INpcDialogue
{
    string Reply(CultivationConfig config, in DialogueContext context, string playerLine);
}

/// <summary>Deterministic dialogue: keyword intents first (trade / sect joining / dual
/// cultivation hooks from the design doc), otherwise a phrase pool keyed by the NPC's
/// affection tier, indexed by a stable hash of (line, npc) so the same question to the same
/// NPC always gets the same answer — and different NPCs answer differently.</summary>
public sealed class TemplateDialogue : INpcDialogue
{
    public string Reply(CultivationConfig config, in DialogueContext context, string playerLine)
    {
        var line = playerLine.Trim();
        var lower = line.ToLowerInvariant();

        foreach (var keyword in config.Dialogue.KeywordReplies)
        {
            foreach (var word in keyword.Keywords)
            {
                if (lower.Contains(word, StringComparison.Ordinal))
                {
                    return Expand(keyword.Reply, config, in context);
                }
            }
        }

        var bucket = config.Dialogue.Buckets[0];
        foreach (var candidate in config.Dialogue.Buckets)
        {
            if (context.AffectionToPlayer >= candidate.MinAffection) bucket = candidate;
        }

        var replies = bucket.Replies;
        var index = (int)(StableHash(line) % (uint)replies.Length + (uint)context.NpcId) % replies.Length;
        return Expand(replies[index], config, in context);
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
