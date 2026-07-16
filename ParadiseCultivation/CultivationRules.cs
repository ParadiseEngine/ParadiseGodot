namespace ParadiseCultivation;

/// <summary>
/// Pure, stateless gameplay rules shared by the runner (sim thread), the UI panels, and the
/// tests — formulas over config + component values, no world access, no side effects.
/// </summary>
public static class CultivationRules
{
    public static long DaysPerYear(CultivationConfig config) =>
        (long)config.Time.DaysPerMonth * config.Time.MonthsPerYear;

    public static string FormatDate(CultivationConfig config, long day)
    {
        var daysPerYear = DaysPerYear(config);
        var year = config.Time.StartYear + day / daysPerYear;
        var month = day % daysPerYear / config.Time.DaysPerMonth + 1;
        var dayOfMonth = day % config.Time.DaysPerMonth + 1;
        return $"Year {year}, Month {month}, Day {dayOfMonth}";
    }

    public static string RealmTitle(CultivationConfig config, int realmIndex, int subStage) =>
        $"{config.Realms[realmIndex].Name} ({config.SubStages[subStage]})";

    public static string NpcName(CultivationConfig config, in NpcState npc) =>
        $"{config.Names.Surnames[npc.SurnameIndex]} {config.Names.GivenNames[npc.GivenNameIndex]}";

    public static string PlayerName(CultivationConfig config, in PlayerData player) =>
        $"{config.Names.Surnames[player.SurnameIndex]} {config.Names.GivenNames[player.GivenNameIndex]}";

    public static string AffectionTierName(CultivationConfig config, float affection)
    {
        var name = config.AffectionTiers[0].Name;
        foreach (var tier in config.AffectionTiers)
        {
            if (affection >= tier.Min) name = tier.Name;
        }
        return name;
    }

    public static float VeinBonusAt(CultivationConfig config, WorldMap map, int x, int y)
    {
        ref readonly var tile = ref map.TileAt(x, y);
        return tile.Terrain == Terrain.SpiritVein
            ? config.VeinCultivationBonus[tile.VeinQuality]
            : config.VeinCultivationBonus[0];
    }

    /// <summary>Player points per cultivated month: realm base × spirit root × vein bonus,
    /// halved while injured.</summary>
    public static double MonthlyCultivationGain(
        CultivationConfig config, WorldMap map, in Cultivator cultivator, in PlayerData player)
    {
        var realm = config.Realms[cultivator.RealmIndex];
        var gain = realm.MonthlyBasePoints
            * config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier
            * (1f + VeinBonusAt(config, map, player.X, player.Y));
        return player.InjuryMonths > 0 ? gain * 0.5 : gain;
    }

    public static float FortuneMultiplier(CultivationConfig config, float fortune) =>
        Math.Min(1f + fortune * config.Fortune.RewardPerPoint, config.Fortune.MaxMultiplier);

    public static bool BreakthroughReady(CultivationConfig config, in Cultivator cultivator) =>
        cultivator.RealmIndex < config.Realms.Length - 1 &&
        cultivator.SubStage == config.SubStages.Length - 1 &&
        cultivator.CultivationPoints >= config.Realms[cultivator.RealmIndex].PointsPerSubStage;

    public static float BreakthroughSuccessChance(
        CultivationConfig config, WorldMap map, in Cultivator cultivator, in PlayerData player)
    {
        var chance = config.Realms[cultivator.RealmIndex].BreakthroughChance
            + player.Fortune * config.Fortune.BreakthroughChancePerPoint
            + VeinBonusAt(config, map, player.X, player.Y) * 0.05f;
        return Math.Clamp(chance, 0.05f, 0.98f);
    }

    public static (int Days, string Mode) PlanTravel(
        CultivationConfig config, WorldMap map, int fromX, int fromY, int realmIndex, int toX, int toY)
    {
        var distance = Math.Max(Math.Abs(toX - fromX), Math.Abs(toY - fromY));
        var flight = realmIndex >= config.Time.SwordFlightRealmIndex;
        var days = flight
            ? distance / config.Time.SwordFlightTilesPerDay
            : distance * config.Time.WalkDaysPerTile;
        if (map.TileAt(toX, toY).Terrain == Terrain.Mountain) days *= config.Time.MountainTravelMultiplier;
        return (Math.Max(1, (int)MathF.Ceiling(days)), flight ? "sword flight" : "on foot");
    }

    /// <summary>Advance sub-stages while the base is full; at Perfected, points cap at
    /// breakthrough readiness (the major breakthrough is a deliberate action).</summary>
    public static void AdvanceSubStages(CultivationConfig config, ref Cultivator cultivator)
    {
        var realm = config.Realms[cultivator.RealmIndex];
        while (cultivator.SubStage < config.SubStages.Length - 1 &&
               cultivator.CultivationPoints >= realm.PointsPerSubStage)
        {
            cultivator.CultivationPoints -= realm.PointsPerSubStage;
            cultivator.SubStage++;
        }
        if (cultivator.SubStage == config.SubStages.Length - 1)
        {
            cultivator.CultivationPoints = Math.Min(cultivator.CultivationPoints, realm.PointsPerSubStage);
        }
    }

    /// <summary>Bake the realm table + settlement tuning components from config (the numbers
    /// systems need must ride on entities — systems cannot reach managed config).</summary>
    public static (RealmLadder Ladder, SettlementTuning Tuning) BakeSettlementData(CultivationConfig config)
    {
        if (config.Realms.Length > RealmLadder.MaxRealms)
        {
            throw new InvalidDataException(
                $"config has {config.Realms.Length} realms; RealmLadder.MaxRealms is {RealmLadder.MaxRealms}.");
        }

        var ladder = new RealmLadder { Count = config.Realms.Length };
        for (var i = 0; i < config.Realms.Length; i++)
        {
            ladder.Realms[i] = new RealmParams
            {
                LifespanYears = config.Realms[i].LifespanYears,
                PointsPerSubStage = config.Realms[i].PointsPerSubStage,
                BreakthroughChance = config.Realms[i].BreakthroughChance,
            };
        }

        var tuning = new SettlementTuning
        {
            SubStageCount = config.SubStages.Length,
            NpcMonthlyPointsMin = config.Npc.MonthlyPointsMin,
            NpcMonthlyPointsMax = config.Npc.MonthlyPointsMax,
            NpcBreakthroughChanceScale = config.Npc.BreakthroughChanceScale,
            DaysPerMonth = config.Time.DaysPerMonth,
            DaysPerYear = (int)DaysPerYear(config),
        };
        return (ladder, tuning);
    }
}
