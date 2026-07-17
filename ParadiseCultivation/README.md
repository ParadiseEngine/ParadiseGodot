# ParadiseCultivation — "Imortals" vertical slice

A playable slice of the *Imortals* immortal-cultivation sandbox (high-concept design v2.0),
built on Paradise.ECS + the repo's snapshot machinery, hosted by **both** Paradise hosts:

```sh
# Standalone .NET runtime (SDL window; add --headless N / --screenshot x.bmp for CI)
dotnet run --project ParadiseRuntime -- --game cultivation [--seed N] [--world-size 0|1]

# Godot play mode
<godot> --path . res://scenes/cultivation.tscn
```

Run tests with `dotnet test --project ParadiseCultivation.Tests` (TUnit).

## The locked direction (high-concept v2.0), as implemented

| Locked decision | Slice implementation |
|---|---|
| Two worlds, no size selection | Config presets: **32x32 Demo** (1 town, 1 sect, 6 NPCs) and **256x256 formal** (20 towns, 8 sects, 400 NPCs; generation-time test guards the 60 s budget) |
| 8 base terrains, no rivers/roads/sea | Plains / Forest / Hills / Mountains / Water / Desert / Snowfield / Swamp from three noise channels (elevation, moisture, temperature); Water = inland lakes, impassable on foot |
| Layered map data | L0/L1 = `Tile.Terrain`, L3 spiritual energy = `Tile.VeinQuality` (an overlay on any dry land, no longer a terrain type), L4 = `Tile.SiteIndex`; L2 removed by design, L5 runtime-only |
| Same-seed reproducibility + validation reroll | A world must place every site, contain a vein, and keep all sites foot-reachable (sites confined to the largest walkable landmass) or it rerolls on a derived seed — deterministically |
| Isometric ink-style travel map | 2:1 iso diamonds with light grid lines and an ink-wash placeholder palette, continuous wheel zoom, drag-pan, screen-rect culling (256x256 stays smooth), site markers + labels |
| The player is a moving character | Travel walks a terrain-cost A* path tile-by-tile as game days tick (view follows); WASD steps one tile (4-adjacency); click within the observable range to travel; fog beyond it |
| Time via `total_days` | 30-day months / 360-day years, action costs round up (min 1 day), age never resets — a new major realm only raises the lifespan cap |
| Saves established early | Versioned JSON (`SaveData` v1): seed + preset (map re-derives), calendar, PCG RNG state (loaded games continue the same random stream), player/NPC components, memories, chronicle. Corrupt/wrong-version loads fail WITHOUT touching the running world |
| LLMs propose, rules decide | `INpcInteractionProposer` returns a structured `InteractionProposal`; `ProposalRules` clamps affection suggestions to the config budget and sanitizes text; the deterministic `TemplateProposer` is the always-available OFFLINE fallback |

Carried over from the earlier slice: the 10-realm ladder with deliberate breakthroughs,
two-way affection with the design-doc tier table, charm scaling, diminishing chat returns,
per-NPC memory logs surviving seclusion, monthly `SettlementSystem` (ECS) driving the living
world, and CJK-capable chat (system-font loading in `ParadiseUi/UiFonts`).

## The shipped content is Chinese

EVERY user-facing string lives in the config set — code contains mechanisms only, so the
authored files localize the whole game. The set is split for maintainability
(`CultivationConfig.Load` composes them; `ConfigFiles.All`):

- `config.json` — gameplay tunables (world, time, realms, interaction economy, ui)
- `names.json` — name pools (surnames/given, town/sect parts)
- `dialogue.json` — ~3000 compiled reply lines: five affection-tier buckets (~2000 lines,
  pools change as relationships climb 陌生→相识→好友→知己), 17 keyword intents with ~28
  variants each (交易/拜师/切磋/传闻/丹药/功法/灵脉/天劫/妖兽/秘境/灵石/寿元/大道/来历/
  称呼/双修/道别), and per-personality pools (孤傲/温婉/心机/爽朗/清冷/豪迈/多疑/儒雅,
  ~59 lines each)
- `text.json` — UI labels, message templates, the onboarding intro, explore flavor pools

`dialogue.json` and the flavor pools are BUILD OUTPUT of
`tools/cultivation/generate_dialogue.py` — the maintained source: ~450 hand-authored
sentence lexicons composed with attribution frames (two alternating shapes). Edit the
lexicons there and re-run the script; hand-written signature lines stay verbatim at the
front of each pool.

Reply selection hashes (line, npc, MEMORY COUNT): deterministic per state — replays and
saves reproduce exactly — yet asking the same question again gives a different answer as the
shared history grows. The font atlas bakes the common-Chinese ranges UNION every character
appearing across ALL config files (`UiFontConfig.GlyphSourceText` → ImGui glyph-ranges
builder), so authored rare hanzi always render. Tests assert config-derived strings
(`Fixture.Skeleton`), never hardcoded English.

## Architecture — ParadiseGame's ECS + snapshot machinery

- **`CultivationRunner` is the `SimulationRunner` analog.** A 60 Hz sim thread advances the
  ECS world as immutable snapshots: each tick rents a write-world from a pool PRE-CREATED on
  the owner thread (SharedWorld.CreateWorld is thread-affinity-guarded), `CopyFrom`s the
  current world, mutates the copy, publishes it. Readers pin snapshot pairs via
  `TrySampleInterpolation`; a world recycles only when unpinned and outside the window.
- **Components** (`Ecs/Components.cs`): `Cultivator` (player + NPC progression), `NpcState`,
  `PlayerData`, per-tick `SimulationContext`, and config-baked `RealmLadder`/`SettlementTuning`
  (systems cannot reach managed config, so the numbers ride on entities).
- **`SettlementSystem`** runs under `[SingleWriter]` + `[SnapshotReadSystems]`: every crossed
  month each NPC cultivates, breaks through, ages, dies — pure hash RNG per (world seed,
  npc id, month index), deterministic regardless of scheduling. Flags feed the runner's
  managed post-pass (chronicle + replacement spawns).
- **Actions are commands**, animated by the time flow (`time.flow`), ending on the exact
  target day. All randomness is a serializable `Pcg32` stream (the save captures it).
- **Outside the ECS, deliberately:** the immutable `WorldMap` (navmesh/CollisionWorld
  precedent), string logs as sim-thread side stores, names/personalities as config-pool
  indices so components stay unmanaged.
- **One UI, two hosts, sim-thread panels.** `CultivationUi` draws through the shared
  `ImGuiUiCore`; the runner pumps its input half each fixed step, so panels read
  `runner.UiWorld` directly (raw `Entity` handles) and mutate only via commands.
  ParadiseRuntime composites via the WebGPU OverlayPass; Godot replays snapshots as canvas
  items.
- **Deterministic end to end**, guarded by `SnapshotTests.same_seed_and_commands_produce_identical_worlds`
  and `SaveLoadTests.loaded_games_continue_the_same_random_stream`.

Deferred (per the staged scope): trade/crafting, combat encounter, secret realm (P2);
layered day/year/checkpoint settlement, sect economy, dao companions (Alpha); Divine Path /
UGC / async social (post-1.0, extension slots only).
