# Troubleshooting

## Installation and build

**Missing `Paradise.Export` or an addon installed from a zip**

Install the `Paradise.Godot.Editor` package as shown in the [quickstart](quickstart.md),
then build and reload. Project Setup checks the project; it does not add package references.
For an old vendored addon, follow the [migration steps](publishing.md#new-and-migrating-projects).

**No Paradise menu**

Use Godot's **.NET build**, build the C# project, and enable **Paradise Engine Tools**
in Project Settings > Plugins.

**Package version mismatch**

Remove redundant direct `Paradise.Export` references and align the addon with the
engine minor your game uses. Project Setup reports redundant references without editing them.

**No components in the inspector**

Run `paradise host build` to generate `.editor/authoring-schema.json`.
Check the editor output for schema errors. The addon reads the game's schema only.

## Documents and assets

**Model visible in Godot but missing at runtime**

Use an `AuthoredEntityNode` with an enabled mesh component and a selected `.mesh`,
`.skinnedmesh`, or source GLB under `assets/`. Run **Paradise/Extract Models** or
`paradise assets watch` for a new GLB, then save the open prefab document.

**Saving a scene does not update a prefab**

Open the prefab through **Paradise/Open Document…**. An ordinary `.tscn` has no document
link. If the prefab changed externally, the addon refuses to overwrite it and reports
an error; your workfile still saves. Reopen the document to load the new version.

**Textures missing at runtime**

Run `paradise assets build` or keep Watch running. Texture encoding needs `ktx`,
installed with `paradise tools install ktx` and found through PATH or `PARADISE_KTX_PATH`.
External images can also be missing from Godot previews because `assets/` is ignored;
embedded GLB textures avoid that preview limitation.

**Collision layers differ from Godot**

Use a single-bit mask on the collider's owning body. The contract stores the lowest
set bit's index, not the full mask; see [collision layers](authoring.md#collision-layers).

## Play and Watch

**No `paradise` CLI found**

Run `dotnet tool install --global Paradise.Cli`, or set its path in
**Paradise/Settings… > paradise CLI**. The addon probes `~/.dotnet/tools` because a
GUI-launched editor may not inherit the shell's PATH.

**No `[host] project` declared**

Set `[host] project = "<launcher>.csproj"` in `assets/project.toml`, relative to the
project root. Add `scene = "scenes/<doc>.prefab"` as the default scene for tray Play.

**Play opens no window or exits immediately**

Read `paradise_godot_play.log` in the system temp directory. On macOS this is usually
`$TMPDIR` under `/var/folders/`, not `/tmp`. The log includes asset builds, launcher
builds, and game output. The first run after code changes can take longer; exit 130
means Stop. Watch uses `paradise_godot_watch_<project>_<hash>.log` beside it.

**Build errors reference unexpected engine APIs**

A workspace `Directory.Build.targets` may replace pinned packages with an engine
source checkout at another version. Set `ParadiseUseEngineSource=false` in
**Paradise/Settings… > Build env** to use the game's published package versions.
This setting applies to CLI builds, including Play and Watch.

## Asset checks in CI

Asset builds do not require Godot to import the source tree:

```bash
dotnet tool install --global Paradise.Cli
paradise tools install ktx
paradise assets build
paradise assets verify
paradise assets prefab-check
```

The last two commands check references/sidecars and canonical prefab formatting.
