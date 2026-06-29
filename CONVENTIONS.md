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

## Float formatting — emulate Mono's `"R"`

The Unity tools ran on **Mono**, whose `float.ToString("R")` formats with 7 significant digits and
falls back to 9 only when 7 doesn't round-trip. Modern .NET's `"R"` instead emits the *shortest*
round-trippable string (often 8 digits), e.g. Mono `0.766044438` vs modern `0.76604444`. To keep
output byte-identical, `ExportJsonWriter.FormatFloat` reproduces Mono's **G7-then-G9** behavior.

This applies only to vector/quaternion/matrix/color floats (the custom converter). Scalar float
properties go through Newtonsoft's default formatting (e.g. whole numbers render as `5.0`).

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

## Deferred (not yet at Unity parity)

Tracked for later phases: camera/entity **Euler-rotation** RH→LH conversion (only identity is
validated — no rotated baseline yet); **dynamic RigidBody3D** export (agent→kinematic / else
static fallback only); **bone-attachment** parent paths; per-collider **layer/trigger/static**
flags; material **ORM channel packing**, `RenderQueue`, and `TransmissionFactor` (defaulted);
**texture file conversion** (Phase 6); prefab **override granularity** (decision above) and full
**component export inside prefab templates**.

## Enums & nulls

Enums serialize **by name** (`StringEnumConverter`). Null properties are **included** in the JSON
(`NullValueHandling.Include`).
