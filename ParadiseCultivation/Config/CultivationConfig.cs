using System.Text.Json;
using System.Text.Json.Nodes;

namespace ParadiseCultivation;

/// <summary>The authored files under <c>data/cultivation/</c> that compose one
/// <see cref="CultivationConfig"/> — split for maintainability: gameplay numbers apart from
/// the (much larger) authored content.</summary>
public static class ConfigFiles
{
    /// <summary>Gameplay tunables (world, time, realms, interaction economy, ui…).</summary>
    public const string Core = "config.json";
    /// <summary>Name pools (surnames, given names, town/sect parts).</summary>
    public const string Names = "names.json";
    /// <summary>Dialogue content (affection buckets, keyword intents, personality pools).</summary>
    public const string Dialogue = "dialogue.json";
    /// <summary>Every user-facing string (UI labels, message templates, intro, flavor).</summary>
    public const string Text = "text.json";
    /// <summary>The optional online intelligence layer (model, budgets, prompt templates).</summary>
    public const string Llm = "llm.json";

    public static readonly string[] All = [Core, Names, Dialogue, Text, Llm];
}

/// <summary>Root of the composed cultivation config — every gameplay tunable and every piece
/// of authored content (see <see cref="ConfigFiles"/> for the on-disk split). Code contains
/// mechanisms only; numbers and words live in the files, per the project's
/// config-over-constants rule.</summary>
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
    public required TradeConfig Trade { get; init; }
    public required SectConfig Sect { get; init; }
    public required CompanionConfig Companion { get; init; }
    public required AscensionConfig Ascension { get; init; }
    public required SecretRealmConfig SecretRealm { get; init; }
    public required WorldEventsConfig WorldEvents { get; init; }
    public required CombatConfig Combat { get; init; }
    /// <summary>The optional online intelligence layer (see <see cref="ILlmTextService"/>).</summary>
    public required LlmConfig Llm { get; init; }
    public required DialogueConfig Dialogue { get; init; }
    public required NamesConfig Names { get; init; }
    public required UiConfig Ui { get; init; }
    /// <summary>Every user-facing string (the shipped config authors them in Chinese).</summary>
    public required TextConfig Text { get; init; }

    public static CultivationConfig FromJson(string json) =>
        JsonSerializer.Deserialize(json, CultivationJsonContext.Default.CultivationConfig)
        ?? throw new InvalidDataException("cultivation config deserialized to null");

    /// <summary>Compose the config from its split files (<see cref="ConfigFiles.All"/>).
    /// <paramref name="readFile"/> abstracts the host's IO — File.ReadAllText for the .NET
    /// runtime, Godot.FileAccess for res:// paths. Content files plug into the core document
    /// under their section names, then the whole composes as one object.</summary>
    public static CultivationConfig Load(Func<string, string> readFile)
    {
        var documentOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        JsonNode Parse(string file) =>
            JsonNode.Parse(readFile(file), documentOptions: documentOptions)
            ?? throw new InvalidDataException($"cultivation config file '{file}' parsed to null");

        var root = Parse(ConfigFiles.Core) as JsonObject
            ?? throw new InvalidDataException($"'{ConfigFiles.Core}' must be a JSON object");
        root["names"] = Parse(ConfigFiles.Names);
        root["dialogue"] = Parse(ConfigFiles.Dialogue);
        root["text"] = Parse(ConfigFiles.Text);
        root["llm"] = Parse(ConfigFiles.Llm);

        return root.Deserialize(CultivationJsonContext.Default.CultivationConfig)
            ?? throw new InvalidDataException("cultivation config deserialized to null");
    }
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
    /// <summary>Click-travel journeys stretch to at least this long, so the marker is SEEN
    /// walking its path (an 11-day walk at the raw rate finishes in a tenth of a second).</summary>
    public required float TravelMinSeconds { get; init; }
    /// <summary>Fixed duration of one WASD step — snappy, independent of terrain cost.</summary>
    public required float StepSeconds { get; init; }
}

public sealed record ActionDaysConfig
{
    public required int Chat { get; init; }
    public required int Gift { get; init; }
    public required int Spar { get; init; }
    public required int Explore { get; init; }
    public required int Breakthrough { get; init; }
    public required int Trade { get; init; }
}

/// <summary>Town markets (the P2 trade slice): every town buys herbs and stocks breakthrough
/// pills; prices carry a deterministic per-town spread so distant markets are worth the trip.</summary>
public sealed record TradeConfig
{
    /// <summary>Base spirit stones a town pays per herb.</summary>
    public required int HerbSellStones { get; init; }
    /// <summary>Base spirit-stone price of one breakthrough pill.</summary>
    public required int PillCostStones { get; init; }
    /// <summary>Pills on each town's shelf; restocked to this on every month crossing.</summary>
    public required int PillStockPerTown { get; init; }
    /// <summary>Success-chance bonus of the pill auto-consumed by a breakthrough attempt.</summary>
    public required float PillBreakthroughBonus { get; init; }
    /// <summary>Per-town price factor range: 1 ± this percent, hashed from world + site.</summary>
    public required int PriceSpreadPercent { get; init; }
}

/// <summary>Sect membership (the P2 slice): apprenticeship gated on the leader's regard and
/// the spirit root, a rank ladder promoted by realm at monthly settlement, a stipend, and a
/// cultivation bonus while at one's own mountain gate. Reuses the affection machinery whole.</summary>
public sealed record SectConfig
{
    /// <summary>The sect leader's affection toward the player required to be accepted.</summary>
    public required float JoinMinLeaderAffection { get; init; }
    /// <summary>Minimum spirit-root grade INDEX to be accepted (0 = any root qualifies).</summary>
    public required int JoinMinSpiritRootGrade { get; init; }
    /// <summary>Game days the apprenticeship ceremony takes.</summary>
    public required int JoinCeremonyDays { get; init; }
    /// <summary>Applied to the leader's affection when the player walks away (negative).</summary>
    public required float LeaveAffectionPenalty { get; init; }
    /// <summary>Cultivation-gain multiplier bonus while standing at one's OWN sect.</summary>
    public required float MemberCultivationBonus { get; init; }
    /// <summary>Ascending by <see cref="SectRankConfig.MinRealmIndex"/>; everyone joins at
    /// rank 0 and monthly settlement promotes through every rank the realm qualifies for.</summary>
    public required SectRankConfig[] Ranks { get; init; }
    /// <summary>Mission archetypes; the month's board entry is hash-picked per (world seed,
    /// sect site, month) — the secret-realm/event pattern, nothing saved.</summary>
    public required SectMissionConfig[] Missions { get; init; }
    /// <summary>Game days one mission takes.</summary>
    public required int MissionDays { get; init; }
    /// <summary>Contribution cost of one breakthrough pill at the sect exchange.</summary>
    public required int ExchangePillContribution { get; init; }
    /// <summary>Contribution cost of clearing ALL injury months at the sect exchange.</summary>
    public required int ExchangeHealContribution { get; init; }
}

public sealed record SectMissionConfig
{
    public required string Name { get; init; }
    /// <summary>Contribution points on success.</summary>
    public required int ContributionReward { get; init; }
    public required float SuccessChance { get; init; }
    /// <summary>Injury months on failure (the attempt spends the month either way).</summary>
    public required int FailureInjuryMonths { get; init; }
}

/// <summary>The endgame: standing at the final realm's Perfected peak with a full points
/// bar, the cultivator may face the last heavenly tribulation. Success wins the game
/// (<see cref="GamePhase.Ascended"/>); failure burns cultivation and injures — the mountain
/// can be climbed again. One fortune-weighted roll, the secret-realm-trial shape.</summary>
public sealed record AscensionConfig
{
    public required float BaseChance { get; init; }
    /// <summary>Per-fortune-point addition to the tribulation chance.</summary>
    public required float FortuneChancePerPoint { get; init; }
    /// <summary>Fraction of accumulated points burned by a failed tribulation.</summary>
    public required float FailureCultivationLoss { get; init; }
    public required int FailureInjuryMonths { get; init; }
    /// <summary>Game days a FAILED tribulation consumes (success ends the run outright).</summary>
    public required int TrialDays { get; init; }
}

/// <summary>Dao companions: the top of the affection ladder made mechanical — a mutual bond
/// gated on both-way affection and realm proximity, granting a dual-cultivation bonus while
/// training at the companion's side. Reuses the affection machinery whole.</summary>
public sealed record CompanionConfig
{
    /// <summary>BOTH affection directions must reach this to propose.</summary>
    public required float MinAffectionBoth { get; init; }
    /// <summary>Maximum |player realm − npc realm| to propose.</summary>
    public required int MaxRealmGap { get; init; }
    /// <summary>Game days the bonding ceremony takes.</summary>
    public required int CeremonyDays { get; init; }
    /// <summary>Cultivation-gain multiplier bonus while cultivating at the companion's site.</summary>
    public required float DualCultivationBonus { get; init; }
    /// <summary>Applied to BOTH affection directions on walking away (negative).</summary>
    public required float LeaveAffectionPenalty { get; init; }
}

public sealed record SectRankConfig
{
    public required string Name { get; init; }
    /// <summary>Realm index required to hold this rank.</summary>
    public required int MinRealmIndex { get; init; }
    /// <summary>Spirit stones paid at each monthly settlement.</summary>
    public required int MonthlyStipendStones { get; init; }
}

/// <summary>The secret realm (P2 slice): a deterministic seed-derived schedule of openings —
/// rumor keywords in chat reveal the location, and one fortune-weighted trial per opening
/// pays out or injures. See <see cref="SecretRealms"/> for the schedule math.</summary>
public sealed record SecretRealmConfig
{
    /// <summary>Absolute month index of the first opening.</summary>
    public required int FirstOpenMonth { get; init; }
    /// <summary>Months between opening STARTS (must exceed <see cref="OpenMonths"/>).</summary>
    public required int PeriodMonths { get; init; }
    /// <summary>Months each opening stays enterable.</summary>
    public required int OpenMonths { get; init; }
    /// <summary>Game days one trial takes.</summary>
    public required int TrialDays { get; init; }
    public required float BaseSuccessChance { get; init; }
    /// <summary>Per-fortune-point addition to the trial success chance.</summary>
    public required float FortuneChancePerPoint { get; init; }
    public required int RewardStonesMin { get; init; }
    public required int RewardStonesMax { get; init; }
    public required int RewardHerbsMin { get; init; }
    public required int RewardHerbsMax { get; init; }
    /// <summary>Cultivation insight on success (scaled by the spirit-root multiplier).</summary>
    public required int RewardInsightPoints { get; init; }
    /// <summary>Fortune gained by surviving the trial.</summary>
    public required float FortuneGain { get; init; }
    /// <summary>Injury months on a failed trial.</summary>
    public required int FailureInjuryMonths { get; init; }
    /// <summary>Chat text containing any of these reveals the open realm's whereabouts.</summary>
    public required string[] RumorKeywords { get; init; }
}

/// <summary>Random world events: a deterministic hash schedule (the secret-realm pattern —
/// pure function of world seed + month, nothing saved) where each event month applies one
/// config multiplier to a rules quantity and writes an authored chronicle line. The optional
/// LLM layer may rewrite the narration; the mechanics never leave this config.</summary>
public sealed record WorldEventsConfig
{
    /// <summary>Percent chance (0–100) that a given month hosts an event.</summary>
    public required int MonthlyChancePercent { get; init; }
    /// <summary>First eligible month index (keeps the opening month quiet).</summary>
    public required int FirstEventMonth { get; init; }
    public required WorldEventArchetypeConfig[] Archetypes { get; init; }
}

public sealed record WorldEventArchetypeConfig
{
    public required string Name { get; init; }
    /// <summary>Which rules quantity the event bends (see <see cref="WorldEventEffect"/>).</summary>
    public required WorldEventEffect Effect { get; init; }
    /// <summary>Multiplier applied to the affected quantity for the event month.</summary>
    public required float Magnitude { get; init; }
    /// <summary>Relative weight in the archetype pick.</summary>
    public required int Weight { get; init; }
    /// <summary>The authored chronicle sentence for the event month (LLM may rewrite it).</summary>
    public required string LogLine { get; init; }
}

/// <summary>The optional online intelligence layer: an LLM rewrites NPC replies and event
/// narration (strings only — the rules layer still owns every number). Enabled requires a
/// credential in the environment; without one the game runs fully offline as before.</summary>
public sealed record LlmConfig
{
    public required bool Enabled { get; init; }
    /// <summary>Model id on the configured OpenAI-compatible endpoint (e.g. gpt-5-mini).</summary>
    public required string Model { get; init; }
    public required int MaxTokens { get; init; }
    public required int TimeoutSeconds { get; init; }
    /// <summary>How many recent memory lines the dialogue prompt includes.</summary>
    public required int RecentMemories { get; init; }
    /// <summary>Joins the recent memory lines inside the prompt.</summary>
    public required string MemorySeparator { get; init; }
    /// <summary>Slots: {0} npc name, {1} personality, {2} npc realm, {3} player name,
    /// {4} player realm, {5} affection tier, {6} max reply chars, {7} affection budget
    /// (±<see cref="InteractionConfig.MaxProposedAffectionDelta"/>). Literal JSON braces in
    /// the template must be doubled ({{ }}) — it goes through string.Format.</summary>
    public required string DialogueSystem { get; init; }
    /// <summary>Slots: {0} joined recent memories, {1} the player's line.</summary>
    public required string DialogueUser { get; init; }
    /// <summary>Slots: {0} max chars.</summary>
    public required string EventSystem { get; init; }
    /// <summary>Slots: {0} date, {1} event name, {2} the authored chronicle line.</summary>
    public required string EventUser { get; init; }
}

/// <summary>Semi-auto wilderness combat (the doc's P2 encounter): exploring can provoke a
/// realm-scaled beast; the PLAYER makes the strategic call (fight or flee), the rounds
/// resolve automatically. No permadeath in the slice — defeat costs injury months.</summary>
public sealed record CombatConfig
{
    /// <summary>Chance an explore is interrupted by a beast (the explore yields no loot).</summary>
    public required float EncounterChance { get; init; }
    /// <summary>Game days resolving the encounter takes (fight or flee).</summary>
    public required int ResolveDays { get; init; }
    public required float PowerPerRealm { get; init; }
    public required float PowerPerSubStage { get; init; }
    /// <summary>Uniform random spread added to each side's round roll.</summary>
    public required float RollSpread { get; init; }
    /// <summary>Flat power the beast adds (they fight dirty).</summary>
    public required float BeastPowerBonus { get; init; }
    /// <summary>Beast realm is the player's ± this, clamped to [0, MaxBeastRealmIndex].</summary>
    public required int BeastRealmSpread { get; init; }
    public required int MaxBeastRealmIndex { get; init; }
    /// <summary>Rounds fought; winning more than half wins the encounter (keep it odd).</summary>
    public required int Rounds { get; init; }
    /// <summary>Expected-power gap at which the UI's appraisal flips between the three
    /// <see cref="UiTextConfig.EncounterJudgeNames"/> labels.</summary>
    public required float JudgeThreshold { get; init; }
    /// <summary>Victory loot scales linearly with (beast realm + 1).</summary>
    public required int LootStonesPerRealm { get; init; }
    public required int LootHerbsPerRealm { get; init; }
    public required int InsightPerRealm { get; init; }
    public required float FortuneWinGain { get; init; }
    /// <summary>Defeat injury: (beast realm + 1) × this, at least 1 month.</summary>
    public required int LossInjuryMonthsPerRealm { get; init; }
    /// <summary>Beast realm at/above which a victory enters the chronicle.</summary>
    public required int NotableRealmIndex { get; init; }
    public required float FleeBaseChance { get; init; }
    /// <summary>Per (player realm − beast realm) addition to the flee chance.</summary>
    public required float FleeChancePerRealmDiff { get; init; }
    /// <summary>The parting blow when a flee fails.</summary>
    public required int FleeFailInjuryMonths { get; init; }
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
    /// <summary>Variants — selection is salted by the NPC's memory count, so asking the
    /// same thing twice gets a different answer as the relationship history grows.</summary>
    public required string[] Replies { get; init; }
}

public sealed record NamesConfig
{
    public required string[] Surnames { get; init; }
    public required string[] GivenNames { get; init; }
    public required string[] TownPrefixes { get; init; }
    public required string[] TownSuffixes { get; init; }
    public required string[] SectPrefixes { get; init; }
    public required string[] SectSuffixes { get; init; }
    /// <summary>Secret-realm names, drawn per opening by <see cref="SecretRealms.NameOf"/>.</summary>
    public required string[] RealmNames { get; init; }
    /// <summary>Wilderness beast names, drawn per encounter from the saved RNG stream.</summary>
    public required string[] BeastNames { get; init; }
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
    /// <summary>Marker for a known, currently open secret realm.</summary>
    public required uint RealmColor { get; init; }
    public required uint PlayerColor { get; init; }
    public required uint GridLineColor { get; init; }
    public required uint PathColor { get; init; }
    /// <summary>Overlay drawn on tiles beyond the observable range.</summary>
    public required uint FogColor { get; init; }
    /// <summary>Iso tile WIDTH in pixels (height is half); continuous wheel zoom.</summary>
    public required float ZoomMin { get; init; }
    public required float ZoomMax { get; init; }
    public required float ZoomDefault { get; init; }
    /// <summary>Chebyshev radius (tiles) the player can see and click destinations within
    /// at realm 0 — divine sense: the effective range is base + realm ×
    /// <see cref="ObservableRangePerRealm"/>, capped at <see cref="ObservableRangeMax"/>
    /// (see <see cref="CultivationRules.ObservableRange"/>).</summary>
    public required int ObservableRange { get; init; }
    public required int ObservableRangePerRealm { get; init; }
    public required int ObservableRangeMax { get; init; }
    /// <summary>Zoom (tile width px) at or above which site name labels draw.</summary>
    public required float LabelZoomThreshold { get; init; }
}
