namespace ParadiseCultivation;

/// <summary>
/// Monthly settlement of the NPC population — the "living world" pillar as an ECS system:
/// for every month the tick's time advance crossed, each living NPC gains cultivation,
/// climbs sub-stages, may attempt the major breakthrough, ages, and may die of exhausted
/// lifespan. Runs for every NPC in parallel under the snapshot-read model; per-NPC-per-month
/// randomness is a pure hash of (world seed, npc id, month index), so settlement is
/// deterministic regardless of scheduling.
///
/// The player entity carries no <see cref="NpcState"/>, so it never matches this system —
/// player time (action-driven, vein/root/injury-modified) is settled by the runner's managed
/// pass, which is outside the system-injection model (untracked writes, per AssemblyInfo).
/// Breakthrough/death chronicle entries and replacement spawns are structural/string work the
/// system cannot do: it raises the <c>Just*</c> flags and the runner's post-pass consumes them.
///
/// <see cref="SimulationContext"/> is injected WRITABLE purely for binding: writable fields
/// bind to the write world, whose context the runner refreshed THIS tick (a read-only field
/// would see the previous tick's month crossings and settle one tick late).
/// </summary>
public ref partial struct SettlementSystem : IEntitySystem
{
    public ref Cultivator Cultivator;
    public ref NpcState Npc;
    public ref SimulationContext Context;
    public ref readonly RealmLadder Ladder;
    public ref readonly SettlementTuning Tuning;

    public void Execute()
    {
        if (Npc.Alive == 0 || Context.MonthsCrossed <= 0)
        {
            return;
        }

        Npc.ChatsThisMonth = 0; // chat diminishing-returns window resets each month

        for (var m = 0; m < Context.MonthsCrossed; m++)
        {
            var monthIndex = Context.FirstMonthIndex + m;
            var realm = Ladder.Realms[Cultivator.RealmIndex];

            var span = Tuning.NpcMonthlyPointsMax - Tuning.NpcMonthlyPointsMin + 1;
            Cultivator.CultivationPoints +=
                Tuning.NpcMonthlyPointsMin + (int)(Hash(Context.WorldSeed, Npc.NpcId, monthIndex, 0) % (uint)span);

            while (Cultivator.SubStage < Tuning.SubStageCount - 1 &&
                   Cultivator.CultivationPoints >= realm.PointsPerSubStage)
            {
                Cultivator.CultivationPoints -= realm.PointsPerSubStage;
                Cultivator.SubStage++;
            }

            if (Cultivator.SubStage == Tuning.SubStageCount - 1 &&
                Cultivator.RealmIndex < Ladder.Count - 1 &&
                Cultivator.CultivationPoints >= realm.PointsPerSubStage &&
                Hash01(Context.WorldSeed, Npc.NpcId, monthIndex, 1) <
                    realm.BreakthroughChance * Tuning.NpcBreakthroughChanceScale)
            {
                Cultivator.RealmIndex++;
                Cultivator.SubStage = 0;
                Cultivator.CultivationPoints = 0;
                Npc.JustBrokeThrough = 1;
            }

            Cultivator.AgeDays += Tuning.DaysPerMonth;
            if (Cultivator.AgeDays / Tuning.DaysPerYear >= Ladder.Realms[Cultivator.RealmIndex].LifespanYears)
            {
                Npc.Alive = 0;
                Npc.JustDied = 1;
                return;
            }
        }
    }

    /// <summary>murmur-style integer hash — deterministic per (seed, npc, month, stream).</summary>
    public static uint Hash(int seed, int npcId, long monthIndex, int stream)
    {
        var h = (uint)seed;
        h ^= (uint)npcId * 0x9E3779B1u;
        h = (h << 13) | (h >> 19);
        h ^= (uint)monthIndex * 0x85EBCA77u;
        h ^= (uint)(monthIndex >> 32) * 0xC2B2AE3Du;
        h = (h << 11) | (h >> 21);
        h ^= (uint)stream * 0x27D4EB2Fu;
        h *= 0x165667B1u;
        h ^= h >> 15;
        return h;
    }

    public static float Hash01(int seed, int npcId, long monthIndex, int stream) =>
        (Hash(seed, npcId, monthIndex, stream) & 0xFFFFFF) / (float)0x1000000;
}
