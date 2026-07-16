namespace ParadiseCultivation;

/// <summary>
/// EVERY user-facing string of the slice — the config-over-constants rule applied to text.
/// Templates use invariant <c>string.Format</c> positional slots ({0}, {1}, …); the code
/// documents each slot's meaning on the consuming call. Authoring one config file therefore
/// localizes the whole game (the shipped config is Chinese; keep to the
/// ChineseSimplifiedCommon glyph set the font atlas bakes).
/// </summary>
public sealed record TextConfig
{
    /// <summary>Chronicle lines logged at the start of a new journey (the onboarding
    /// guidance beat). Slots: {0} player name, {1} home town name.</summary>
    public required string[] Intro { get; init; }
    public required UiTextConfig Ui { get; init; }
    public required MessageTextConfig Messages { get; init; }
}

/// <summary>Panel titles, labels, buttons, tooltips. ImGui uses window TITLES as IDs — keep
/// them unique.</summary>
public sealed record UiTextConfig
{
    public required string NewGameTitle { get; init; }
    public required string NewGamePitch { get; init; }
    public required string SeedLabel { get; init; }
    public required string WorldLabel { get; init; }
    public required string RerollWorld { get; init; }
    public required string RerollFate { get; init; }
    /// <summary>{0} player name.</summary>
    public required string NameLine { get; init; }
    /// <summary>{0} element, {1} grade name, {2} multiplier.</summary>
    public required string SpiritRootLine { get; init; }
    /// <summary>{0} charm tier name.</summary>
    public required string CharmLine { get; init; }
    /// <summary>{0} town count, {1} sect count.</summary>
    public required string WorldSummaryLine { get; init; }
    public required string BeginJourney { get; init; }
    public required string LoadSavedJourney { get; init; }

    public required string MapTitle { get; init; }
    /// <summary>{0} width, {1} height.</summary>
    public required string MapHelp { get; init; }
    public required string TooFarHint { get; init; }
    /// <summary>{0} days, {1} mode.</summary>
    public required string TravelTooltip { get; init; }
    public required string NoFootPath { get; init; }
    public required string BeyondSight { get; init; }
    /// <summary>{0} vein quality.</summary>
    public required string VeinTooltip { get; init; }
    public required string TownWord { get; init; }
    public required string SectWord { get; init; }

    public required string StatusTitle { get; init; }
    /// <summary>{0} year, {1} month, {2} day — also used by chronicle/memory timestamps.</summary>
    public required string DateFormat { get; init; }
    /// <summary>{0} age years, {1} lifespan years.</summary>
    public required string AgeLine { get; init; }
    /// <summary>{0} points, {1} threshold, {2} monthly gain.</summary>
    public required string ProgressLine { get; init; }
    /// <summary>{0} charm name, {1} fortune, {2} reward multiplier.</summary>
    public required string CharmFortuneLine { get; init; }
    /// <summary>{0} stones, {1} herbs.</summary>
    public required string StonesHerbsLine { get; init; }
    /// <summary>{0} months remaining.</summary>
    public required string InjuredLine { get; init; }
    /// <summary>{0} bonus percent (already formatted, e.g. 20%).</summary>
    public required string OnVeinLine { get; init; }
    /// <summary>{0} days remaining.</summary>
    public required string TimeFlowsLine { get; init; }

    public required string ActionsTitle { get; init; }
    public required string MonthsLabel { get; init; }
    public required string YearsLabel { get; init; }
    public required string CultivateButton { get; init; }
    public required string SecludeButton { get; init; }
    public required string ExploreButton { get; init; }
    /// <summary>{0} success chance (formatted percent).</summary>
    public required string BreakthroughReadyLine { get; init; }
    public required string BreakthroughButton { get; init; }
    public required string BreakthroughLockedLine { get; init; }
    public required string SaveButton { get; init; }
    public required string LoadButton { get; init; }

    public required string LocationTitle { get; init; }
    /// <summary>{0} terrain name.</summary>
    public required string WildernessLine { get; init; }
    /// <summary>{0} vein quality, {1} terrain name.</summary>
    public required string WildernessVeinLine { get; init; }
    public required string SelectSomeone { get; init; }
    public required string EncounterTitle { get; init; }
    /// <summary>{0} beast name, {1} its realm name.</summary>
    public required string EncounterLine { get; init; }
    /// <summary>Appraisal by expected-power gap: [too strong, even match, beatable].</summary>
    public required string[] EncounterJudgeNames { get; init; }
    public required string FightButton { get; init; }
    public required string FleeButton { get; init; }

    public required string RealmSectionTitle { get; init; }
    /// <summary>{0} realm name.</summary>
    public required string RealmHereLine { get; init; }
    /// <summary>{0} success chance (formatted percent).</summary>
    public required string RealmChanceLine { get; init; }
    public required string EnterRealmButton { get; init; }
    public required string RealmSpentLine { get; init; }
    /// <summary>Octant names, x-east y-south order: 东 东南 南 西南 西 西北 北 东北.</summary>
    public required string[] DirectionNames { get; init; }

    public required string SectSectionTitle { get; init; }
    public required string JoinSectButton { get; init; }
    /// <summary>{0} current leader affection, {1} required.</summary>
    public required string JoinReqAffectionLine { get; init; }
    /// <summary>{0} required spirit-root grade name.</summary>
    public required string JoinReqRootLine { get; init; }
    public required string SectNoLeaderLine { get; init; }
    /// <summary>{0} rank name, {1} monthly stipend.</summary>
    public required string MemberRankLine { get; init; }
    /// <summary>{0} next rank name, {1} required realm name.</summary>
    public required string NextRankLine { get; init; }
    public required string TopRankLine { get; init; }
    public required string LeaveSectButton { get; init; }
    /// <summary>{0} the sect the player already belongs to.</summary>
    public required string OtherSectLine { get; init; }
    /// <summary>{0} sect name, {1} rank name (status panel).</summary>
    public required string StatusSectLine { get; init; }

    public required string MarketTitle { get; init; }
    /// <summary>{0} player herbs, {1} stones per herb at this town.</summary>
    public required string MarketHerbLine { get; init; }
    public required string SellOneButton { get; init; }
    public required string SellAllButton { get; init; }
    /// <summary>{0} pill stock, {1} pill price.</summary>
    public required string MarketPillLine { get; init; }
    public required string BuyPillButton { get; init; }
    /// <summary>{0} pill count (status panel; shown only when &gt; 0).</summary>
    public required string PillsLine { get; init; }
    /// <summary>{0} bonus (formatted percent) — under the breakthrough-ready line.</summary>
    public required string PillReadyLine { get; init; }
    public required string SectLeaderTag { get; init; }
    /// <summary>Terrain display names, indexed by <see cref="Terrain"/> (8 entries).</summary>
    public required string[] TerrainNames { get; init; }
    /// <summary>{0} stone count.</summary>
    public required string GiftButton { get; init; }
    public required string SparButton { get; init; }
    public required string SayButton { get; init; }
    /// <summary>{0} affection value, {1} tier name.</summary>
    public required string TheirRegardLine { get; init; }
    /// <summary>{0} affection value, {1} tier name.</summary>
    public required string YourRegardLine { get; init; }
    /// <summary>{0} age years, {1} lifespan years.</summary>
    public required string NpcAgeLine { get; init; }
    public required string TheyRemember { get; init; }

    public required string ChronicleTitle { get; init; }
    public required string DeathTitle { get; init; }
    /// <summary>{0} player name, {1} realm title, {2} age years.</summary>
    public required string DeathLine { get; init; }
    public required string ReincarnateButton { get; init; }

    /// <summary>{0} realm name, {1} sub-stage name.</summary>
    public required string RealmTitleFormat { get; init; }
}

/// <summary>Runner-produced messages, chronicle entries, and memory log lines.</summary>
public sealed record MessageTextConfig
{
    /// <summary>{0} player name, {1} home name.</summary>
    public required string ArrivalLog { get; init; }
    public required string WildsName { get; init; }
    /// <summary>{0} player name, {1} age years, {2} realm name.</summary>
    public required string DeathLog { get; init; }

    public required string WalkMode { get; init; }
    public required string FlightMode { get; init; }
    /// <summary>{0} mode, {1} days.</summary>
    public required string TravelStartMsg { get; init; }
    public required string TravelStepBlocked { get; init; }
    public required string TravelNoPath { get; init; }
    /// <summary>{0} site name, {1} days, {2} mode.</summary>
    public required string ArrivedAtMsg { get; init; }
    /// <summary>{0} days, {1} mode.</summary>
    public required string TraveledMsg { get; init; }
    public required string Occupied { get; init; }

    /// <summary>{0} months.</summary>
    public required string CultivateStartMsg { get; init; }
    /// <summary>{0} months, {1} points gained, {2} realm title.</summary>
    public required string CultivateDoneMsg { get; init; }
    /// <summary>{0} player name, {1} years.</summary>
    public required string SecludeEnterLog { get; init; }
    /// <summary>{0} years.</summary>
    public required string SecludeStartMsg { get; init; }
    /// <summary>{0} player name, {1} years.</summary>
    public required string SecludeLeaveLog { get; init; }
    /// <summary>{0} years, {1} points gained, {2} realm title.</summary>
    public required string SecludeDoneMsg { get; init; }

    public required string BreakthroughNotReady { get; init; }
    public required string TribulationFlavor { get; init; }
    /// <summary>{0} player name, {1} new realm name, {2} tribulation flavor (may be empty).</summary>
    public required string BreakthroughLog { get; init; }
    /// <summary>{0} new realm title, {1} tribulation flavor, {2} lifespan years.</summary>
    public required string BreakthroughMsg { get; init; }
    /// <summary>{0} player name, {1} attempted realm name.</summary>
    public required string BreakthroughFailLog { get; init; }
    /// <summary>{0} injury months.</summary>
    public required string BreakthroughFailInjuryMsg { get; init; }
    public required string BreakthroughFailMsg { get; init; }

    /// <summary>{0} joined find list.</summary>
    public required string ExploreFoundMsg { get; init; }
    public required string ExploreListSeparator { get; init; }
    /// <summary>Flavor variants, {0} herb count.</summary>
    public required string[] ExploreHerbs { get; init; }
    /// <summary>Flavor variants, {0} stone count.</summary>
    public required string[] ExploreStones { get; init; }
    /// <summary>Flavor variants, {0} insight points.</summary>
    public required string[] ExploreInsight { get; init; }
    /// <summary>{0} player name.</summary>
    public required string ExploreInsightLog { get; init; }
    /// <summary>Atmosphere lines when nothing is found (picked by the saved RNG stream).</summary>
    public required string[] ExploreNothing { get; init; }

    /// <summary>{0} beast name.</summary>
    public required string EncounterMsg { get; init; }
    public required string EncounterBlocksMsg { get; init; }
    /// <summary>{0} rounds, {1} beast, {2} stones, {3} herbs, {4} insight.</summary>
    public required string FightWinMsg { get; init; }
    /// <summary>{0} player name, {1} beast.</summary>
    public required string FightWinLog { get; init; }
    /// <summary>{0} beast, {1} injury months.</summary>
    public required string FightLoseMsg { get; init; }
    /// <summary>{0} beast.</summary>
    public required string FleeOkMsg { get; init; }
    /// <summary>{0} beast, {1} injury months.</summary>
    public required string FleeFailMsg { get; init; }

    /// <summary>{0} realm name.</summary>
    public required string RealmOpenLog { get; init; }
    /// <summary>{0} realm name, {1} direction, {2} distance (li), {3} months remaining.</summary>
    public required string RumorRealmMsg { get; init; }
    public required string RealmNotHereMsg { get; init; }
    public required string RealmSpentMsg { get; init; }
    /// <summary>{0} player name, {1} realm name.</summary>
    public required string RealmSuccessLog { get; init; }
    /// <summary>{0} realm name, {1} stones, {2} herbs, {3} insight points.</summary>
    public required string RealmSuccessMsg { get; init; }
    /// <summary>{0} realm name, {1} injury months.</summary>
    public required string RealmFailMsg { get; init; }

    public required string JoinNoSectMsg { get; init; }
    /// <summary>{0} the sect the player already belongs to.</summary>
    public required string JoinAlreadyMemberMsg { get; init; }
    /// <summary>{0} current leader affection, {1} required.</summary>
    public required string JoinNeedAffectionMsg { get; init; }
    public required string JoinNeedRootMsg { get; init; }
    public required string JoinNoLeaderMsg { get; init; }
    /// <summary>{0} sect name, {1} starting rank name.</summary>
    public required string JoinDoneMsg { get; init; }
    /// <summary>{0} player name, {1} sect name, {2} starting rank name.</summary>
    public required string JoinSectLog { get; init; }
    public required string LeaveNotHereMsg { get; init; }
    /// <summary>{0} sect name.</summary>
    public required string LeaveDoneMsg { get; init; }
    /// <summary>{0} player name, {1} sect name.</summary>
    public required string LeaveSectLog { get; init; }
    /// <summary>{0} player name, {1} sect name, {2} new rank name.</summary>
    public required string SectPromoteLog { get; init; }

    public required string TradeNoMarketMsg { get; init; }
    /// <summary>{0} herbs sold, {1} stones received.</summary>
    public required string SellDoneMsg { get; init; }
    public required string SellNothingMsg { get; init; }
    /// <summary>{0} stones paid.</summary>
    public required string BuyPillDoneMsg { get; init; }
    public required string BuyPillNoStockMsg { get; init; }
    /// <summary>{0} pill price.</summary>
    public required string BuyPillNeedStonesMsg { get; init; }
    /// <summary>{0} bonus (formatted percent) — prefixed to the breakthrough result.</summary>
    public required string PillUsedNote { get; init; }

    /// <summary>{0} stone count.</summary>
    public required string GiftNeedMsg { get; init; }
    /// <summary>{0} player name, {1} stone count.</summary>
    public required string GiftMemory { get; init; }
    /// <summary>{0} npc name, {1} stone count, {2} affection tier.</summary>
    public required string GiftReply { get; init; }
    /// <summary>{0} player name.</summary>
    public required string SparWinMemory { get; init; }
    /// <summary>{0} player name.</summary>
    public required string SparLoseMemory { get; init; }
    /// <summary>{0} npc name, {1} insight points.</summary>
    public required string SparWinReply { get; init; }
    /// <summary>{0} npc name.</summary>
    public required string SparLoseReply { get; init; }
    /// <summary>{0} player name, {1} truncated line.</summary>
    public required string ChatMemory { get; init; }
    public required string ChatFallbackReply { get; init; }

    /// <summary>{0} path.</summary>
    public required string SaveDoneMsg { get; init; }
    /// <summary>{0} error.</summary>
    public required string SaveFailMsg { get; init; }
    public required string SaveBusyMsg { get; init; }
    /// <summary>{0} path.</summary>
    public required string LoadDoneMsg { get; init; }
    /// <summary>{0} error.</summary>
    public required string LoadFailMsg { get; init; }
    /// <summary>{0} found version, {1} expected version.</summary>
    public required string LoadVersionMsg { get; init; }
    public required string LoadMalformedMsg { get; init; }
    public required string LoadBusyMsg { get; init; }

    /// <summary>{0} npc name, {1} site name, {2} new realm name.</summary>
    public required string NpcBreakthroughLog { get; init; }
    /// <summary>{0} npc name, {1} site name.</summary>
    public required string NpcDeathLog { get; init; }
}
