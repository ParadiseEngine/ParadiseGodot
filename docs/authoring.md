# Authoring guide

How scene content in Godot becomes Paradise Engine runtime data. Everything revolves around
the `EntityExport` node script and the `data/` directory (configurable via
`paradise/export/data_dir`; `res://data` by convention).

## EntityExport

Attach `addons/paradise/Authoring/EntityExport.cs` to a `Node3D`. Only `EntityExport` nodes
are exported — a plain instanced GLB renders in the Godot editor but is **invisible to the
runtime**. Wrap decorative models in an `EntityExport` (`Kind = "Prop"`).

Each entity gets a stable GUID in the `paradise_entity_guid` node metadata (created on first
export). The GUID — not the node path — is the entity's identity across exports and at
runtime, where the tools-only script itself is absent.

Key properties:

| Property | Meaning |
| --- | --- |
| `Kind` | Free-form label (`Prop`, `Player`, …) consumed by the runtime's spawn logic |
| `ActiveOnLoad` | Spawn active or dormant |
| `ModelPath` | Source GLB override; otherwise the nearest instanced child's scene file is used. Must resolve **under `data/`** or the entity warns and renders nothing in the runtime |
| `InitialAnimation` / `IdleAnimation` / `WalkAnimation` | Skeletal clip names |
| `IsAgent`, `MoveSpeed`, `Acceleration` | Navmesh-following agent movement |
| `IsDynamicBody`, `BodyMass`, `BodyLinearDamping`, `BodyRestitution`, `BodyFriction` | Rigid-body sphere dynamics (pool balls etc.) |
| `PhysicsColliders` / `InteractionColliders` | Explicit collider node lists; physics vs. trigger split (Area3D exports as trigger) |
| `Sprite*` | Spritesheet flipbook animation (sheet under `data/sprites/`) |
| `Particle*` | Deterministic sim-side particle emitter |

The scene **root must keep an identity transform** — a nudged root offsets every exported
`WorldMatrix`.

## Export flow

Saving a scene auto-exports it (the plugin's save hook); **Paradise/Export Active Scene** does
it on demand. Output per scene:

- `data/scenes/<Scene>.json` — entities (components, world matrices, colliders, materials refs)
- `data/scenes/<Scene>.navmesh.bin` — DotRecast MeshSet, when the scene has a navmesh
- `data/materials/*.json` — material descriptions referenced by slot overrides
- `data/ProjectSettings.json` — global physics tuning (edited via Paradise/Settings…)
- `data/ui/**` — a staged copy of the authored UI tree (see below)

## UI assets

NoesisGUI XAML, fonts and images are **authored** under `res://ui` (configurable via
`paradise/export/ui_source_dir`) and committed there. Every export copies that tree into
`data/ui/`, preserving subfolders and taking `.xaml`, `.ttf`, `.otf`, `.png`, `.jpg` and `.svg`.
Noesis Studio's design-time sidecars (`*.noesis` and the hidden `.noesis/` folder) stay behind.
A project with no UI directory skips the step silently.

The two locations serve different consumers, so point each at the right one:

| Consumer | Reads | Why |
|---|---|---|
| Godot play mode (`EcsSceneBridge.UiXaml`) | `res://ui/…` | Loads the file straight off disk — no export step, edits apply on the next run |
| Standalone runtime (`--ui`) | `data/ui/…` | Ships `data/` only; the staged copy is its whole world |

Staging is additive — it overwrites what it copies but never wipes `data/ui/`, so renaming an
authored file leaves the old staged copy behind until `data/` is regenerated. Staged XAML is
also linted: a `Source="…"` or `FontFamily="folder/#family"` reference that did not stage
raises an export warning (never an error), which catches an asset left outside the UI
directory.

Headless (CI) export:

```bash
PARADISE_EXPORT_SCENE=res://scenes/sample.tscn godot --headless --editor --path .
```

(`PARADISE_GENERATE_PRIMITIVES=1` and `PARADISE_CONVERT_DATA_GLBS=1` run the other pipeline
tasks; tasks run in that order, then Godot quits.)

## Meshes and textures

- **All renderable assets live under `data/`** — the runtime resolves meshes only there.
- **Primitives** (box/sphere/capsule entities without source art) reference the shared unit
  GLBs in `data/primitives/` (**Paradise/Generate Primitive GLBs**); the entity's size is
  carried as transform scale and folded back into collider dimensions at load.
- **Textures are external KTX2 sidecars** (`<glbstem>_<i>.ktx2`) referenced by `images[].uri`,
  read natively by both Godot and the engine's GLB reader. The import hook transcodes any GLB
  (re)imported under `data/` in place (needs the `ktx` CLI); a GLB and its sidecars must
  travel together.
- A GLB whose textures are **external non-KTX2 images** (shared PNG atlases) renders
  untextured in the runtime — the sidecar pass only covers embedded images.
- Material look at runtime comes from the GLB plus per-slot overrides
  (`surface_material_override/N`); keep the `MeshInstance3D` surface count equal to the GLB's
  primitive count.

## Collision layers

The contract stores a **single layer index** (Unity-style), consumed as `BelongsTo = 1 <<
index`. The exporter maps the nearest `CollisionObject3D` ancestor's `collision_layer` mask to
that index via its lowest set bit — use single-bit masks. Convention: bit 1 = Floor, bit 2 =
Obstacle. Character movement casts filter to Obstacle only (a capsule resting on the floor
would otherwise report a permanent contact).

## Navmesh

Bake in-editor (scene save regenerates `<Scene>.navmesh.bin`). Rules that matter:

- **`AgentRadius` must equal the agent capsule radius** (sample: 0.4) — never 0. Erosion at
  bake time is what keeps planned paths clear of walls.
- Both hosts load the same `.bin` (DotRecast MeshSet) — the runtime never reads Godot's
  `NavigationRegion3D` directly.
- Keep the baked file current: it regenerates on save; a stale `.bin` follows old geometry.

## Physics tuning

Global dynamics (gravity, skin, friction, restitution fallbacks) are project settings edited
in **Paradise/Settings…**, saved to `project.godot`, and exported to
`data/ProjectSettings.json` — the runtime reads the JSON. Per-entity values (`Body*`) override
per body. Note: authored values replace runtime defaults exactly — when wiring a previously
inert field, author the old constant into existing scenes and re-export in the same change.
