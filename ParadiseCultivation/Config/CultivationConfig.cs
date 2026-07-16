using System.Text.Json;

namespace ParadiseCultivation;

/// <summary>Root of <c>data/cultivation/config.json</c> — every gameplay tunable of the
/// cultivation slice (world generation, calendar, realm ladder, spirit roots, charm,
/// affection, interaction economy, dialogue phrase pools). Code contains mechanisms only;
/// numbers live here, per the project's config-over-constants rule.</summary>
public sealed record CultivationConfig
{
    public required WorldConfig World { get; init; }
    public required TimeConfig Time { get; init; }
    /// <summary>The 10 major realms, Qi Refining → True Immortal, in ascending order.</summary>
    public required RealmConfig[] Realms { get; init; }
    /// <summary>Sub-stage display names (Early / Middle / Late / Perfected).</summary>
    public required string[] SubStages { get; init; }
    public required SpiritRootConfig SpiritRoots { get; init; }
    /// <summary>Cultivation-speed bonus indexed by vein quality (0 = no vein … 4 = supreme).</summary>
    public required float[] VeinCultivationBonus { get; init; }
    public required CharmTierConfig[] CharmTiers { get; init; }
    /// <summary>Ascending by <see cref="AffectionTierConfig.Min"/>; lookup takes the last tier
    /// whose Min is ≤ the affection value.</summary>
    public required AffectionTierConfig[] AffectionTiers { get; init; }
    public required FortuneConfig Fortune { get; init; }
    public required InteractionConfig Interaction { get; init; }
    public required NpcConfig Npc { get; init; }
    public required PlayerConfig Player { get; init; }
    public required DialogueConfig Dialogue { get; init; }
    public required NamesConfig Names { get; init; }
    public required UiConfig Ui { get; init; }

    public static CultivationConfig FromJson(string json) =>
        JsonSerializer.Deserialize(json, CultivationJsonContext.Default.CultivationConfig)
        ?? throw new InvalidDataException("cultivation config deserialized to null");
}

public sealed record WorldConfig
{
    public required WorldSizeConfig[] Sizes { get; init; }
    public required int DefaultSizeIndex { get; init; }
    public required TerrainConfig Terrain { get; init; }
    public required int MinTownSeparation { get; init; }
    public required int MinSectSeparation { get; init; }
    public required int NpcsPerTown { get; init; }
    public required int NpcsPerSect { get; init; }
}

public sealed record WorldSizeConfig
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int TownCount { get; init; }
    public required int SectCount { get; init; }
}

public sealed record TerrainConfig
{
    public required int ElevationOctaves { get; init; }
    public required float ElevationScale { get; init; }
    /// <summary>Elevation above this is Mountain.</summary>
    public required float MountainThreshold { get; init; }
    /// <summary>Elevation below this is River (lowland water).</summary>
    public required float WaterThreshold { get; init; }
    public required float MoistureScale { get; init; }
    /// <summary>Moisture above this (on otherwise-plain tiles) is Forest.</summary>
    public required float ForestThreshold { get; init; }
    public required float SpiritScale { get; init; }
    /// <summary>Ascending spirit-noise thresholds for vein quality 1…4.</summary>
    public required float[] VeinQualityThresholds { get; init; }
}

public sealed record TimeConfig
{
    public required int DaysPerMonth { get; init; }
    public required int MonthsPerYear { get; init; }
    public required int StartYear { get; init; }
    /// <summary>How game time flows on the 60 Hz sim thread while an action is pending.</summary>
    public required TimeFlowConfig Flow { get; init; }
    /// <summary>Player starting age in years.</summary>
    public required int StartAgeYears { get; init; }
    public required ActionDaysConfig ActionDays { get; init; }
    public required float WalkDaysPerTile { get; init; }
    public required float SwordFlightTilesPerDay { get; init; }
    /// <summary>Realm index (0-based) that unlocks sword flight.</summary>
    public required int SwordFlightRealmIndex { get; init; }
    /// <summary>Travel-day multiplier when the destination tile is a Mountain.</summary>
    public required float MountainTravelMultiplier { get; init; }
}

/// <summary>Actions animate: game days tick by at <see cref="DaysPerSecond"/> real-time (the
/// design doc's "month transitions make time feel tangible"), accelerated so no action takes
/// longer than <see cref="MaxActionSeconds"/> wall-clock (century seclusions stay snappy).</summary>
public sealed record TimeFlowConfig
{
    public required float DaysPerSecond { get; init; }
    public required float MaxActionSeconds { get; init; }
}

public sealed record ActionDaysConfig
{
    public required int Chat { get; init; }
    public required int Gift { get; init; }
    public required int Spar { get; init; }
    public required int Explore { get; init; }
    public required int Breakthrough { get; init; }
}

public sealed record RealmConfig
{
    public required string Name { get; init; }
    public required int LifespanYears { get; init; }
    /// <summary>Cultivation points to advance one sub-stage (and to be breakthrough-ready at
    /// Perfected).</summary>
    public required int PointsPerSubStage { get; init; }
    /// <summary>Base points gained per month of dedicated cultivation at this realm.</summary>
    public required int MonthlyBasePoints { get; init; }
    /// <summary>Base success chance of the major breakthrough out of this realm (0 for the
    /// final realm).</summary>
    public required float BreakthroughChance { get; init; }
    /// <summary>Fraction of accumulated points lost on a failed breakthrough.</summary>
    public required float FailureCultivationLoss { get; init; }
    /// <summary>Months of injury (halved cultivation gain) after a failed breakthrough.</summary>
    public required int FailureInjuryMonths { get; init; }
    /// <summary>Breaking through OUT of this realm faces a heavenly tribulation (Golden Core+).</summary>
    public required bool HasTribulation { get; init; }
}

public sealed record SpiritRootConfig
{
    public required string[] Elements { get; init; }
    public required SpiritRootGradeConfig[] Grades { get; init; }
}

public sealed record SpiritRootGradeConfig
{
    public required string Name { get; init; }
    public required float Multiplier { get; init; }
    /// <summary>Relative roll weight at character creation.</summary>
    public required int Weight { get; init; }
}

public sealed record CharmTierConfig
{
    public required string Name { get; init; }
    public required float Multiplier { get; init; }
    public required int Weight { get; init; }
}

public sealed record AffectionTierConfig
{
    public required int Min { get; init; }
    public required string Name { get; init; }
}

public sealed record FortuneConfig
{
    public required float Initial { get; init; }
    /// <summary>Reward multiplier = 1 + fortune × RewardPerPoint, capped at MaxMultiplier.</summary>
    public required float RewardPerPoint { get; init; }
    public required float MaxMultiplier { get; init; }
    public required float Max { get; init; }
    /// <summary>Fortune added by a breakthrough-insight encounter while exploring.</summary>
    public required float InsightGain { get; init; }
    /// <summary>Per-point contribution of fortune to breakthrough success chance.</summary>
    public required float BreakthroughChancePerPoint { get; init; }
}

public sealed record InteractionConfig
{
    /// <summary>Base affection from one chat; divided by (1 + chats already this month).</summary>
    public required float ChatAffection { get; init; }
    /// <summary>Fraction of the NPC-side gain mirrored onto the player's side.</summary>
    public required float PlayerAffectionShare { get; init; }
    public required int GiftSpiritStones { get; init; }
    public required float GiftAffectionPerStone { get; init; }
    public required float SparWinAffection { get; init; }
    public required float SparLoseAffection { get; init; }
    /// <summary>Cultivation insight points the spar winner gains.</summary>
    public required int SparInsightPoints { get; init; }
    /// <summary>Per-realm-step power in the spar contest roll.</summary>
    public required float SparPowerPerRealm { get; init; }
    public required float SparPowerPerSubStage { get; init; }
    public required float SparRollSpread { get; init; }
    /// <summary>Memory entries shown in the NPC panel.</summary>
    public required int MemoryWindow { get; init; }
}

public sealed record NpcConfig
{
    public required int MonthlyPointsMin { get; init; }
    public required int MonthlyPointsMax { get; init; }
    /// <summary>NPC breakthrough chance multiplier relative to the realm's base chance.</summary>
    public required float BreakthroughChanceScale { get; init; }
    /// <summary>Age (years) of replacement cultivators generated when an NPC dies.</summary>
    public required int ReplacementAgeYears { get; init; }
    /// <summary>Realm index at/above which NPC breakthroughs and deaths enter the chronicle.</summary>
    public required int NotableRealmIndex { get; init; }
    /// <summary>Max starting realm index for town NPCs / sect NPCs / sect leaders.</summary>
    public required int TownMaxRealmIndex { get; init; }
    public required int SectMaxRealmIndex { get; init; }
    public required int LeaderMinRealmIndex { get; init; }
    public required string[] Personalities { get; init; }
}

public sealed record PlayerConfig
{
    public required int StartSpiritStones { get; init; }
    public required ExploreConfig Explore { get; init; }
}

public sealed record ExploreConfig
{
    public required float HerbChance { get; init; }
    public required int HerbMin { get; init; }
    public required int HerbMax { get; init; }
    public required float StonesChance { get; init; }
    public required int StonesMin { get; init; }
    public required int StonesMax { get; init; }
    public required float InsightChance { get; init; }
    public required int InsightPoints { get; init; }
}

public sealed record DialogueConfig
{
    /// <summary>Ascending by <see cref="DialogueBucketConfig.MinAffection"/>; reply pool is the
    /// last bucket whose MinAffection is ≤ the NPC's affection toward the player.</summary>
    public required DialogueBucketConfig[] Buckets { get; init; }
    public required KeywordReplyConfig[] KeywordReplies { get; init; }
}

public sealed record DialogueBucketConfig
{
    public required int MinAffection { get; init; }
    public required string[] Replies { get; init; }
}

public sealed record KeywordReplyConfig
{
    public required string[] Keywords { get; init; }
    public required string Reply { get; init; }
}

public sealed record NamesConfig
{
    public required string[] Surnames { get; init; }
    public required string[] GivenNames { get; init; }
    public required string[] TownPrefixes { get; init; }
    public required string[] TownSuffixes { get; init; }
    public required string[] SectPrefixes { get; init; }
    public required string[] SectSuffixes { get; init; }
}

/// <summary>Presentation tunables (map tile colors as 0xAABBGGRR ImGui-packed values, marker
/// colors, zoom bounds) — kept in config with everything else.</summary>
public sealed record UiConfig
{
    public required uint[] TerrainColors { get; init; }
    public required uint[] VeinQualityColors { get; init; }
    public required uint TownColor { get; init; }
    public required uint SectColor { get; init; }
    public required uint PlayerColor { get; init; }
    public required int TileSizeMin { get; init; }
    public required int TileSizeMax { get; init; }
    public required int TileSizeDefault { get; init; }
}
