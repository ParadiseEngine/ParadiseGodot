# Export Contract Conventions (pinned)

These are the conventions the Godot export tools **must** reproduce, enforced by
`Paradise.Export.Tests`.

## Handedness — the contract is right-handed (Godot / glTF standard)

The export contract stores transforms in **Y-up, right-handed** (+X right, +Y up, **−Z forward**),
matching Godot and glTF. The Godot exporter writes its transform values **verbatim** — there is
**no** handedness conversion at export time.

(History: the contract was originally pinned to Unity's left-handed convention (+Z forward) for
byte-parity with `ParadiseUnityEditor`. It was flipped to right-handed so the entire pipeline —
export data, the shared runtime simulation, and the engine — uses a single coordinate system. The
golden fixture `Fixtures/SampleScene.expected.json` is the old Unity baseline with its Z-dependent
values mirrored into this right-handed convention.)

Validation: Godot's default camera authored at `(0, 1, 10)` is exported verbatim as `(0, 1, 10)`.

## Color — linear, packed 8-bit

Colors are emitted as `{ "r", "g", "b", "a" }` objects with **linear** float channels packed to
8 bits per channel (`Color32`). The Unity exporter linearized sRGB authoring colors before
packing. The Godot exporter calls `Color.SrgbToLinear()` on authored colors (albedo, emissive,
light, ambient/fog) to match — Godot stores authored colors as sRGB while the contract is linear.

## Matrices — column-major

`Matrix4x4` is serialized as a flat `float[16]` in **column-major** order. (Translation from
`Matrix4x4.CreateTranslation(x,y,z)` lands at flat indices 3, 7, 11.)

## Serializer & numeric formatting (System.Text.Json)

Serialization uses **System.Text.Json** with source generation (`ParadiseJsonContext`) — chosen over
Newtonsoft.Json because Newtonsoft's static reflection caches pinned Godot's collectible
AssemblyLoadContext and broke C# hot-reload (godotengine/godot#78513). The library is AOT-compatible
(`IsAotCompatible`); hand-written converters supply the structural shapes (vectors/matrices as float
arrays, matrices column-major, `Color32` as `{ r, g, b, a }`, enums by name, nulls included).

**The contract is value-based, not byte-based.** Numbers use STJ's native formatting (e.g. `5`, not
the old Mono-Newtonsoft `5.0`; shortest round-trippable precision). The exported *values* are
unchanged; only their textual form differs. Golden fixtures (`*.expected.json`) are STJ-output
snapshots that guard against serializer drift, not byte-for-byte Unity matches.

## Colliders (Phase 2)

Collider shapes are emitted in the **entity root's local space with lossy scale folded into the
dimensions** (`ColliderScaleFold`), matching the Unity tool. The fold is the collider's scale
**relative to its entity root** — the root's own scale still lives in the entity `WorldMatrix`,
so a data consumer must fold it into the dimensions with the same rules and take the pose
rotation from a proper decomposition, never from the raw (scale-bearing) matrix basis
(`SceneAssembler.AppendCollider` / `DecomposePose` in Paradise.Sample.Runtime is the reference):

| Shape | Folded dimension |
|---|---|
| Box | `size × abs(relativeScale)` per axis |
| Sphere | `radius × max(|x|,|y|,|z|)` |
| Capsule (Y-aligned) | `radius × max(|x|,|z|)`, `height × |y|` |

Godot's `CapsuleShape3D` is always **Y-axis aligned** (no Unity-style `direction` enum), so only
the Y case is modeled — it equals Unity's `direction = 1` path. A non-Y capsule is authored by
rotating the node, captured in the collider's `LocalRotation`. Collider `Path` is root-exclusive
(empty when the collider is on the entity root itself).

## Entity transform matrices (Phase 2)

`LocalMatrix` / `WorldMatrix` are built by `ContractMatrix.Trs` in Unity's **column-vector layout**:
basis vectors in matrix columns, translation in the last column. After column-major flattening,
translation lands at flat indices **12/13/14** (matching Unity's `Matrix4x4.TRS`), not the 3/7/11
that System.Numerics' native row-vector `CreateTranslation` would produce.

## Entity GUID identity (Phase 2)

The per-placement entity GUID is stored in Godot node **metadata** (`paradise_entity_guid`,
persisted in the `.tscn`). `AuthoredEntityNode` mints it and enforces uniqueness among entity
nodes in the edited scene on `NOTIFICATION_EDITOR_PRE_SAVE`. This is one of the three things that
stay real code rather than becoming authored data — a schema cannot mint anything.

## Materials (Phase 3)

Godot `BaseMaterial3D` (StandardMaterial3D / ORMMaterial3D) → `LevelMaterialData`, one JSON per
material under `data/materials/`. Mapping:

- `BaseColorFactor` / `EmissiveFactor` — `AlbedoColor` / `Emission × EmissionEnergyMultiplier`,
  **sRGB→linear** (see above), packed 8-bit.
- `MetallicFactor` / `RoughnessFactor` — `Metallic` / `Roughness`.
- `AlphaMode` — from `Transparency`: `Disabled→Opaque`, `AlphaScissor`/`AlphaHash→Mask`, else
  `Blend`; also `Blend` when albedo alpha < 1 (mirrors the Unity resolution).
- Textures — referenced by **project-relative source path** (`res://` stripped). Actual texture
  **conversion (PNG/KTX2)** is the asset pipeline's job (Phase 6), not the material exporter.
- `Renderable.Materials` slot lists are filled from the entity's `MeshInstance3D` surfaces; the
  top-level `LevelData.Materials` stays empty (matches the Unity baseline). The slots sat on the
  ENTITY until contract v4 moved them onto the component whose GLB they index — three different
  things are called `Materials` in this pipeline, and only that one moved.

## Meshes & environment (schema v4)

- **`Renderable.Mesh`** — each entity with visuals exports its subtree to
  `data/meshes/<content-key>.glb` via Godot's native `GltfDocument` (no Blender round-trip for
  scene-authored meshes), in ENTITY-LOCAL space (the entity's `WorldMatrix` places it).
  Content-keyed dedupe: identical visual compositions (the two crates, the three balls) share
  one GLB; per-entity looks come from the `Renderable.Materials` slot overrides.
- **Slot order contract** — the GLB's primitive order equals the renderable's `Materials` slot
  order (both walk the same depth-first `MeshInstance3D` traversal). A null slot means the
  GLB's own embedded material is authoritative; non-null slots override with
  `materials/*.json` (factor-only at runtime — material-JSON texture paths reference Godot
  SOURCE files; the supported texturing route is GLB-embedded KTX2).
- **KTX2-only textures** — the `ktx create` pass (`KtxCreate.ConvertEmbeddedTextures`) is MANDATORY
  for GLBs embedding convertible images; the engine reader rejects PNG/JPEG payloads.
  Textureless GLBs pass without the tool (tool resolution happens only after the texture
  scan). Procedural textures (GradientTexture2D…) do NOT export — author file-based textures
  for anything that must survive the contract.
- **Everything is an entity** — static scenery (floor, walls, obstacles) is authored with
  `AuthoredEntityNode` like every prop (a `Node3D` root wrapping the `StaticBody3D` that keeps the
  `navigation_source` group), so ONE path covers visuals (`Renderable.Mesh` + dedupe),
  collision (`Collider` component) and placement (`WorldMatrix`); the CollisionWorld rebuilds
  from entity colliders alone. `Rigidbody.BodyType` includes `Dynamic` (authored on the
  `paradise.rigidbody` component) — the runtime spawns those as simulated balls.
- **Material naming** — sub-resource materials take their field name from the sub-resource id
  (`materials/mat_ball1.json`), not the scene filename (which used to collide).
- **Headless export** — `PARADISE_EXPORT_SCENE=res://scenes/x.tscn godot --headless --editor
  --path .` regenerates `data/` and exits (the CI/regeneration entry).

## Sprite animation & particles (sim-driven)

- **The SIMULATION owns all animation/particle state** — `SpriteAnimation` (flipbook clock →
  `Frame`) and `ParticleEmitter` (seeded xorshift RNG + inline 64-slot particle pool) are ECS
  components living in world snapshots, so both hosts render the identical frame/particles.
  `SpriteAnimationSystem.SampleFrame`/`SampleParticleFrame` are the ONLY sampling rules; both
  hosts call them over interpolated snapshot time. Particle slots are STABLE for a particle's
  life (renderers interpolate slot-wise; an older age in the later snapshot marks slot reuse →
  snap, don't sweep).
- **Contract components** — `Components.SpriteAnimation` (sheet, columns/rows/frameCount, fps,
  loop, quad size, billboard) and `Components.ParticleEmitter` (`Kind`: `Sprite` = flipbook
  camera-facing quads, `Voxel` = solid cubes; rate/lifetime/speed/spread cone around entity +Y/
  gravity/drag/size-over-life/seed/tint + the sprite-kind sheet). Both are optional (absent =
  null — backward compatible with older documents); both normalize via `ValidateAndNormalize`
  before writing. `MaxParticles` is capped at the runtime pool size (64).
- **Spritesheets** — source images live under `res://data/sprites/`; the contract stores the
  data-relative field with the RUNTIME extension (`sprites/torch.ktx2`). The ingest pass
  (`DataGlbConverter.ConvertSpriteSheets`, part of Paradise/Convert data GLBs → KTX2 and the
  import hook) encodes a KTX2 SIDECAR next to the source; Godot keeps rendering the source
  image, only the .NET runtime reads the sidecar. Frames are row-major, left-to-right then
  top-to-bottom.
- **Authoring** — tick `paradise.sprite-animation` and point it at a `Sprite3D`: grid, sheet and
  quad size are read off that node, the clock is authored. It is no longer discovered from the
  children. Likewise `paradise.particle-emitter` — the component's PRESENCE is what exports an
  emitter, so the old "kind = None means off" sentinel is gone.
- **Render halves** — Godot: the bridge writes `Sprite3D.Frame` and refills one
  `MultiMeshInstance3D` per emitter (billboard-particles material, flipbook phase in
  `INSTANCE_CUSTOM.z`; alpha BLEND — the engine shader has no cutout path). .NET: dynamic
  primitives (`SpriteQuadState` / `ParticleBatchState`) rewritten per frame, sheet material =
  standalone-KTX2 base color, `AlphaMode.Blend`.

## Runtime (Paradise.Sample.Runtime)

`Paradise.Sample.Runtime/` is the engine-renderer twin of `runtime/EcsSceneBridge.cs`: it loads the
exported `data/` (scene JSON via `ExportJsonReader`, GLBs via the engine's
`Paradise.Assets.Gltf`), rebuilds the CollisionWorld from the static
entities' colliders, spawns the SAME `SimulationRunner` sim (`Rigidbody.Dynamic` → `SpawnBall`),
and PBR-renders snapshots interpolated at the bridge's constants (delay 2/60, max lag 4/60,
Lerp/Slerp). Left-click drags to aim/strike the cue ball. Contract matrices are column-vector
layout — `SceneAssembler.ToModelMatrix` transposes to System.Numerics row-vector convention. The
camera projection mode is NOT in the contract (schema v3 candidate): the runtime defaults to
perspective 75° (Godot's default) with `--ortho`/`--fov` overrides.
`dotnet run --project Paradise.Sample.Runtime -- --scene data/scenes/sample.json [--headless N]`.

## Physics — stateless collision (runtime)

Movement collision is owned by **Paradise.Physics** (`ParadiseEngine/src/Paradise.Physics`), a
pure-C# stateless query library modeled on Unity Physics (DOTS): no caches, no incremental state,
`CollisionWorld.Build` is a pure function of its inputs, queries are allocation-free and
order-deterministic. All simulation state stays in ECS components, so world snapshots remain
complete (bank-heist's "physics state is ECS state" principle).

- **Geometry source** — the bridge harvests every `StaticBody3D` collider in the scene
  (Box/Sphere/Capsule, scale folded per `ColliderScaleFold`): the pool table bed, cushions, and
  frame rails the balls rest on and bounce off.
- **Layers** — Godot `collision_layer` maps to `CollisionFilter.BelongsTo`: bit 1 = Floor,
  bit 2 = Obstacle (`Paradise.Sample.Pool/Physics/PhysicsLayers`). Ball contacts collide with
  **Floor | Obstacle** — the felt the ball rests on (via gravity + contact) and the cushions it
  bounces off.
- **`MovementSystem` is the sole owner of the final `LocalTransform`** — one generated
  `IWorldSystem` (whole-query segment access, one `Execute` per tick) running the global ball
  dynamics step. Collision reaches the generated system through the read-only `PhysicsWorldRef`
  component: an unmanaged `CollisionWorldHandle` borrowed from the runner-owned `CollisionWorld`
  (default/invalid handle = unobstructed integration — casts miss and the ball integrates freely).
- **Spawn contract** — the `Balls` queryable REQUIRES `PhysicsWorldRef` and `SimulationContext`
  (plus the ball dynamics/config components). An entity missing any required component silently
  doesn't match the query: `MovementSystem` never sees it and it simply never moves, with no
  error anywhere. ALWAYS spawn through `SimulationRunner.SpawnBall` (or copy its builder chain
  verbatim, seeding `DeltaSeconds` and the collision handle) — a dt of 0 likewise makes the
  system skip the entity.
- The Godot physics server stays **Dummy** (2D and 3D): the sim owns physics; Godot must not run
  a second solver. Picking raycasts the sim's `CollisionWorld` instead of `DirectSpaceState`.
- **Dynamics** — sphere-only dynamic bodies (position = sphere center in `LocalTransform`). The
  resolver is the engine's stateless `Paradise.Physics.RigidSphereDynamics` (damp/integrate with
  cast-and-bounce vs statics → pairwise sphere impulses → static depenetration pass, full 3D under
  gravity); the game's `MovementSystem.StepBalls` only marshals components ↔ unmanaged scratch
  spans (stackalloc ≤64 bodies, else `NativeMemory` — the tick never touches the GC heap) and
  passes an empty kinematic-pusher span (no characters). Balls rest on the felt via gravity +
  contact (Y is live).
- **`CollisionWorld` storage is `Paradise.BLOB` in unmanaged memory (adopted with the BVH
  broadphase).** The world is one `NativeBlobAssetReference` blob root (NativeMemory-backed — no
  GC-heap pinning) — `{ BlobArray<Collider>, BlobArray<RigidTransform>, BlobArray<Aabb>,
  BlobTree<BvhNode> }` — with a preorder-BVH traversal (deterministic median-split build;
  guarded by brute-force differential tests). The public query API was unchanged by the swap;
  `CollisionWorld` is now `IDisposable` (blob finalizer is the backstop). Still deferred, with
  blob layout now ready for them: **mesh/convex/compound colliders** (variable-size geometry as
  in Unity Physics' `BlobAssetReference<Collider>`) and a **baked
  `data/scenes/<Scene>.collision.bin` export asset** (zero-parse load, golden-testable, replaces
  runtime scene harvesting). Referencing blobs from ECS components still needs an unmanaged
  pointer-backed `BlobAssetReference` handle in Paradise.BLOB first.
- **Rolling visuals** — game-side (engine stays transcendental-free): `MovementSystem`
  integrates ω = (Up × v)/r into `LocalTransform.Rotation` on write-back; the renderer's
  existing Slerp interpolation picks it up. Cosmetic only — sphere collision ignores rotation.

## Snapshot-read execution (systems run fully parallel)

`Paradise.Sample.Pool` opts into two assembly attributes that together define the system memory
model (`AssemblyInfo.cs`):

- **`[assembly: SingleWriter]`** — every component has at most ONE writer system (PECS3008
  analyzer error otherwise) ⇒ system writes are disjoint.
- **`[assembly: SnapshotReadSystems]`** — codegen binds systems' **read-only fields**
  (`ref readonly T`, `ReadOnlySpan<T>`, read-only queryable segments, all-readonly queryable
  data) to the **immutable CURRENT world** passed to `SystemSchedule.Run(readWorld)` (the
  previous tick), while **writable fields** bind to the WRITE world (`CopyFrom`-seeded, so a
  sole writer reads its own current values) ⇒ reads never alias in-flight writes.

Consequences and rules:
- The runner builds schedules with `SnapshotDagScheduler` (waves split only on write∩write and
  explicit `[After]`) + `ParallelWaveScheduler` — with the two attributes, all systems collapse
  into ONE fully parallel wave, deterministically (outcome independent of interleaving).
- **Read-only views are one tick stale by design.** Intra-tick chains must flow through writable
  fields of the same component — `MovementSystem` resolves the whole ball dynamics step inside a
  single `Execute`, so it needs no cross-system latency at all.
- **Managed pre-pass writes to the write world are invisible to read-only system fields** (they
  bind to the current world). This is why spawn builders seed `SimulationContext.DeltaSeconds`
  AND `PhysicsWorldRef` — without them the first tick reads dt = 0 / an invalid handle. A dt of
  0 makes `MovementSystem` skip the entity entirely.
- **World systems (`IWorldSystem`)** get whole-query flat segment access
  (`ComponentSegments<T>` per component, index-correlated across components) — use them for
  global work (pairwise physics, gather/solve/scatter) that per-entity systems can't express.
  They still schedule by masks like any system; a world system serializes within itself.
- No structural changes between `CopyFrom` and the schedule run (spawn/despawn go through the
  ECB or before the copy); entities born mid-tick fall back to same-world reads for that tick.
- `GameSimulation` runs the SAME model as the runner: it keeps a private previous-world snapshot
  (`CopyFrom(World)` at each tick start) as the read source and executes systems via
  `SnapshotDagScheduler` + `ParallelWaveScheduler` — the runner's semantics, minus the thread and
  snapshot pool. Both drivers therefore need `SimulationContext.DeltaSeconds` and
  `PhysicsWorldRef` seeded at spawn.

## Single-variable components

`Paradise.Sample.Pool` follows the immortal-cultivation discipline: **one variable per component**
(a single `Value` field), aggregated at use sites by `[Queryable]`. Writer-first splitting — each
MUTATED variable becomes its own component, so single-writer ownership (PECS3008) is enforced
per-variable and false write conflicts stay rare. `Components.cs` is the reference; the split
mirrors `MovementSystem`'s access (`Balls.Position[i].Value`, `Balls.Velocity[i].Value`, …).

THREE sanctioned exceptions keep a whole struct:
1. **read-only baked config bags** — an atomic snapshot of authored data, never partially written
   (`BallPhysicsConfig`, `SpriteConfig`, `ParticleConfig`, `PhysicsTuning`);
2. **inline-buffer / runtime-state bags** — an unmanaged inline array must live inside one component
   (`PocketConfig`, `ParticleState`);
3. nothing else.

The physics solver is untouched by the split: `MovementSystem.StepBalls` marshals the ball's
single-var + config components field-by-field into the external `RigidSphereDynamics`'s `DynamicSphere`.

## SystemEvents — the deferred fan-out bus

Cross-system "X happened → N reactors" signals ride the engine's `SystemEvents` bus (Paradise.ECS
0.5.x), not per-entity flags. A **system** producer injects a `SystemEventWriter` and `Append`s an
unmanaged event (`GameEvents.cs`); a **managed** producer calls `world.Events.Emit<T>` (0.5.2 — sim-
thread only, outside the wave); an owner-reactor consumes via an injected `SystemEventReader`
(`Inbox.Read<T>()`). Events are off-entity, one-frame-deferred (produced frame N → read N+1),
merged deterministically in schedule order, and snapshot-carried (`World.CopyFrom`). The pool sample
demonstrates all three roles: `MovementSystem` `Append`s `BallPocketed` on a pocket; `ScoreSystem`
(the sole writer of `Score`) reacts; `SimulationRunner.RequestReset` `Emit`s `GameReset`. Events fan
out to READERS; any shared mutation they trigger keeps ONE owner.

## UI — MVVM over the sim

The ImGui samples follow immortal-cultivation's MVVM split: a **ViewModel** (no `ImGuiNET`) projects
sim-snapshot state into display data and exposes command methods that drive the sim through its
command/event seam; a **View** is a thin immediate-mode ImGui renderer over one ViewModel, holding
only presentation state; a **composition root** owns the runner and wires the pair. Both run on the
sim thread (the immediate-mode contract); the UI never mutates sim state except through the
ViewModel's commands. This sits on the existing `ImGuiUiCore` draw-snapshot two-half (the sim thread
owns the ImGui frame; the render half only replays snapshots). The **pool** demo lives with pool
(`Paradise.Sample.Ui/PoolViewModel.cs` ↔ `Paradise.Sample.Ui/PoolView.cs`, root `PoolSampleUi`); the
generic `Paradise.Sample.ImGui` project keeps only the shared `ImGuiSampleRunner` sim-thread driver.

### Odyssey sample

`--game odyssey` (Godot: `scenes/odyssey.tscn`) is a **piloted 3D spaceship** flying a procedural
sector map (a star, orbiting planets, asteroids, a glowing warp gate) — a sci-fi re-skin of the same
architecture over the `Paradise.Sample.Odyssey` core. Pilot with **WASD** (thrust/turn), **hold SPACE**
to charge the warp drive, then **fly into the gate** to jump to the next sector (which regenerates);
**N** starts a new voyage. Rendered in **both** hosts with the same sim.

- **Sim (single-variable, owner systems).** The abstract warp/hull mechanic keeps its
  **intent → system → event → owner-reactor** seam: `WarpSystem` rolls a `WarpIntent` and `Append`s a
  `WarpResolved`; the owner-reactors (`ChargeSystem`/`VoyageSystem`) fold sector/hull/credits one frame
  later; `RequestNewVoyage` is a managed `Emit`. The **spatial layer** adds `Position`/`Rotation`
  (+ ship `Velocity`/`Heading`, body `OrbitAngle`/`SpinPhase`) written by **one** `MotionSystem` — the
  sole transform writer — over TWO disjoint segments (the ship vs the bodies), the merged-multi-segment
  pattern from immortal-cultivation's `MonthlySettlementSystem`.
- **Threaded snapshot runner.** `OdysseyRunner` is now the pool's proven threaded double-buffer model
  (world pool + 60 Hz sim thread + `TrySampleInterpolation` + locked state reads + a command queue for
  `SetThrust`/`SetTurn`/`SetCharging`/`RequestWarp`/`RequestNewVoyage`), so both hosts read transforms
  while the sim ticks. Fly-to-gate and per-sector map regeneration are managed passes between ticks (the
  body roster is fixed — a warp RESHUFFLES orbit config rather than respawning, so hosts build instances
  once). `TickOnce` is still public for synchronous tests.
- **SDL host** (`Paradise.Sample.Runtime/OdysseyHost.cs` + `ProcMesh.cs`) — procedural meshes (UV sphere,
  a cone ship, a torus gate) uploaded to `PbrScene`, emissive star/gate + bloom, a chase `PbrCamera`.
- **Godot host** (`runtime/OdysseyBridge.cs : Node3D` + `scenes/odyssey.tscn`) — built-in meshes
  (`SphereMesh`/`TorusMesh`/cone `CylinderMesh`), `StandardMaterial3D` (emissive star/gate + glow), a
  chase `Camera3D`; snapshot → `GlobalTransform` each `_Process`.
- **HUD** — the MVVM `OdysseyViewModel` ↔ `OdysseyView` ("Star Voyager": warp-charge/hull gauges, ship's
  log, seeded starfield) draws as a pure reader overlay (the sim owns its own thread). The pool ImGui
  demo is `--game pool`.

## Prefabs (Phase 5)

Godot's prefab model is **PackedScene instancing**. A node instanced from a scene carries
`Node.SceneFilePath`, and resources have stable `uid://` ids (the equivalent of Unity's asset GUID).
Identity maps cleanly onto the contract:

| Contract field | Godot source |
|---|---|
| `PrefabAssetPath` | nearest-instance-root `SceneFilePath` |
| `PrefabGuid` | `ResourceLoader.GetResourceUid` → `uid://…` |
| `PrefabAssetType` | the prefab file extension |
| `NearestInstanceRoot` | the nearest ancestor (or self) with `SceneFilePath` |

- **Template export** — each referenced prefab is written once to `data/prefabs/<name>.json`
  (`PrefabTemplateData`). Template entities are **shallow** (id / kind / transform / renderable);
  the authoritative per-placement component data comes from the scene export.
- **`ModelPrefabGenerator`** (`Paradise/Generate Model Prefabs`) — generates a clean
  `AuthoredEntityNode` root with the GLB/glTF instanced as a child. Idempotent: existing prefabs are left untouched,
  preserving hand-authored roots (the Godot equivalent of Unity's GUID-preserving regenerate).

### Override granularity — explicit decision (resolves the semantic gap)

Unity exported per-property prefab overrides via `PrefabUtility.GetPropertyModifications` /
`IsAddedComponentOverride`. **Godot has no equivalent C# API** — instance overrides live in the
outer `.tscn` text, not in a queryable model. **Decision:** the Godot exporter does **not** emit
override granularity; `Overrides` is written empty. This is acceptable because the scene export
already emits each placement's **full** transform, materials, and colliders, so the runtime can
diff instance-vs-prefab itself rather than relying on exported flags. Revisit only if the runtime
proves it needs the flags, in which case a `.tscn` parser would be required.

## Asset pipeline (Phase 6)

Both external CLIs are kept (per the migration decision); their orchestration ports near-verbatim
to engine-neutral Core (`Paradise.Export.Pipeline`), with only the trigger changing from Unity's
`AssetPostprocessor` to a Godot menu (`Paradise/Convert Models (FBX→GLB→KTX2)`).

- **`BlenderFbxGlb`** — headless Blender (`--background --factory-startup`, embedded Python,
  `export_yup=True`) converts FBX→GLB. Skips when unchanged: a SHA-256 of the FBX is stored in the
  GLB's `asset.extras`. Resolved from `PARADISE_BLENDER_PATH` / standard installs / PATH.
- **`KtxCreate`** — the KTX-Software v5 `ktx create` CLI (toktx was removed in v5) converts the
  GLB's embedded PNG/JPEG to KTX2 (Basis Universal) and
  rewrites the GLB to reference them via `KHR_texture_basisu`. Per-texture encoding preset is chosen
  from material slot usage (base/emissive → sRGB BasisLZ; metallic-roughness/occlusion → linear
  UASTC; normal → linear UASTC normal-mode), falling back to the image name. Resolved from
  `PARADISE_KTX_PATH` / the vendored `third_party/tools/KTX-Software/Darwin-arm64` (v5.0.0-rc1
  `bin/ktx` + `lib/libktx`) / PATH; macOS sets `DYLD_*` to the bundled libs.
- **Settings window** — `Project > Tools > Paradise/Settings…` sets machine-level ktx/Blender
  paths (stored in EditorSettings `paradise/tools/*`, never committed) and applies them as the
  `PARADISE_*_PATH` environment variables above at plugin load and on save — the first stop of
  both tools' resolution chains, so GUI-launched editors work without a shell PATH.
- **Graceful degradation** — a missing CLI reports a warning and leaves the asset unconverted
  rather than failing the run.
- `GlbBinary` / `ProcessTools` are shared engine-neutral helpers (the GLB container read/write was
  duplicated across both Unity tools).

Trigger note: conversion is **menu-driven** for now; auto-running on filesystem import is a Phase 7
automation concern.

## Project settings & layers (Phase 7)

`data/ProjectSettings.json` holds the physics collision matrix + render settings.

- **Collision matrix — layer policy (resolves the open question).** Godot's
  `collision_layer`/`collision_mask` are 32-bit (parity with Unity's 32 layers), but Godot has
  **no global layer-vs-layer collision matrix** — collisions are decided per body. The exporter
  therefore emits a **permissive** matrix (each of 32 layers collides with all: `-1`), which is both
  the honest mapping and byte-identical to Unity's default. Visual/render layers differ (Godot
  exposes 20 vs Unity 32); light cull masks are per-light and drop bits ≥ 20.
- **Render settings** are read from Godot `ProjectSettings`: `RenderScale` ←
  `rendering/scaling_3d/scale`; `MsaaSamples` ← `rendering/anti_aliasing/quality/msaa_3d` (enum →
  raw 1/2/4/8, then clamped to 1 or 4 by `ValidateAndNormalize`); `AnisotropicLevel` ← anisotropic filter enum (off → 1, else
  16); specular-AA has no Godot source and keeps the contract defaults. For a default project this
  is byte-identical to the Unity baseline (golden-tested).

## Automation (Phase 7)

The editor plugin connects the `SceneSaved` signal: saving the edited scene **re-exports its scene
data** (entities, materials, navmesh, project settings) automatically. The asset pipeline
(FBX→GLB→KTX2) remains **menu-driven** — auto-running it on filesystem import is still deferred.

## Parity status

Two real Unity exports exist on disk and are pinned **byte-for-byte** by Core golden tests:
`data/scenes/SampleScene.json` (camera + lighting) and `data/ProjectSettings.json` (physics +
render). Everything else (entities, colliders, materials, navmesh binary, prefab identity, asset
pipeline) is validated by Core unit/structural tests + compile-verification against GodotSharp,
since no richer Unity baseline scene exists. A representative Unity scene with rotated entities,
materials, colliders, and a navmesh — exported from `ParadiseUnityEditor` — would let the byte-level
audit close several of the deferred-parity items below at once.

## Deferred (not yet at Unity parity)

Tracked beyond the 8-phase migration: camera/entity **Euler-rotation** RH→LH conversion (only
identity is validated — no rotated baseline yet); **dynamic RigidBody3D** export (agent→kinematic /
else static fallback only); **bone-attachment** parent paths; per-collider **layer/trigger/static**
flags; material **ORM channel packing**, `RenderQueue`, and `TransmissionFactor` (defaulted);
prefab **override granularity** (decision above) and full **component export inside prefab
templates**; **auto-running** the asset pipeline on filesystem import (currently menu-driven). Most
of these would be closed by a richer Unity baseline scene (see Parity status).

## Enums & nulls

Enums serialize **by name** (`StringEnumConverter`). Null properties are **included** in the JSON
(`NullValueHandling.Include`).
