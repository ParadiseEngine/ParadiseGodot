# Migration Plan — ParadiseUnityEditor → ParadiseGodotEditor

> **2026-07-18 update:** the engine-neutral export core described below as a local
> `Paradise.Export/` project now lives in the ParadiseEngine monorepo
> (`src/Paradise.Export`) and is consumed here as the `Paradise.Export` NuGet package,
> so any editor host (Godot, Unity, …) can share it. The Godot-bound half
> (`addons/paradise/`) stays in this repo. Path references below are historical.

Migrating the authoring + export toolset from `~/proj/ParadiseUnityEditor` (Unity 6000.3,
~6,700 LOC) to this Godot 4.7 project.

Both projects play the **same role**: an authoring front-end that walks a scene and emits
**engine-neutral data** (`data/*.json` + binaries) consumed by the separate pure-C# Paradise
Engine runtime (`~/proj/ParadiseEngine`). **The export contract is fixed** — only the
"read from the engine" half is rewritten. Success = Godot emits byte-comparable data to Unity.

## Foundational decisions

| Decision | Choice | Rationale |
|---|---|---|
| **Language** | **Godot .NET (C#)** ✅ *confirmed* | Preserves the ~25–30% engine-neutral C# that ports verbatim; keeps DotRecast + Newtonsoft + subprocess orchestration. GDScript would rewrite all of it for no gain. |
| FBX → GLB | **Keep Blender** (`bpy.ops.export_scene.gltf`) | Fidelity on skins/animation/materials. Godot-native `ufbx` rejected. |
| PNG/JPG → KTX2 | **Keep KTX-Software** (`ktx create`, v5 — toktx removed upstream) | Godot cannot encode `.ktx2` (import-only, no `Image.save_ktx2()`). |
| NavMesh binary | **Keep DotRecast** (pure C#) | Writer ports verbatim; runtime expects DotRecast `MeshSet` format. |
| JSON | **Keep Newtonsoft** (NuGet) | `ExportJsonWriter` + `System.Numerics` converter port verbatim. |
| Convention | Y-up, **right-handed**, meters, column-major matrices | Godot is natively right-handed (Unity was left-handed) — see Risk #1. |

## Target architecture (preserve the Unity Runtime/Editor seam)

Two assemblies, mirroring Unity's engine-neutral / engine-bound split:

```
Paradise.Export/            ← class library, NO Godot reference. Unit-testable standalone.
  Data/LevelDocument.cs         ← VERBATIM from Unity Runtime/Data
  Data/ParadiseComponentAttribute.cs
  Serialization/                ← ExportJsonWriter, ISceneDocumentWriter, JsonSceneDocumentWriter (verbatim)
  Pipeline/BlenderFbxGlb.cs     ← Blender subprocess + embedded Python + GLB parse (verbatim core)
  Pipeline/KtxCreate.cs         ← `ktx create` subprocess + GLB-JSON rewrite (verbatim core)
  NavMesh/DotRecastWriter.cs    ← DotRecast serialization (verbatim core)
  Paths/SceneExportPaths.cs     ← res:// path mapping (light rewrite)

addons/paradise/         ← Godot EditorPlugin (references Core + Godot)
  plugin.cfg, ParadisePlugin.cs ← [Tool] EditorPlugin: menus, signals, automation
  Authoring/EntityExport.cs     ← [Tool] Node3D — the EntityAuthoring equivalent
  Export/SceneDataExporter.cs   ← Godot scene walk → LevelData
  Export/MaterialExporter.cs    ← StandardMaterial3D → LevelMaterialData
  Export/ColliderExportUtility.cs
  NavMesh/NavMeshBake.cs        ← NavigationServer3D bake → NavMeshExporter (feeds Core writer)
  Prefab/PrefabExporter.cs, ModelPrefabGenerator.cs
```

`Core` has zero Godot/Unity dependencies → it ports verbatim and is testable in plain `dotnet test`.

## Unity → Godot 4 API map (the rewrite surface)

| Unity | Godot 4 |
|---|---|
| `GameObject`+`Transform` / `MonoBehaviour` | `Node3D` / `[Tool]` script |
| `Camera` (MainCamera tag) | `Camera3D` |
| `Light` | `DirectionalLight3D` / `OmniLight3D` / `SpotLight3D` |
| `RenderSettings`/`QualitySettings`/URP asset | `WorldEnvironment`+`Environment`, `ProjectSettings` |
| `Box/Sphere/CapsuleCollider` | `CollisionShape3D` + `Box/Sphere/CapsuleShape3D` |
| `Rigidbody`, `NavMeshObstacle` | `RigidBody3D/StaticBody3D`, `NavigationObstacle3D` |
| `Material` / `Texture2D` | `StandardMaterial3D` / `Image` (readable natively) |
| Prefab `.prefab` / `PrefabUtility` | `PackedScene` + scene inheritance |
| `AssetDatabase` / `AssetPostprocessor` | `EditorInterface` / `EditorImportPlugin` + `EditorFileSystem` |
| `[MenuItem]`, `EditorSceneManager.sceneSaved` | `EditorPlugin.add_tool_menu_item`, `scene_saved` signal |
| `NavMesh.CalculateTriangulation` | `NavigationServer3D.bake_from_source_geometry_data` → `NavigationMesh` |
| `GlobalObjectId` (identity) | scene-unique node / GUID in node `meta` |

## Per-tool effort

| Tool (Unity) | LOC | Verdict | Notes |
|---|---|---|---|
| `LevelDocument` | 397 | **Verbatim** → Core | |
| `ExportJsonWriter` + writers | 231 | **Verbatim** → Core | Newtonsoft via NuGet |
| DotRecast writer half of `NavMeshExporter` | ~280 | **Verbatim** → Core | |
| Blender core of `FbxGlbExportPostprocessor` | ~450 | **Verbatim** → Core | drop only the Unity hook |
| toktx core of `GlbKtx2TextureProcessor` | ~780 | **Verbatim** → Core | drop only the Unity hook |
| `SceneExportPaths` / `TransformPaths` | 117 | Light rewrite | `res://` / `NodePath` |
| `ColliderExportUtility` | 244 | ~50% | geometry math kept; reads swap; capsule care |
| `SceneDataExporter` | 713 | ~60% | walk + component reads; DTO shaping kept |
| `MaterialExporter` | 346 | ~60% | drop UnityGLTF; `Image` readback |
| `EntityAuthoring` | 332 | ~70% | `[Tool]` node; GUID lifecycle re-homed |
| `NavMeshBake` | 640 | ~75% | Godot native nav baking |
| `SceneExportAutomation` | 366 | ~70% | EditorPlugin signals |
| `PrefabExporter` / `ModelPrefabGenerator` | 976 | ~80–90% | `PackedScene`; override-semantics gap |

## Phased plan

Each phase has a concrete exit criterion. Phases 0–1 de-risk the contract before bulk porting.

### Phase 0 — Enable .NET + scaffold
- Enable the Mono/.NET build; create C# solution; add `Paradise.Export` class library + `addons/paradise` plugin (`plugin.cfg`, `[Tool] EditorPlugin`).
- Add NuGet: `Newtonsoft.Json`, `DotRecast.*` (Core/Detour/Detour.Io).
- **Exit:** plugin loads; a `Paradise/Export` tool-menu item logs; `dotnet build` of Core is green.

### Phase 1 — Vertical slice + golden-test harness ⭐ (de-risk)
- Port `LevelDocument`, `ExportJsonWriter`, `ISceneDocumentWriter`, `JsonSceneDocumentWriter`, `SceneExportPaths` into Core (verbatim / light).
- Minimal `SceneDataExporter`: walk a Godot scene → export `Camera3D` + lights + one trivial entity → `data/scenes/<Scene>.json`.
- **Build the golden harness:** an equivalent scene authored in both engines (or a hand-authored expected JSON); export from Godot; diff against the Unity baseline with float tolerance. Focus: handedness (positions, rotations, matrices, light direction), color space, matrix column-major order.
- **Exit:** Godot JSON matches the Unity baseline for camera + lights within tolerance; coordinate/color conventions pinned and documented.

### Phase 2 — Authoring + colliders + entities
- `EntityExport` `[Tool]` node (Kind, IsAgent, model ref, collider refs as `NodePath`); GUID lifecycle via `_notification(NOTIFICATION_EDITOR_PRE_SAVE)` + uniqueness scan; identity stored in node `meta`.
- `ColliderExportUtility` (Box/Sphere/Capsule + obstacle), scale-folded to root-local.
- Full entity export: transforms, parent/bone path, collider/rigidbody/agent/interactable components.
- **Exit:** an authored entity with colliders round-trips to JSON matching the Unity shape.

### Phase 3 — Materials
- `MaterialExporter`: `StandardMaterial3D`/`ORMMaterial3D` → `LevelMaterialData` (PBR factors, textures, alpha mode from `transparency`); texture readback via `Image.get_data`; write `data/materials/*.json`.
- **Exit:** material JSON matches Unity baseline; texture paths resolve.

### Phase 4 — NavMesh
- `NavMeshBake`: gather collision shapes (excluding agents) → `NavigationMeshSourceGeometryData3D` → `NavigationServer3D.bake_from_source_geometry_data` → `NavigationMesh`.
- `NavMeshExporter`: `NavigationMesh.get_vertices/get_polygons` → **reuse the verbatim DotRecast writer** in Core → `data/scenes/<Scene>.navmesh.bin`; patch `NavMeshFile` in scene JSON.
- **Exit:** navmesh `.bin` loads in the runtime / DotRecast reader; geometry matches within tolerance.

### Phase 5 — Prefabs (hardest)
- `ModelPrefabGenerator`: generate entity `PackedScene`s from GLB models (clean root + model child), preserving entity GUIDs across regenerate.
- `PrefabExporter`: instance metadata + overrides. **Resolve the Unity-prefab-override → Godot-scene-instance-override semantic gap explicitly** (decide which overrides are tracked).
- **Exit:** generated prefab instances export with correct prefab refs + overrides.

### Phase 6 — Asset pipeline (Blender + toktx)
- Move Blender (`FbxGlbExportPostprocessor`) and toktx (`GlbKtx2TextureProcessor`) cores into `Core/Pipeline` (verbatim); env-var tool resolution (`PARADISE_BLENDER_PATH`, `PARADISE_KTX_PATH`).
- Wire triggers via Godot `EditorImportPlugin` / `EditorFileSystem` filesystem signals.
- **Exit:** importing an FBX produces GLB → KTX2 with `KHR_texture_basisu`, matching Unity output.

### Phase 7 — Automation + settings + parity audit
- `EditorPlugin` signals: re-export on `scene_saved`; re-bake/export on asset change; export `data/ProjectSettings.json` (physics collision matrix, render settings).
- Full parity audit: export a representative scene from both engines, diff the entire `data/` tree.
- **Exit:** `data/` trees match within tolerance; documented deltas justified.

## Validation strategy

- **Golden baselines:** commit Unity-exported `data/` for a small fixture scene; CI diffs Godot output against it (float tolerance ~1e-4; matrices/quaternions normalized before compare).
- **Core unit tests:** `dotnet test` on `Paradise.Export` — JSON round-trip, color packing, matrix column-major order, DotRecast writer — no Godot needed.
- **Handedness first:** Phase 1 exists specifically to catch coordinate/handedness mismatches before any bulk port.

## Risks & gotchas

1. ~~**Handedness (#1).**~~ ✅ **RESOLVED — contract is right-handed.** The contract was briefly pinned to Unity left-handed (verbatim Unity output) but has since been flipped to **right-handed** (Y-up, −Z forward — Godot/glTF standard) so the export data, the shared runtime simulation, and the engine all use one coordinate system. The Godot exporter now writes transforms verbatim with **no** handedness conversion. Also found and fixed: Mono's `float.ToString("R")` (G7-then-G9) differs from modern .NET's shortest-round-trip form — `FormatFloat` emulates Mono so float formatting stays byte-identical. Pinned by the `SampleScene` golden test (Unity baseline, Z-mirrored to right-handed). See `CONVENTIONS.md`.
2. **Prefab override semantics.** Unity per-property overrides ≠ Godot scene-instance overrides. The trickiest port (Phase 5).
3. **Capsule conventions.** `CapsuleShape3D` height/radius/axis differ from Unity `CapsuleCollider`.
4. **Color space.** Confirm Godot's linear workflow matches Unity's `CreateLinearColor` output.
5. **Layers/masks.** Godot: 32 physics layers but 20 visual render layers (Unity had 32). Light `cullingMask`/`renderingLayerMask` mapping needs a decision.
6. **`.import` vs `.meta`.** Godot auto-generates `.import` + `ResourceUID`; the Unity "never commit .meta" rule does not apply.

## Open questions

- ~~Confirm **C#/.NET**~~ ✅ confirmed — Phase 0 unblocked.
- Layer/render-mask mapping policy (Risk #5).
- Which prefab overrides must survive export (Risk #2).
- Is a representative Unity baseline scene available to use as the golden fixture?
