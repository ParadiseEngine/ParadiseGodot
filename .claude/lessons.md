# Project Lessons — ParadiseGodotEditor

## Running the CLI from the editor

- **[hits: 1] Every CLI verb the addon runs inherits the workspace's engine-source override, and a
  source checkout on a different version than the game pins breaks all of them at once.** Play,
  the tray's Play and `host build` all shell out to `dotnet`, which walks up to the workspace
  `Directory.Build.targets` and swaps packages for project references. With the engine at v0.42
  and the game pinning 0.41 the launcher build failed on `PbrRenderer`'s constructor — an API the
  game never used — and a partially-built output produced a `MissingMethodException` at startup
  instead. Neither names a version. Check `git -C ParadiseEngine describe --tags` against the
  game's pin before reading the errors as real, and set `ParadiseUseEngineSource=false` in the
  addon's Build env setting to build the way CI does.

## Mirroring and PackedScene

- **[hits: 1] `PackedScene.Pack` writes an EMPTY scene for any node whose `Owner` is not the
  root.** A scene from `GltfDocument.GenerateScene` arrives with its hierarchy unowned, so packing
  it saved a file that loaded back with zero `MeshInstance3D` and no error anywhere — the save
  reported `Ok`. Walk the subtree setting `Owner` before packing (`DocumentLoader.Own` and
  `ModelMirror.Own` both exist for this). Symptom is always the same: a mirror that "worked" and
  draws nothing.

- **[hits: 1] `ResourceLoader.Load` hands back the CACHED resource, not what is on disk.** Reading
  a working file to carry the author's nodes across a rebuild returned the version from before
  their last save, so the carry-over silently preserved an older scene. Pass
  `cacheMode: ResourceLoader.CacheMode.Ignore` wherever the point is to read the FILE. The same
  trap bites a probe that verifies its own write — a false negative that looks exactly like the
  feature being broken.

- **[hits: 1] Godot does not scan dot-prefixed directories, so nothing under `.editor/` is ever
  imported — but an explicit path still loads.** `OpenSceneFromPath` and
  `ResourceLoader.Load` on `res://.editor/godot/…` both work (measured, 4.7.1), because a `.tscn`
  and a `.scn` are native formats needing no import step. A `.glb` there is inert: no `.import`,
  no dock entry, nothing to load. That is why a model mirror saves a SCENE rather than copying
  the GLB.

## Test gotchas

- [hits: 1] **Deleting the `_Get` / `_Set` / `_GetPropertyList` overrides from `AuthoredEntityNode` produces a GREEN build, passing tests, and a node that draws, stores and saves nothing.** The shim is the only place those Godot hooks exist; `AuthoredEntityCore` holds the logic but Godot never calls it directly. Nothing in the unit suite can see the loss — a shim is only ever exercised by a running editor — and the symptom is silent: the inspector shows no components, `Node.Get("<guid>/Enabled")` returns an EMPTY Variant rather than `false` (the tell: `_Get` was never reached), and a save writes an unchanged document. Caused 2026-09-04 by a scripted edit whose replacement boundary (`s.index('    }\n}\n#endif')`) swallowed every member after the one being replaced. Two defences: after any edit to either copy of the shim, `grep -c override` should be **6**; and run the headless probe (`--headless --editor` with a plugin that opens a document and prints `BakedHostValues()`), because that is the only thing that exercises the shim at all.

- [hits: 1] **A component field literally named `Enabled` is UNREACHABLE in the inspector — it collides with the addon's component-toggle property.** `AuthoredEntityCore` names the per-component toggle `<guid>/Enabled` (`EnabledSuffix`), and both `_Get` and `SetAuthored` test `name.EndsWith("/Enabled")` BEFORE looking at the schema's fields — so a schema field called `Enabled` produces the identical property name and the toggle always wins. Reading it returns whether the component is ticked; writing it ticks/unticks the component instead of setting the field. Found 2026-09-03 while probing the document loader with a test schema. **This is not hypothetical: `SceneLightData.Enabled` and the engine's `HostLight.Enabled` both have it.** Fixing it means changing the toggle's spelling to something a C# property name cannot be, which changes stored `.tscn` property names — cheap now that a workfile is a regenerable cache, but its own change. Until then, do not name an authored field `Enabled`.

- [hits: 1] **Constructing a Godot `Variant` in a plain unit test SEGFAULTS the test host (exit 139) — and the run still reports "Passed".** `Variant.From(1.5f)` outside a running Godot process marshals into native code that is not loaded, killing the runner *after* the other tests report. The summary reads `total: 23, failed: 0, succeeded: 23` with a separate `error: 1` line and `Exit code: 139`, so grepping only the total/failed lines hides it completely — always check the exit code, not just the summary. Consequence for design: anything unit-tested must contain **no `Variant`**. Split payload/schema conversion into a pure layer over a neutral value union and keep `Variant.From` in a thin switch at the Godot edge, exercised by a headless `--headless --editor` probe instead. (Namespace trap while probing: inside `namespace Paradise.Godot.Editor.Tests`, `Godot.X` resolves to `Paradise.Godot.X` — write `global::Godot.X`.)

- [hits: 1] **`TrySampleInterpolation` pins the snapshot pair it returns until the NEXT call — a test that samples once mid-run and then keeps calling `TickOnce` will exhaust the 32-world pool and the sim silently stops ticking (backpressure), capping movement.** The old `direct_move_input_slides_the_agent_and_clamps_to_the_navmesh` test only "passed" its ≤-edge assertion because of this freeze, not because of the clamp. In tests: sample right before asserting, or re-sample periodically so old pins release.

- [hits: 1] **Async work the runner dispatches via `Task.Run` may not have STARTED when the synchronously-ticked sim goes idle — tests must spin-wait for the call, not assert immediately.** `Fixture.RunUntilIdle` ticks without sleeping, so a chat's whole 2-day advance can finish in <1ms wall-clock, before the thread pool ever runs the dispatched LLM request; asserting `Calls == 1` (or dequeuing a pending TCS) right after idle flakes to 0/empty. Pattern: the fake service exposes `WaitForCall(n)` / a `CompleteOldest` that polls its pending queue with `Thread.Sleep(1)` up to a timeout, and result-application asserts use a `PumpUntil(runner, condition)` tick loop (the completion re-enters via the command queue, so the test must keep ticking).

- [hits: 1] **The `World` alias is source-generated PER-ASSEMBLY — test projects don't get it.** Writing `Stack<World>` in `Paradise.Sample.Pool.Tests` fails with CS0305 ("`World<TMask, TConfig>` requires 2 type arguments") because the `global using World = …` alias only exists inside assemblies where the Paradise.ECS generator ran (e.g. `Paradise.Sample.Pool`). In tests, receive worlds via `out var` from public APIs; to poke internal pooled state (e.g. starving `SimulationRunner._pool`), drive the collection reflectively (`GetType().GetMethod("Pop"/"Push")`) instead of naming the world type.

## Asset project in the Godot editor (document model)

- [hits: 1] **`assets/` MUST carry a `.gdignore`: the engine's `.mesh` document extension collides with Godot's own binary `Mesh` resource extension.** (Superseded by the three-tree entry under Build / Toolchain.) Without the ignore, the editor's filesystem scan tries to import every `models/*.mesh` document and errors `Unrecognized binary resource file` on each (seen 2026-09-07 with a fixture copied from ShiningPie). The addon never needs Godot to import the source tree anyway — `ModelPreview` loads GLBs off disk through `GltfDocument`.
- [hits: 1] **In an editor, `GltfDocument.GenerateScene` produces `ImporterMeshInstance3D` nodes, which draw nothing.** They are the import pipeline's intermediate form; `ResourceImporterScene` is what normally turns them into `MeshInstance3D`. A preview has to do that itself (`ImporterMesh.GetMesh()`; keep name, transform, skin, children). The first probe counted 0 `MeshInstance3D` in a perfectly good scene.
- [hits: 1] **A GLB under the project directory always localizes to `res://` inside `GltfDocument`, so its EXTERNAL images go through `ResourceLoader` and fail under `.gdignore` — passing an absolute host `base_path` does not change that** (it is localized too). Accept an untextured preview, or embed textures. Tried and measured 2026-09-07; do not re-try the base-path idea.
- [hits: 1] **The only way to exercise the addon's Godot edge is a throwaway `[Tool] EditorPlugin` under `addons/probe/`, enabled in `project.godot`, run with `godot --headless --editor --path . --quit-after N`, printing `PROBE PASS/FAIL` lines and quitting with a code.** Unit tests cannot construct a `Variant` and a green build proves nothing about `_GetPropertyList`/`_Set`. Remove the probe, the fixture and the project.godot line before committing — Godot also scribbles `.uid` files and re-serializes `data/` JSON while it is up.

- [hits: 1] **Do not prepend a dotnet directory to PATH when launching the `paradise` tool from the editor: with `/usr/local/share/dotnet` first, the shim exits 0 with NO output** (measured 2026-09-07 through `OS.Execute` in a headless editor; every other part of the wrapper — `cd`, `export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`, `exec` — was bisected clean). The tool was installed by the user-local `~/.dotnet/dotnet`, and a different install first on PATH changes what the apphost resolves. The CLI locates the SDK itself (PATH, DOTNET_ROOT, installer directories), so hand it the environment untouched; `ParadiseCli.Wrap` is pure and unit-tested for exactly this reason.

- [hits: 2] **Every tree the engine's tooling owns needs a `.gdignore`, not only `assets/`: `build/` and `.editor/` too.** Godot's scan errors `Unrecognized binary resource file` on the engine's `.material` documents in a play tree exactly as it does on `.mesh` in the source tree (seen 2026-09-07 after a release build landed in `build/`). The plugin mints all three at load (`ParadiseProject.EnsureGodotIgnores`); a fresh clone has neither derived tree yet, so minting only in Project Setup is too late.
- [hits: 1] **The workspace's engine-source override tracks engine `main`, which can be ahead of the published packages the repo pins — and then `paradise host build` (the CLI builds the launcher with the override) fails while `dotnet build -p:ParadiseUseEngineSource=false` and CI are green.** 2026-09-07: `main` was two commits past `v0.40.0` and #261 had removed `ColliderShapeData`. Check `git log v<pinned>..origin/main` in the engine before trusting an override build failure as the addon's fault.
- [hits: 1] **A KTX2 the old pipeline wrote is Basis-compressed (`vkFormat: VK_FORMAT_UNDEFINED`, zstd) and the 0.40 build refuses an authored KTX2, so migrating means going BACK to PNG: `ktx extract --transcode rgba8 --raw --level 0` (targets are lowercase; `rgba32` is not one) and a hand-rolled PNG encoder.** A GLB may also carry an exporter leftover the engine refuses — `player.glb` had a second skin no node referenced ("one rig per GLB"); drop skins no node uses before extracting.

## Build / Toolchain

- [hits: 2] **Reference the sibling ParadiseEngine repo by its real relative path (`..\ParadiseEngine\src\...`) in `ParadiseGodot.sln`, NOT through the in-tree `ParadiseEngine` symlink.** Building engine projects through the symlinked path (`ParadiseGodotEditor/ParadiseEngine/...`) double-spells the same physical files, which breaks tooling that relies on canonical directory walking:
  - **`.editorconfig` discovery fails** → the engine's relaxed CA severities (e.g. `CA1062 = none`, `CA1716 = suggestion`) revert to SDK defaults and, with the engine's `TreatWarningsAsErrors=true`, become ~160 hard errors. Same `Paradise.ECS` builds with 0 errors via the real path, 78 via the symlink.
  - **NuGet restore races** on `obj/project.assets.json` ("file already exists") because the project is seen under two paths.
  The symlink is fine for IDE browsing only; the `.sln` and any `ProjectReference` must use `..\ParadiseEngine`.
  Second hit (2026-07-16, git worktree): a symlink ANYWHERE in the build path prefix reproduces the .editorconfig failure — error messages show canonical source paths while the project dir stays symlink-spelled, so the engine's CA severity relaxations don't apply (~88 CA1062-style errors). MSBuild does NOT canonicalize ProjectReference spellings, but DOES canonicalize the entry project path (so building "through" a symlinked repo view fails differently, with MSB3202 on the relative refs).

- [hits: 1] **Building from a Claude worktree (`.claude/worktrees/<name>/`) breaks the `..\..\ParadiseEngine` engine references — fix with a git WORKTREE of the engine repo at `.claude/worktrees/ParadiseEngine`, never a symlink.** The relative `ProjectReference` resolves to `.claude/worktrees/ParadiseEngine`, which doesn't exist. A symlink there fails twice: (1) the engine's `.editorconfig` severity downgrades don't apply through the symlink spelling, and with `AnalysisMode=AllEnabledByDefault` + `CodeAnalysisTreatWarningsAsErrors=true` (src/Paradise.ECS.Common.props) every CA rule the editorconfig normally relaxes comes back as ~88 hard errors; (2) the symlink shares `obj/` with the real checkout, and a FAILING symlink-spelled build deletes the real checkout's `ref/*.dll` outputs (each spelling's csc-input hash differs → permanent incremental miss + mutual clobbering; `dotnet test`'s MSBuild eval doesn't even forward `-p:` workarounds). Fix: `git -C ~/proj/ParadiseEngine worktree add --detach .claude/worktrees/ParadiseEngine main` + `touch .claude/worktrees/ParadiseEngine/.gdignore` — real directory, own obj/, editorconfig applies, no property hacks. Remember to bump/re-create it when the engine main moves, and `git -C ~/proj/ParadiseEngine worktree remove` it when the game worktree is deleted.

- [hits: 1] **A git worktree of this repo MUST sit directly under `/Users/quabug/proj/<name>` (same depth as the main checkout) or every `..\..\ParadiseEngine` ProjectReference resolves into `.claude/worktrees/` and nothing builds.** EnterWorktree creates worktrees at `<repo>/.claude/worktrees/<name>`, which is one level too deep AND symlink workarounds hit the .editorconfig lesson above. Fix: `git worktree unlock <wt>` (the session locks it) → `git worktree move <wt> /Users/quabug/proj/<repo>-<name>` → re-lock → leave a compat symlink at the old path so the running session's cwd keeps working. Engine objs then restore with the same canonical paths as the main checkout (no assets.json flip-flop).

- [hits: 2] **The Godot assembly's recursive `**/*.cs` glob sweeps EVERY sub-directory: any new sub-project at the repo root MUST get a `<Compile Remove="<dir>/**/*.cs" />` in `ParadiseGodot.csproj`.** First hit: the `ParadiseEngine` symlink flattened the whole engine into `ParadiseGodot.dll` (CS0106). Second hit (2026-07-06): adding `Paradise.Sample.Runtime`/`Paradise.Sample.Runtime.Tests` without exclusions made the glob compile their sources AND their `obj/` **generated `AssemblyInfo.cs`/`AssemblyAttributes.cs`** → CS0579 duplicate-attribute errors (misleadingly reported at `.godot/mono/temp/obj/Debug/*.AssemblyInfo.cs`, ParadiseGodot's own copy — the swept duplicates live in the sub-project's `obj/`). The top-level `DefaultItemExcludes` only covers the project's OWN `obj/`, never nested ones. Checklist when creating a sibling C# project: `dotnet sln add` + `Compile Remove` in ParadiseGodot.csproj, then `dotnet build ParadiseGodot.sln` to confirm.

- [hits: 1] **The local-only `ParadiseEngine` symlink at the repo root is INSIDE `res://`, so the
  Godot editor scans the whole engine tree on startup** — once the engine's sample projects have
  build output, the scan spews `Unrecognized binary resource file: res://ParadiseEngine/src/
  Paradise.*.Sample/bin/...` errors and wastes time walking thousands of bin/obj files. Fix: an
  empty `.gdignore` at `~/proj/ParadiseEngine/.gdignore` (Godot skips any directory containing
  one). The symlink is gitignored in the game repo and the `.gdignore` lives in the engine repo
  — if it goes missing (fresh engine clone), the error spam comes back. Also: a rename/deletion
  of a C# project leaves stale `<old-name>/obj` NuGet leftovers that survive `git rm` — delete
  the directory itself, not just tracked files.

