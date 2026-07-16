# ParadiseCultivation — Immortal Cultivation vertical slice

A playable slice of the *Immortal Cultivation Open World Sandbox* design
(`Immortal_Cultivation_Game_Detailed_Design_EN.md`), built on Paradise.ECS + the repo's
snapshot machinery, hosted by **both** Paradise hosts:

```sh
# Standalone .NET runtime (SDL window; add --headless N / --screenshot x.bmp for CI)
dotnet run --project ParadiseRuntime -- --game cultivation [--seed N] [--world-size 0..2]

# Godot play mode
<godot> --path . res://scenes/cultivation.tscn
```

Run tests with `dotnet test --project ParadiseCultivation.Tests` (TUnit).

## What is implemented (MVP pillars from the high-concept doc)

| Design pillar | Slice implementation |
|---|---|
| Procedural world | Seeded value-noise terrain (plains/forest/river/mountain), spirit veins with 4 quality tiers, greedy town/sect placement, reroll UI (`WorldGenerator`) |
| Time as a resource | Calendar (30-day months); every action costs days and ANIMATES on the sim thread (config `time.flow`), lifespan per realm, death by lifespan |
| Cultivation ladder | All 10 realms × 4 sub-stages, auto sub-stage advance, deliberate major breakthrough with fortune/vein-modified chance, failure injuries, tribulation flavor at Golden Core+ |
| Memory-driven NPCs | Two-way affection (design-doc tier table), charm multiplier on positive gains, diminishing chat returns, persistent per-NPC memory log surviving seclusion |
| Living world | `SettlementSystem` (ECS): every crossed month, each NPC cultivates, breaks through (chronicled rumors), ages, dies and is replaced — deterministic per-NPC-per-month hash RNG |
| System-governed AI | `INpcDialogue` seam: deterministic keyword-intent + affection-bucket template dialogue today; an LLM-backed implementation can be swapped in without touching game/UI code |

Deferred (per the doc's own "Can Be Deferred" list): LLM gateway, combat scenes, sect
membership progression, alchemy/crafting, dao companions, secret realms, Divine Path, UGC.

## Architecture — ParadiseGame's ECS + snapshot machinery

- **`CultivationRunner` is the `SimulationRunner` analog.** A 60 Hz sim thread advances the
  ECS world as immutable snapshots: each tick rents a write-world from a pool PRE-CREATED on
  the owner thread (SharedWorld.CreateWorld is thread-affinity-guarded), `CopyFrom`s the
  current world, mutates the copy, publishes it. Readers pin snapshot pairs via
  `TrySampleInterpolation`; a world recycles only when unpinned and outside the window.
- **Components** (`Ecs/Components.cs`): `Cultivator` (player + NPC progression), `NpcState`,
  `PlayerData`, per-tick `SimulationContext` (dt, day, crossed months — the ParadiseGame
  pattern for shared frame data), and config-baked `RealmLadder`/`SettlementTuning` (systems
  cannot reach managed config, so the numbers ride on entities; config-over-constants holds).
- **`SettlementSystem`** runs under `[SingleWriter]` + `[SnapshotReadSystems]`
  (SnapshotDagScheduler + ParallelWaveScheduler, one parallel wave). Per-NPC-per-month
  randomness is a pure hash of (world seed, npc id, month index) — deterministic regardless
  of scheduling. It raises `JustBrokeThrough`/`JustDied` flags; the runner's managed
  post-pass turns them into chronicle entries and replacement spawns (structural + string
  work systems can't do). Player settlement is managed-code (action-driven, vein/root/injury
  modifiers) — untracked writes outside the system-injection model, per the assembly attrs.
- **Actions are commands.** The UI/hosts enqueue `CultivationCommand`s from any thread; the
  sim thread applies them. Time-consuming actions start an animated advance (game days flow
  at `time.flow.daysPerSecond`, accelerated so no action exceeds `maxActionSeconds`), ending
  on the EXACT target day (integer-valued double completion).
- **Outside the ECS, deliberately:** the immutable terrain/site `WorldMap` (the
  navmesh/CollisionWorld precedent — static data is not simulation state) and the string
  logs (chronicle + per-NPC memories, sim-thread side stores on the runner). Names and
  personalities are indices into config pools, so components stay unmanaged.
- **One UI, two hosts, sim-thread panels.** `CultivationUi` registers one draw delegate on
  the shared `ImGuiUiCore`; the RUNNER pumps its input half every fixed step, so panels run
  on the sim thread reading `runner.UiWorld` directly (BankHeist direct ECS access — raw
  `Entity` handles, `GetComponent`, no facade) and mutate only via commands. ParadiseRuntime
  composites via the WebGPU OverlayPass (`CultivationHost`); Godot replays the snapshots as
  canvas items (`runtime/CultivationBridge.cs` + `ImGuiCanvasRenderer`).
- **Deterministic.** World generation and full runs are reproducible per (seed, commands):
  integer-hash noise, seeded `Random` on the sim thread, hash-based settlement, FNV dialogue
  selection. Guarded by `SnapshotTests.same_seed_and_commands_produce_identical_worlds`.
