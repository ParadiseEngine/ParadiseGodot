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
        return F(config.Text.Ui.DateFormat, year, month, dayOfMonth);
    }

    /// <summary>Invariant positional formatting for every authored text template.</summary>
    public static string F(string template, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);

    public static string RealmTitle(CultivationConfig config, int realmIndex, int subStage) =>
        F(config.Text.Ui.RealmTitleFormat, config.Realms[realmIndex].Name, config.SubStages[subStage]);

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

    public static float VeinBonusAt(CultivationConfig config, WorldMap map, int x, int y) =>
        config.VeinCultivationBonus[map.TileAt(x, y).VeinQuality]; // L3 layer, any terrain

    /// <summary>How far the player sees (and may click destinations): divine sense grows
    /// with realm — base + per-realm bonus, capped. Fog, vein visibility, and the travel
    /// click gate all follow this one number.</summary>
    public static int ObservableRange(CultivationConfig config, int realmIndex) =>
        Math.Min(config.Ui.ObservableRangeMax,
            config.Ui.ObservableRange + realmIndex * config.Ui.ObservableRangePerRealm);

    /// <summary>Percent display through the authored format (invariant :P0 inserts a
    /// non-breaking space before %, which reads wrong in Chinese text).</summary>
    public static string Percent(CultivationConfig config, double fraction) =>
        F(config.Text.Ui.PercentFormat, (int)Math.Round(fraction * 100.0));

    /// <summary>Player points per cultivated month: realm base × spirit root × vein bonus ×
    /// the day's world-event multiplier × dual cultivation (when at the companion's side),
    /// halved while injured.</summary>
    public static double MonthlyCultivationGain(
        CultivationConfig config, WorldMap map, in Cultivator cultivator, in PlayerData player, long day,
        bool companionPresent = false)
    {
        var realm = config.Realms[cultivator.RealmIndex];
        var gain = realm.MonthlyBasePoints
            * config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier
            * (1f + VeinBonusAt(config, map, player.X, player.Y));
        if (player.SectSiteIndex >= 0 &&
            map.TileAt(player.X, player.Y).SiteIndex == player.SectSiteIndex)
        {
            gain *= 1f + config.Sect.MemberCultivationBonus; // training at one's own mountain
        }
        if (companionPresent)
        {
            gain *= 1f + config.Companion.DualCultivationBonus; // dual cultivation
        }
        gain *= WorldEvents.Multiplier(config, map.GenerationSeed, day, WorldEventEffect.CultivationGain);
        return player.InjuryMonths > 0 ? gain * 0.5 : gain;
    }

    public static float FortuneMultiplier(CultivationConfig config, float fortune) =>
        Math.Min(1f + fortune * config.Fortune.RewardPerPoint, config.Fortune.MaxMultiplier);

    public static bool BreakthroughReady(CultivationConfig config, in Cultivator cultivator) =>
        cultivator.RealmIndex < config.Realms.Length - 1 &&
        cultivator.SubStage == config.SubStages.Length - 1 &&
        cultivator.CultivationPoints >= config.Realms[cultivator.RealmIndex].PointsPerSubStage;

    /// <summary>Eligible for the final tribulation: the last realm's Perfected peak with a
    /// full points bar (the <see cref="BreakthroughReady"/> analog for the ladder's top).</summary>
    public static bool AscensionReady(CultivationConfig config, in Cultivator cultivator) =>
        cultivator.RealmIndex == config.Realms.Length - 1 &&
        cultivator.SubStage == config.SubStages.Length - 1 &&
        cultivator.CultivationPoints >= config.Realms[cultivator.RealmIndex].PointsPerSubStage;

    /// <summary>The final tribulation's success chance — base + fortune, the same shape as
    /// the secret-realm trial.</summary>
    public static float AscensionChance(CultivationConfig config, in PlayerData player) =>
        Math.Clamp(
            config.Ascension.BaseChance + player.Fortune * config.Ascension.FortuneChancePerPoint,
            0.05f, 0.95f);

    public static float BreakthroughSuccessChance(
        CultivationConfig config, WorldMap map, in Cultivator cultivator, in PlayerData player)
    {
        var chance = config.Realms[cultivator.RealmIndex].BreakthroughChance
            + player.Fortune * config.Fortune.BreakthroughChancePerPoint
            + VeinBonusAt(config, map, player.X, player.Y) * 0.05f;
        return Math.Clamp(chance, 0.05f, 0.98f);
    }

    /// <summary>Deterministic per-town price factor in 1 ± spread — hashed from the world's
    /// generation seed and the site index, so no dynamic state and every load agrees.</summary>
    public static float TownPriceMultiplier(CultivationConfig config, WorldMap map, int siteIndex)
    {
        var h = (uint)map.GenerationSeed * 2654435761u ^ ((uint)siteIndex + 0x9E3779B9u) * 2246822519u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        var unit = (h & 0xFFFFu) / 65535f;
        return 1f + (unit * 2f - 1f) * (config.Trade.PriceSpreadPercent / 100f);
    }

    /// <summary>Stones this town pays per herb (base × town factor × the day's world-event
    /// multiplier, floored at 1).</summary>
    public static int HerbSellStones(CultivationConfig config, WorldMap map, int siteIndex, long day) =>
        Math.Max(1, (int)MathF.Round(config.Trade.HerbSellStones * TownPriceMultiplier(config, map, siteIndex)
            * WorldEvents.Multiplier(config, map.GenerationSeed, day, WorldEventEffect.HerbPrice)));

    /// <summary>Stones one breakthrough pill costs at this town (event-month aware).</summary>
    public static int PillCostStones(CultivationConfig config, WorldMap map, int siteIndex, long day) =>
        Math.Max(1, (int)MathF.Round(config.Trade.PillCostStones * TownPriceMultiplier(config, map, siteIndex)
            * WorldEvents.Multiplier(config, map.GenerationSeed, day, WorldEventEffect.PillPrice)));

    /// <summary>The month's sect-mission archetype index for a sect site — hash-derived from
    /// (world seed, site, month) like the secret-realm/event schedules: deterministic per
    /// world, nothing saved, every load agrees. -1 when no missions are authored.</summary>
    public static int SectMissionIndex(CultivationConfig config, int worldSeed, int siteIndex, long monthIndex)
    {
        var missions = config.Sect.Missions;
        if (missions.Length == 0)
        {
            return -1;
        }
        var h = (uint)worldSeed * 2654435761u
            ^ ((uint)siteIndex + 0x632BE5ABu) * 2246822519u
            ^ ((uint)monthIndex + 0x9E3779B9u) * 3266489917u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return (int)(h % (uint)missions.Length);
    }

    /// <summary>Plan a journey (terrain-cost A* on foot / straight-line flight); null when
    /// the destination is unreachable. Thin alias over <see cref="Pathfinding.Plan"/>.</summary>
    public static TravelPlan? PlanTravel(
        CultivationConfig config, WorldMap map, int fromX, int fromY, int realmIndex, int toX, int toY) =>
        Pathfinding.Plan(config, map, fromX, fromY, realmIndex, toX, toY);

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
