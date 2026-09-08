# Data contract reference

The addon edits documents defined by `Paradise.Assets.Documents` and uses
`Paradise.Export` for authoring and value conversion. The packages referenced in
`Paradise.Godot.Editor.csproj` define the supported contract.

## Authored and built data

| Form | Location | Contents |
| --- | --- | --- |
| Authored | `assets/` | Canonical TOML documents; asset references carry `{ guid, path }` |
| Built | `build/` or `.editor/play/` | Runtime files produced by `paradise assets build` or `paradise host play`; references resolve to built paths |
| Godot workfiles | `.editor/godot/` | Derived scenes used for editing and model previews |

Commit authored data and its `.meta` identity sidecars. The runtime reads built data.
Godot ignores all three trees during its resource scan because engine extensions
such as `.mesh` and `.material` overlap Godot resource extensions.

## Coordinates

Use right-handed coordinates, Y up, −Z forward, +X right, and meters, matching
Godot and glTF. Matrices use the contract's column-major convention.
The addon performs no handedness conversion.

Prefab transforms store local position, quaternion rotation, and scale.
World placement is composed through entity parentage. The editor reads and writes
the separate transform channels to avoid matrix decomposition drift.

## Prefab documents

Each `PrefabDocument` contains objects with component payloads:

- `meta`: object GUID, name, and parent identity.
- `transform`: local position, rotation, and scale.
- Game components: stable component GUID, type name, and data described by the
  game's authoring schema.

There is no fixed engine component list in the addon. The inspector reads
`.editor/authoring-schema.json` from the launcher build. Components missing from
that schema remain in the document even though the inspector cannot display them.

Saving merges edits into the document read from disk. Unchanged values and unknown
payloads survive; externally changed documents are protected from overwrite.
References to Godot nodes are baked into values before writing.

## Asset documents

| Asset | Authored form | Built form |
| --- | --- | --- |
| Mesh | `.mesh` or `.skinnedmesh` document naming a source model | Cooked mesh blob |
| Material | `.material` document with PBR values and texture references | Material with built texture paths |
| Texture | Source image referenced by a material or extracted model | KTX2 texture |
| Project settings | `assets/ProjectSettings.toml` | Runtime settings in the selected build format |

`paradise assets watch` or `paradise assets extract` creates documents beside source
GLBs. A GLB reference selected in the inspector resolves through its sidecar to a
mesh document. The GLB itself is source data, not the runtime mesh.

## Versioning

The addon's minor tracks the `Paradise.Export` minor it targets. At load,
`ProjectSetup.SupportedExportVersion` is compared with the resolved assembly's
major.minor, and a mismatch produces a warning. Keep the addon and game engine
versions aligned; see [publishing](publishing.md) for release checks.
