using System.Text.Json;

namespace ParadiseCultivation;

/// <summary>Root of <c>data/cultivation/config.json</c> — every gameplay tunable of the
/// cultivation slice (world generation, calendar, realm ladder, spirit roots, charm,
/// affection, interaction economy, dialogue phrase pools). Code contains mechanisms only;
/// numbers live here, per the project's config-over-constants rule.</summary>
public sealed record CultivationConfig
{
    public required WorldConfig World { get; init; }
    public required SaveConfig Save { get; init; }
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
    /// <summary>Every user-facing string (the shipped config authors them in Chinese).</summary>
    public required TextConfig Text { get; init; }

    public static CultivationConfig FromJson(string json) =>
        JsonSerializer.Deserialize(json, CultivationJsonContext.Default.CultivationConfig)
        ?? throw new InvalidDataException("cultivation config deserialized to null");
}

public sealed record WorldConfig
{
    /// <summary>The locked world presets — the 32x32 Demo and the 256x256 formal world
    /// (high-concept v2.0: no small/medium/large size selection).</summary>
    public required WorldPresetConfig[] Presets { get; init; }
    public required int DefaultPresetIndex { get; init; }
    public required TerrainConfig Terrain { get; init; }
    /// <summary>Validation-failure rerolls with a derived seed up to this many attempts.</summary>
    public required int MaxGenerationAttempts { get; init; }
}

public sealed record WorldPresetConfig
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int TownCount { get; init; }
    public required int SectCount { get; init; }
    public required int NpcsPerTown { get; init; }
    public required int NpcsPerSect { get; init; }
    public required int MinTownSeparation { get; init; }
    public required int MinSectSeparation { get; init; }
}

/// <summary>Noise channels and thresholds mapping to the 8 locked base terrains, plus the
/// L3 spirit-vein layer and per-terrain foot-travel costs.</summary>
public sealed record TerrainConfig
{
    public required int ElevationOctaves { get; init; }
    public required float ElevationScale { get; init; }
    public required float MoistureScale { get; init; }
    public required float TemperatureScale { get; init; }
    public required float SpiritScale { get; init; }
    /// <summary>Elevation above this is Mountains.</summary>
    public required float MountainThreshold { get; init; }
    /// <summary>Elevation above this (below mountains) is Hills.</summary>
    public required float HillThreshold { get; init; }
    /// <summary>Elevation below this is Water (inland lakes — no rivers/sea by design).</summary>
    public required float WaterThreshold { get; init; }
    /// <summary>Temperature below this becomes Snowfield.</summary>
    public required float SnowTemperature { get; init; }
    /// <summary>Hot + dry (moisture below DesertMoisture, temperature above this) is Desert.</summary>
    public required float DesertTemperature { get; init; }
    public required float DesertMoisture { get; init; }
    /// <summary>Wet lowland (moisture above this, low elevation) is Swamp.</summary>
    public required float SwampMoisture { get; init; }
    /// <summary>Low elevation bound for swamp (must be below WaterThreshold + this margin).</summary>
    public required float SwampElevationMargin { get; init; }
    /// <summary>Moisture above this (on remaining land) is Forest; else Plains.</summary>
    public required float ForestMoisture { get; init; }
    /// <summary>Ascending spirit-noise thresholds for vein quality 1…4 (the L3 layer).</summary>
    public required float[] VeinQualityThresholds { get; init; }
    /// <summary>Foot-travel days per tile, indexed by <see cref="Terrain"/>; a value ≤ 0
    /// means impassable on foot (Water). Sword flight ignores terrain.</summary>
    public required float[] MoveCostDays { get; init; }
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
    public required float SwordFlightTilesPerDay { get; init; }
    /// <summary>Realm index (0-based) that unlocks sword flight.</summary>
    public required int SwordFlightRealmIndex { get; init; }
}

/// <summary>Versioned-save settings (the design doc's "establish saves early").</summary>
public sealed record SaveConfig
{
    /// <summary>Directory for save files, relative to the working directory.</summary>
    public required string Directory { get; init; }
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
    /// <summary>Proposal budget: an interaction proposer's affection-delta suggestion is
    /// clamped to ±this before the rules layer applies it (LLMs propose, rules decide).</summary>
    public required float MaxProposedAffectionDelta { get; init; }
    /// <summary>Proposal replies are truncated to this length (safety validation).</summary>
    public required int MaxReplyLength { get; init; }
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
    /// <summary>Personality-exclusive reply pools, mixed in deterministically by
    /// <see cref="TemplateProposer"/> — different temperaments answer differently.</summary>
    public required PersonalityRepliesConfig[] PersonalityReplies { get; init; }
    /// <summary>Percentage (0-100) of non-keyword replies drawn from the personality pool.</summary>
    public required int PersonalityReplyPercent { get; init; }
}

public sealed record PersonalityRepliesConfig
{
    public required string Personality { get; init; }
    public required string[] Replies { get; init; }
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
/// colors, zoom bounds, font) — kept in config with everything else.</summary>
public sealed record UiConfig
{
    /// <summary>TTF/TTC to load for CJK-capable text; empty = probe known system fonts.</summary>
    public required string FontPath { get; init; }
    public required float FontSizePixels { get; init; }
    /// <summary>Ink-wash placeholder palette, indexed by <see cref="Terrain"/> (8 entries).</summary>
    public required uint[] TerrainColors { get; init; }
    /// <summary>Vein overlay marker colors by quality (index 0 unused).</summary>
    public required uint[] VeinQualityColors { get; init; }
    public required uint TownColor { get; init; }
    public required uint SectColor { get; init; }
    public required uint PlayerColor { get; init; }
    public required uint GridLineColor { get; init; }
    public required uint PathColor { get; init; }
    /// <summary>Overlay drawn on tiles beyond the observable range.</summary>
    public required uint FogColor { get; init; }
    /// <summary>Iso tile WIDTH in pixels (height is half); continuous wheel zoom.</summary>
    public required float ZoomMin { get; init; }
    public required float ZoomMax { get; init; }
    public required float ZoomDefault { get; init; }
    /// <summary>Chebyshev radius (tiles) the player can see and click destinations within.</summary>
    public required int ObservableRange { get; init; }
    /// <summary>Zoom (tile width px) at or above which site name labels draw.</summary>
    public required float LabelZoomThreshold { get; init; }
}
