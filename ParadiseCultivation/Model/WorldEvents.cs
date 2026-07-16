namespace ParadiseCultivation;

/// <summary>What one month's event bends, as a multiplier on the named quantity.</summary>
public enum WorldEventEffect
{
    CultivationGain,
    EncounterChance,
    HerbPrice,
    PillPrice,
}

/// <summary>One month's world event, resolved from the schedule (never stored).</summary>
public readonly record struct WorldEventInfo(
    long MonthIndex, int ArchetypeIndex, string Name, WorldEventEffect Effect, float Magnitude, string LogLine);

/// <summary>
/// The random-event schedule — pure functions over (config, worldSeed, month), the
/// <see cref="SecretRealms"/> pattern: whether a month hosts an event and which archetype
/// both derive from a hash of the generation seed and the absolute month index, so the world
/// replays identically per seed and saves store nothing. Mechanical effects are config
/// multipliers applied by the rules layer; the chronicle line is authored text that the
/// optional LLM layer may rewrite (narration only — never the mechanics).
/// </summary>
public static class WorldEvents
{
    /// <summary>The event of month <paramref name="monthIndex"/>, or null (quiet month).</summary>
    public static WorldEventInfo? TryGetForMonth(CultivationConfig config, int worldSeed, long monthIndex)
    {
        var cfg = config.WorldEvents;
        if (cfg.Archetypes.Length == 0 || monthIndex < cfg.FirstEventMonth)
        {
            return null;
        }
        if (Hash(worldSeed, monthIndex, 0x9E11u) % 100u >= (uint)cfg.MonthlyChancePercent)
        {
            return null;
        }

        var totalWeight = 0;
        foreach (var archetype in cfg.Archetypes)
        {
            totalWeight += Math.Max(0, archetype.Weight);
        }
        if (totalWeight <= 0)
        {
            return null;
        }

        var pick = (int)(Hash(worldSeed, monthIndex, 0xA5C3u) % (uint)totalWeight);
        for (var i = 0; i < cfg.Archetypes.Length; i++)
        {
            pick -= Math.Max(0, cfg.Archetypes[i].Weight);
            if (pick < 0)
            {
                var a = cfg.Archetypes[i];
                return new WorldEventInfo(monthIndex, i, a.Name, a.Effect, a.Magnitude, a.LogLine);
            }
        }
        return null; // unreachable (weights sum > 0)
    }

    /// <summary>The event whose month contains <paramref name="day"/>, or null.</summary>
    public static WorldEventInfo? TryGetCurrent(CultivationConfig config, int worldSeed, long day) =>
        TryGetForMonth(config, worldSeed, day / config.Time.DaysPerMonth);

    /// <summary>The multiplier the day's event applies to <paramref name="effect"/> —
    /// 1 on quiet months or when a different quantity is affected. The single hook the
    /// rules layer consults (prices, encounter odds, cultivation gain).</summary>
    public static float Multiplier(CultivationConfig config, int worldSeed, long day, WorldEventEffect effect) =>
        TryGetCurrent(config, worldSeed, day) is { } current && current.Effect == effect
            ? current.Magnitude
            : 1f;

    /// <summary>splitmix-style avalanche over (seed, month, stream) — like
    /// <see cref="CultivationRules.TownPriceMultiplier"/>, deterministic and stateless.</summary>
    private static uint Hash(int seed, long month, uint stream)
    {
        var h = (uint)seed * 2654435761u ^ (uint)month * 2246822519u ^ ((uint)(month >> 32) + stream) * 3266489917u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        h *= 3266489917u;
        h ^= h >> 16;
        return h;
    }
}
