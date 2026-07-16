namespace ParadiseCultivation;

/// <summary>
/// The versioned save format (the design doc's "establish saves early"): everything needed
/// to reconstruct authoritative state. The map is NOT stored — it re-derives from
/// (Seed, PresetIndex) by the same-seed reproducibility guarantee; dynamic state (player,
/// NPCs, logs) and the EXACT RNG stream (PCG state) are stored, so a loaded game continues
/// deterministically. Loaders must fail safely: any parse/version/shape error leaves the
/// running world untouched.
/// </summary>
public sealed record SaveData
{
    /// <summary>v2 added the trade state (player pills + per-town pill stock) as OPTIONAL
    /// fields, so v1 saves still load — missing values fall back to defaults (the doc's
    /// "migration established early"). Loaders accept 1…CurrentVersion.</summary>
    public const int CurrentVersion = 2;

    public required int Version { get; init; }
    public required int Seed { get; init; }
    public required int PresetIndex { get; init; }
    public required long Day { get; init; }
    public required double DayCursor { get; init; }
    public required GamePhase Phase { get; init; }
    public required ulong RngState { get; init; }
    public required ulong RngStream { get; init; }
    public required int NextNpcId { get; init; }
    public required SavedPlayer Player { get; init; }
    public required SavedNpc[] Npcs { get; init; }
    public required SavedMemory[] Chronicle { get; init; }
    /// <summary>Pill stock per site index (towns only carry stock). Optional since v2 —
    /// absent (v1) means every shelf restocks full on load.</summary>
    public int[]? TownPillStock { get; init; }
}

public sealed record SavedPlayer
{
    public required SavedCultivator Cultivator { get; init; }
    public required int SurnameIndex { get; init; }
    public required int GivenNameIndex { get; init; }
    public required int SpiritRootElement { get; init; }
    public required int SpiritRootGrade { get; init; }
    public required int CharmTier { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required float Fortune { get; init; }
    public required int SpiritStones { get; init; }
    public required int Herbs { get; init; }
    /// <summary>Optional since v2 — absent (v1) defaults to 0.</summary>
    public int Pills { get; init; }
    public required int InjuryMonths { get; init; }
    public required double LifespanYears { get; init; }
}

public sealed record SavedNpc
{
    public required SavedCultivator Cultivator { get; init; }
    public required int NpcId { get; init; }
    public required int SiteIndex { get; init; }
    public required int SurnameIndex { get; init; }
    public required int GivenNameIndex { get; init; }
    public required int PersonalityIndex { get; init; }
    public required int CharmTier { get; init; }
    public required float AffectionToPlayer { get; init; }
    public required float PlayerAffection { get; init; }
    public required int ChatsThisMonth { get; init; }
    public required bool Alive { get; init; }
    public required bool IsLeader { get; init; }
    public required SavedMemory[] Memories { get; init; }
}

public sealed record SavedCultivator
{
    public required int RealmIndex { get; init; }
    public required int SubStage { get; init; }
    public required double CultivationPoints { get; init; }
    public required double AgeDays { get; init; }
}

public sealed record SavedMemory
{
    public required long Day { get; init; }
    public required string Summary { get; init; }
}
