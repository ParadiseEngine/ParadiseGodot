using System.Runtime.CompilerServices;

namespace ParadiseCultivation;

/// <summary>
/// Shared per-tick simulation data, modelled as a component because Paradise.ECS injects
/// components, not arbitrary objects (the ParadiseGame SimulationContext pattern). Carried by
/// every simulated entity; <see cref="CultivationRunner"/> refreshes it on the write world
/// each tick before the schedule runs. <see cref="SettlementSystem"/> injects it WRITABLE so
/// it binds to the write world and reads THIS tick's values — month crossings must settle on
/// the tick they happen, not one tick late.
/// </summary>
[Component]
public partial struct SimulationContext
{
    public float DeltaSeconds;
    /// <summary>Absolute whole day since year StartYear, month 1, day 1.</summary>
    public long Day;
    /// <summary>Month boundaries crossed by this tick's time advance (usually 0).</summary>
    public int MonthsCrossed;
    /// <summary>Absolute index of the first crossed month (for deterministic per-month RNG).</summary>
    public long FirstMonthIndex;
    /// <summary>World seed, mixed into the settlement hash so runs differ per world.</summary>
    public int WorldSeed;
}

/// <summary>Cultivation progression shared by the player and every NPC.</summary>
[Component]
public partial struct Cultivator
{
    public int RealmIndex;
    public int SubStage;
    public double CultivationPoints;
    public double AgeDays;
}

/// <summary>
/// NPC-only state. Strings live outside the ECS: names/personalities are INDICES into the
/// config's authored pools (immutable, so cross-thread snapshot reads are safe), and the
/// memory log is a sim-thread side store on the runner keyed by entity. Affection is two-way
/// per the design doc. The Just* flags are set by <see cref="SettlementSystem"/> and consumed
/// (chronicle + replacement spawn) by the runner's managed post-pass, which clears them.
/// </summary>
[Component]
public partial struct NpcState
{
    public int NpcId;
    public int SiteIndex;
    public int SurnameIndex;
    public int GivenNameIndex;
    public int PersonalityIndex;
    public int CharmTier;
    public float AffectionToPlayer;
    public float PlayerAffection;
    public int ChatsThisMonth;
    public byte Alive;
    public byte IsLeader;
    public byte JustBrokeThrough;
    public byte JustDied;
}

/// <summary>Player-only state (position on the map, resources, creation rolls).</summary>
[Component]
public partial struct PlayerData
{
    public int SurnameIndex;
    public int GivenNameIndex;
    public int SpiritRootElement;
    public int SpiritRootGrade;
    public int CharmTier;
    public int X;
    public int Y;
    public float Fortune;
    public int SpiritStones;
    public int Herbs;
    /// <summary>Months of halved cultivation gain remaining (failed-breakthrough injury).</summary>
    public int InjuryMonths;
    public double LifespanYears;
}

/// <summary>The realm parameters <see cref="SettlementSystem"/> needs per NPC month.</summary>
public struct RealmParams
{
    public int LifespanYears;
    public int PointsPerSubStage;
    public float BreakthroughChance;
}

/// <summary>Fixed-capacity inline realm table (C# 12 InlineArray — unmanaged, blittable).</summary>
[InlineArray(RealmLadder.MaxRealms)]
public struct RealmParamsBuffer
{
    private RealmParams _element0;
}

/// <summary>
/// The realm ladder baked from <c>data/cultivation/config.json</c> at spawn — systems cannot
/// reach managed config objects, so the numbers they need ride on the entities as read-only
/// components (config-over-constants still holds: the values come from the authored JSON).
/// </summary>
[Component]
public partial struct RealmLadder
{
    public const int MaxRealms = 16;

    public RealmParamsBuffer Realms;
    public int Count;
}

/// <summary>Settlement tuning baked from config at spawn (read-only in the system).</summary>
[Component]
public partial struct SettlementTuning
{
    public int SubStageCount;
    public int NpcMonthlyPointsMin;
    public int NpcMonthlyPointsMax;
    public float NpcBreakthroughChanceScale;
    public int DaysPerMonth;
    public int DaysPerYear;
}
