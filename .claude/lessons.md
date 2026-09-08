# Project lessons

## CLI and engine versions

- CLI builds inherit workspace `Directory.Build.targets` overrides. If the engine
  checkout differs from the game's package pin, Play, tray Play, and `host build`
  can fail on unrelated APIs or start with `MissingMethodException`. Compare engine
  tags/commits with the pin before diagnosing the addon. Set
  `ParadiseUseEngineSource=false` in **Paradise/Settings… > Build env** to match CI.
- Preserve PATH when launching `paradise`. Prepending `/usr/local/share/dotnet`
  caused a tool installed with `~/.dotnet/dotnet` to exit 0 without output through
  Godot's `OS.Execute`. The CLI locates its SDK; changing PATH can select another host.

## Godot scenes and resources

- Set descendant `Owner` values to the scene root before `PackedScene.Pack`.
  `GltfDocument.GenerateScene` returns unowned nodes; packing them can succeed while
  saving no geometry. See `DocumentLoader.Own` and `ModelMirror.Own`.
- Use `ResourceLoader.CacheMode.Ignore` when reading current disk contents, including
  workfile carryover and probes that verify their own writes. The default cache can
  return a scene from before its last save.
- Godot skips dot-prefixed directories during import. Explicit loads of native `.tscn`
  and `.scn` files under `.editor/` work (verified on 4.7.1); an unimported GLB does not.
  Store model mirrors as scenes.
- `GltfDocument.GenerateScene` in the editor returns `ImporterMeshInstance3D` nodes,
  which do not draw. Convert them to `MeshInstance3D` with `ImporterMesh.GetMesh()`
  while preserving names, transforms, skins, and children.
- GLBs inside the project localize to `res://`; external images then use
  `ResourceLoader` and fail under `.gdignore`. An absolute `base_path` is also
  localized and does not help. Use embedded textures or accept an untextured preview.

## Inspector and tests

- Keep all five `public override` declarations in each `AuthoredEntityNode` shim:
  `_Notification`, `_Ready`, `_GetPropertyList`, `_Get`, and `_Set`.
  Removing property hooks can leave builds and unit tests green while the inspector
  and saves silently stop working. Compare both copies after edits and probe the editor.
  The old `grep -c override` count of six included a comment, not a sixth method.
- An authored field named `Enabled` conflicts with the `<guid>/Enabled` component
  toggle. Get/set dispatch handles the toggle first. Avoid that field name until the
  toggle uses a distinct spelling; changing it also changes stored `.tscn` properties.
- Constructing `Variant` outside Godot crashes the test host (exit 139), even if its
  summary lists all tests as passed. Check the exit code. Test neutral payload/schema
  values in unit tests and keep `Variant` conversion at the Godot boundary.
  In namespace `Paradise.Godot.Editor.Tests`, qualify Godot types with `global::Godot`.
- Exercise Godot-dependent behavior with a temporary `[Tool] EditorPlugin` in
  `addons/probe/`, enabled in `project.godot`. Run:

  ```bash
  godot --headless --editor --path . --quit-after N
  ```

  Print explicit pass/fail results and set the exit code. Remove the probe, fixtures,
  plugin entry, and generated UIDs afterward; inspect other files Godot rewrote.

## Asset pipeline

- Keep `.gdignore` in `assets/`, `build/`, and `.editor/`. Engine `.mesh` and `.material`
  documents collide with Godot resource formats and otherwise cause import errors.
  `ParadiseProject.EnsureGodotIgnores` creates all three on plugin load, including
  derived directories absent in fresh clones.
- Engine 0.40 rejects authored KTX2 files from the old pipeline. Recover pixels with
  `ktx extract --transcode rgba8 --raw --level 0` and encode them as PNG; target names
  are lowercase and `rgba32` is invalid. Remove unreferenced GLB skins if extraction
  rejects an extra rig.

## Build paths

- Use real engine paths for solution and project references. Symlinks anywhere in
  the build path can break `.editorconfig` discovery, restore the SDK's stricter
  analyzer defaults, and make two path spellings race over shared `obj/` files.
  A failing build can delete another checkout's reference outputs.
- Canonicalize temporary probe paths too: on macOS, build through `/private/var`
  rather than its `/var` symlink. Otherwise Godot's generator can emit incorrect
  `ScriptPath` attributes and the editor silently fails to load the probe.
- Local source references require the expected sibling layout. Place worktrees at
  the matching depth or create a real engine worktree where references resolve;
  never use a symlink as the build-path fix. Keep that engine worktree current and
  remove it when no longer needed.
- Add `<Compile Remove="<dir>/**/*.cs" />` to `ParadiseGodot.csproj` for each new
  subproject. Its recursive glob otherwise includes the subproject's code and generated
  `obj/` attributes. Add it to `ParadiseGodot.slnx` and build the solution.
- A local engine symlink inside `res://` needs `.gdignore` at the engine root or
  Godot scans its build output. After removing or renaming a project, delete leftover
  build directories too; `git rm` does not remove untracked `obj/` files.

## Historical engine test lessons

These apply when working on engine simulation tests, now outside this addon:

- `TrySampleInterpolation` pins snapshots until the next call. Sample just before
  asserting or resample while ticking, otherwise the 32-world pool fills and simulation
  backpressure can make movement tests pass without testing the intended behavior.
- A synchronous tick loop can finish before `Task.Run` starts. Wait for fake-service
  calls with a timeout, then keep pumping ticks until queued completions are applied.
- The source-generated `World` alias is per assembly. Tests can use `out var` or
  reflection for internal pool manipulation instead of assuming that alias exists.
