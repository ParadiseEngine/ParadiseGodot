# Data contract reference

The engine-neutral data Paradise Engine runtimes load, as `paradise assets build` writes it from
the asset project this addon edits. The serialization types live in the
[`Paradise.Export`](https://www.nuget.org/packages/Paradise.Export) package (`Paradise.Export.Data`,
`Paradise.Export.Serialization`) — the package version's **major.minor is the contract version**;
the addon warns at load when the referenced package diverges from the version it targets.

Two forms of every document exist and they are the same contract: the AUTHORED form under
`assets/` (canonical TOML, references as `{ guid, path }`) and the BUILT form under `build/` or
`.editor/play/` (TOML or JSON by build profile, every reference baked to the path the build wrote).
A runtime only ever reads the built form.

## Coordinate convention

**Right-handed, Y-up, −Z forward, +X right** (the Godot/glTF standard), meters, column-major
matrices. The exporter writes Godot values verbatim — no handedness conversion anywhere; any
consumer must read the data as-is (no Z-mirror).

## Files

### `scenes/<name>.prefab|json` — scene document (`PrefabData`)

- **Environment**: ambient/sky energy, tonemap (mode/exposure/white), SSAO, glow, fog.
- **Lights**: directional/omni/spot with transforms, color, energy, shadows.
- **Entities**: one record per `AuthoredEntityNode` —
  - identity: GUID (from `paradise_entity_guid` metadata), name, plus whatever
    `paradise.identity` authored (`Kind`, `IsActive`, `Prefab`, …)
  - `meta` and `transform`: identity, name and parent; LOCAL position, rotation and scale
    (world space is composed down the parent chain by the loader)
  - a mesh field (`authoredBy: mesh`): authored as a reference to the `.mesh` or
    `.skinnedmesh` document minted beside the GLB, built to the blob's path; plus per-slot
    material overrides (`MaterialsComponentData.Slots`)
  - colliders: unit shapes + layer index (see [authoring](authoring.md#collision-layers));
    `IsTrigger` for interaction volumes
  - optional components, absent = null: `Agent` (move speed/acceleration), `Rigidbody`
    (mass/damping/restitution/friction), `SpriteAnimation`, `ParticleEmitter`,
    `Interactable`, skeletal animation clip names.

Unknown/absent optional fields deserialize to defaults — additive schema evolution is
non-breaking (the reason the contract version follows the package's major.minor).

#### `Entities[].Components.Custom` — game-defined components

Components the engine does not define, authored with `[Authored]` and carried verbatim:

```json
"Custom": [ { "Id": "mygame.ledge", "Data": { "Overhang": 2.0, "Friction": 0.35, "IsTrigger": false } } ]
```

`Data` is opaque to the engine — the game deserializes it into its own record through its own
source-generated context. **Omitted entirely when an entity authors nothing**, so documents from
projects that use none of this are unchanged.

### `materials/*.material`

Material descriptions referenced from entity slot overrides: PBR factors, texture references,
alpha mode, and the procedural-material extension (`MaterialKind`, flow/noise parameters,
`ColorA`/`ColorB`, `EmissiveStrength`). Authored with texture slots as references; built with
them baked to the KTX2 the build wrote. A GLB's own materials are documents too — `paradise assets
extract` writes them beside it and records them on the GLB's seed prefab, which is how a runtime
learns what each draw slot had before an override.

### `ProjectSettings.toml|json`

Global physics dynamics (min speeds, skin, push strength, gravity Y, static
friction/restitution fallbacks) — the runtime's simulation parameters, authored as
`assets/ProjectSettings.toml`.

### Meshes: `models/*.mesh|.skinnedmesh` + `*.ktx2`

A GLB is interchange and ships nothing. `paradise assets watch` mints a mesh document beside it
(`.mesh`, or `.skinnedmesh` bound to its `.skeleton`), the build cooks the document to a mesh
blob at the same path, and the scene's mesh field names that. Engine-side reading:
`Paradise.Assets.Mesh.MeshBlobFormat.Read`; textures are KTX2 the build encoded from the GLB's
images or a material's texture references.

## Versioning

- Contract version = `Paradise.Export` major.minor (currently **0.3**).
- Additive fields: allowed within a minor (consumers default them).
- Breaking changes bump the minor (pre-1.0) and will ship with migration notes; the addon's
  Project Setup pins the version it supports and the plugin warns on mismatch.
