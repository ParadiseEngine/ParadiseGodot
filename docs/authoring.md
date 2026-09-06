# Authoring guide

How scene content in Godot becomes Paradise Engine runtime data. The source of truth is the
asset project — `assets/project.toml`, the `*.prefab` documents and the assets they reference,
each with a `.meta` sidecar — and everything revolves around the `AuthoredEntityNode` script,
which edits those documents, and `paradise assets build`, which produces what a runtime reads.

## The entity node

Attach `addons/paradise/Authoring/AuthoredEntityNode.cs` to a `Node3D`. Only these nodes are
exported — a plain instanced GLB renders in the Godot editor but is **invisible to the runtime**.

Each entity gets a stable GUID in the `paradise_entity_guid` node metadata, minted on first save.
The GUID — not the node path — is the entity's identity across exports and at runtime, where the
tools-only script itself is absent.

**Everything else about the entity is an authored component.** There is no fixed list of
properties on the node: what you can set is whatever the schema declares, and the node has three
responsibilities a schema cannot express —

| Stays code | Why |
| --- | --- |
| GUID minting + uniqueness | a schema cannot generate a value, or check it against its siblings |
| Transform | position/rotation/scale are `Node3D`'s; the exporter reads `GlobalTransform` |
| Baking references to values | a node path means nothing to the runtime |

The scene **root must keep an identity transform** — a nudged root offsets every exported
`WorldMatrix`.

## AuthoredEntityNode — every component, engine and game alike

**`AuthoredEntityNode`** is the only entity node. Pick a component from **Add Component** and its
fields appear, described by a *schema* rather than by code in this addon — untick a component's
`Enabled` to remove it again. The inspector shows only what the entity actually carries; the menu
lists what it could — and that is true of the
engine's own components (`paradise.identity`, `.renderable`, `.collider`, `.rigidbody`, `.agent`,
`.interactable`, `.sprite-animation`, `.particle-emitter`) exactly as much as of a game's.

It replaced `EntityExport`, which hardcoded 41 `[Export]` fields. Adding a component now touches
**nothing in this addon**. Mark a plain record
with `[Authored]` from `Paradise.Authoring`, and a source generator publishes the schema:

```csharp
[Authored("mygame.ledge", DisplayName = "Ice ledge")]
public sealed record LedgeConfig
{
    [Meters, AuthorRange(0.5, 20), AuthorDoc("How far the ledge overhangs.")]
    public float Overhang { get; set; } = 2f;

    [Unit01, AuthorDoc("0 is glass, 1 is grippy.")]
    public float Friction { get; set; } = 0.35f;

    public bool IsTrigger { get; set; }
}
```

The node reads two schemas and merges them, engine first:

| Source | Where it comes from |
|---|---|
| Engine components | compiled into `Paradise.Export`; always present |
| Your components | `.editor/authoring-schema.json`, dumped by your launcher's build (`paradise host build`) |

Values export into the entity's `Components.Custom` as `{ "Id": "mygame.ledge", "Data": { … } }`,
and the runtime reads them straight back into the same record. **Entities that author nothing are
unaffected** — `Custom` is omitted entirely, so existing scenes and their exported data do not
change.

### Declaring how it looks, not coding it

The record also declares its editor visuals, so no per-component gizmo class exists:

- `[AuthorBoxGizmo(x, z, depth)]` — draw a wireframe box sized from three of the record's own
  fields, from `Y = 0` downward.
- `[AuthorNativeShape]` on a nested part — author it by **pointing at a `CollisionShape3D`** and
  editing it with Godot's own handles. The reference is baked to plain numbers at export, since a
  `NodePath` means nothing to the runtime.

Hints are **semantic** (`[Meters]`, `[Radians]`, `[Seconds]`, `[Unit01]`), never Godot's own
vocabulary — the same schema drives the Blender addon and the browser editor, and the moment one
editor's vocabulary enters it, the others inherit it forever. Ranges are advisory: the runtime is
still what decides whether a value is playable.

A game that needs authoring behaviour no schema can express can implement
`ParadiseGodot.Authoring.IAuthoredEntity` on its own node instead; the exporter picks that up
the same way.

### What is no longer discovered for you

The exporter used to walk an entity's children looking for a GLB to render and a `Sprite3D` to
animate. **It no longer does.** Dropping a model under an entity exports nothing until you point
`paradise.renderable`'s `Mesh` at the file, and a sprite needs `paradise.sprite-animation`'s
source picked. The gain is that what an entity exports is visible in the inspector instead of
being inferred from its children; the cost is that you have to say so.

Three things remain behaviour rather than data, because no schema can carry them: the entity's
GUID (minted and kept unique on save), its transform (that is `Node3D`'s, and you still move nodes
in the viewport), and the baking of a reference into values at export.

## Export flow

Saving a scene writes its document back (the plugin's save hook): `assets/scenes/<name>.prefab`,
canonical TOML, merging what the author changed over what the document held. Nothing else is
written by the editor. What a runtime reads is the BUILD — `paradise assets build` (or the play
tree `paradise host play` maintains under `.editor/play/`):

- `scenes/<name>.prefab|json` — the document with every reference baked to a built path
- `materials/*.material` — material documents, texture slots baked to their KTX2
- `models/*.mesh|.skinnedmesh` — mesh blobs cooked from the GLB the document names
- `**/*.ktx2` — textures
- `ProjectSettings.toml|json` — global physics tuning, authored as `assets/ProjectSettings.toml`

## UI assets

NoesisGUI XAML, fonts and images are **authored** under `ui/` and committed there, and that is
where every consumer reads them — Godot play mode (`EcsSceneBridge.UiXaml`) and the standalone
runtime (`--ui ui/<overlay>.xaml`) both load the file straight off disk, so an edit applies on
the next run and nothing is staged. Noesis Studio's design-time sidecars (`*.noesis` and the
hidden `.noesis/` folder) never ship.

## Meshes and textures

- **Source assets live under `assets/`**, and a GLB ships nothing: `paradise assets watch` (or
  `extract`) mints the `.mesh` / `.skinnedmesh` / `.skeleton` / `.anim` documents beside it, and
  a mesh reference names the DOCUMENT. `paradise assets build` cooks the blobs and KTX2 textures
  the runtime reads.
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

Global dynamics (gravity, skin, friction, restitution fallbacks) are the game's
`assets/ProjectSettings.toml`, a config document the build copies beside the scenes and the
runtime reads. Per-entity values (`Body*`) override
per body. Note: authored values replace runtime defaults exactly — when wiring a previously
inert field, author the old constant into existing scenes and re-export in the same change.
