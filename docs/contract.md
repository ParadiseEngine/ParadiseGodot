# Data contract reference

The engine-neutral data the addon exports and Paradise Engine runtimes load. The
serialization types live in the [`Paradise.Export`](https://www.nuget.org/packages/Paradise.Export)
package (`Paradise.Export.Data`, `Paradise.Export.Serialization`) — the package version's
**major.minor is the contract version**; the addon warns at load when the referenced package
diverges from the version it targets.

## Coordinate convention

**Right-handed, Y-up, −Z forward, +X right** (the Godot/glTF standard), meters, column-major
matrices. The exporter writes Godot values verbatim — no handedness conversion anywhere; any
consumer must read the data as-is (no Z-mirror).

## Files

### `data/scenes/<Scene>.json` — scene contract (`LevelData`)

- **Environment**: ambient/sky energy, tonemap (mode/exposure/white), SSAO, glow, fog.
- **Lights**: directional/omni/spot with transforms, color, energy, shadows.
- **Entities**: one record per `EntityExport` node —
  - identity: GUID (from `paradise_entity_guid` metadata), name, `Kind`, `ActiveOnLoad`
  - `WorldMatrix` (column-major, world space; primitive size rides in scale)
  - `Renderable.Mesh`: **data-relative reference** to the source GLB (`Models/knight.glb`,
    `primitives/cube.glb`) plus per-slot material overrides
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

### `data/scenes/<Scene>.navmesh.bin`

DotRecast **MeshSet** written by `NavMeshBinaryWriter` (modern format, not the C++ demo
compatibility layout). Read with `DtMeshSetReader.Read(BinaryReader)` — the overload without
`maxVertsPerPoly`. Triangles have +Y normals; bake erosion equals agent radius.

### `data/materials/*.json`

Material descriptions referenced from entity slot overrides: PBR factors, texture references,
alpha mode, and the procedural-material extension (`MaterialKind`, flow/noise parameters,
`ColorA`/`ColorB`, `EmissiveStrength`). Sub-resource (procedural) textures are never
referenced — texture content always comes from the GLB; overrides carry factors only.

### `data/ProjectSettings.json`

Global physics dynamics (min speeds, skin, push strength, gravity Y, static
friction/restitution fallbacks) — the runtime's simulation parameters, edited via
Paradise/Settings….

### Meshes: `data/**/*.glb` + `*.ktx2` sidecars

Standard glTF binary, geometry only where textures were externalized: `images[].uri` points
at KTX2 sidecars (`<glbstem>_<i>.ktx2`) next to the GLB. Engine-side reading:
`Paradise.Assets.Gltf.GltfSceneReader.Read(glb, externalImageResolver)`.

## Versioning

- Contract version = `Paradise.Export` major.minor (currently **0.3**).
- Additive fields: allowed within a minor (consumers default them).
- Breaking changes bump the minor (pre-1.0) and will ship with migration notes; the addon's
  Project Setup pins the version it supports and the plugin warns on mismatch.
