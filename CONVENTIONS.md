# Export Contract Conventions (pinned)

These are the conventions the Godot export tools **must** reproduce so their output stays
byte-comparable with the original Unity tools (`ParadiseUnityEditor`). Pinned in Phase 1 against
the real Unity baseline `data/scenes/SampleScene.json`, enforced by `ParadiseExport.Core.Tests`.

## Handedness — the contract is Unity left-handed

The export contract stores transforms in **Unity's convention: Y-up, left-handed** (+X right,
+Y up, **+Z forward**). The Unity tools wrote transform values verbatim, with **no** handedness
conversion. (Note: `MIGRATION.md` originally described the contract as "right-handed" — that was
aspirational; the actual on-disk baseline is left-handed.)

Godot is Y-up, **right-handed** (+X right, +Y up, **−Z** forward). Because the contract is fixed
and the runtime already consumes Unity-convention data, the Godot exporter converts Godot's
right-handed values to the contract's left-handed values at export time, via a **Z-axis mirror**
(`CoordinateConversion`):

| Quantity | Conversion |
|---|---|
| position / direction `(x, y, z)` | `(x, y, −z)` |
| rotation quaternion `(x, y, z, w)` | `(−x, −y, z, w)` |
| transform matrix `M` | `S · M · S`, `S = diag(1, 1, −1)` |

Validation: Godot's default camera authored at `(0, 1, 10)` looking toward −Z maps to the
contract's `(0, 1, −10)`, matching the baseline.

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
dimensions** (`ColliderScaleFold`), matching the Unity tool:

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
persisted in the `.tscn`). `EntityExport` mints it and enforces uniqueness among `EntityExport`
nodes in the edited scene on `NOTIFICATION_EDITOR_PRE_SAVE`.

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
- Entity `Materials` slot lists are filled from the entity's `MeshInstance3D` surfaces; the
  top-level `LevelData.Materials` stays empty (matches the Unity baseline).

## NavMesh (Phase 4)

The scene navmesh is baked from **static collision geometry** (`NavigationServer3D` +
`NavigationMeshSourceGeometryData3D`, `ParsedGeometryType.StaticColliders`) and written as the
runtime's DotRecast **MeshSet** binary to `data/scenes/<Scene>.navmesh.bin`; the document's
`NavMeshFile` records the filename.

- **Agent exclusion** — parsing only static colliders naturally drops moving agents
  (CharacterBody3D / RigidBody3D), the Godot-idiomatic equivalent of Unity's
  `EntityAuthoring.IsAgent` filter.
- **Handedness + winding** — baked vertices are Z-mirrored to the contract's left-handed
  convention (`CoordinateConversion.Position`); since the mirror flips triangle winding, the
  fan-triangulated polygons are emitted **reversed** to keep them oriented as the Unity tool's were.
- **Quantization** — `NavMeshBinaryWriter` (ported verbatim) uses cell size/height 0.1, agent
  height 1.8, radius 0, max climb 0.3, 3 verts/poly; bake cell sizes match. Adjacency is rebuilt
  from shared edges (index pairs, then world-position pairs for seams).

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
- **`ModelPrefabGenerator`** (`Paradise/Generate Model Prefabs`) — generates a clean `EntityExport`
  root with the GLB/glTF instanced as a child. Idempotent: existing prefabs are left untouched,
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
to engine-neutral Core (`ParadiseExport.Core.Pipeline`), with only the trigger changing from Unity's
`AssetPostprocessor` to a Godot menu (`Paradise/Convert Models (FBX→GLB→KTX2)`).

- **`BlenderFbxGlb`** — headless Blender (`--background --factory-startup`, embedded Python,
  `export_yup=True`) converts FBX→GLB. Skips when unchanged: a SHA-256 of the FBX is stored in the
  GLB's `asset.extras`. Resolved from `PARADISE_BLENDER_PATH` / standard installs / PATH.
- **`ToktxKtx2`** — toktx converts the GLB's embedded PNG/JPEG to KTX2 (Basis Universal) and
  rewrites the GLB to reference them via `KHR_texture_basisu`. Per-texture encoding preset is chosen
  from material slot usage (base/emissive → sRGB BasisLZ; metallic-roughness/occlusion → linear
  UASTC; normal → linear UASTC normal-mode), falling back to the image name. Resolved from
  `PARADISE_TOKTX_PATH` / `third_party/tools/KTX-Software` / PATH; macOS sets `DYLD_*` to the
  bundled libs.
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
