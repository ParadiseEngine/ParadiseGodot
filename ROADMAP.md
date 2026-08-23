# Project Roadmap

> Last updated: 2026-07-18

## Vision

Make Godot the authoring editor for Paradise Engine: a user installs one Godot addon into
their own Godot .NET project and gets entity authoring, the asset pipeline (GLB → KTX2,
primitives, prefabs), scene/navmesh/material export to the engine-neutral data contract, and
one-click preview in the standalone .NET runtime — without cloning this repository. This repo
stays the flagship sample project and development workbench for the addon.

## Current Status

The engine is fully consumed as published NuGet packages (`Paradise.*` **0.5.2**, unified). Game code
lives under `Paradise.Sample.*` — the sample game core is `Paradise.Sample.Pool` (a pool physics
game), realigned to the immortal-cultivation data-oriented architecture (single-variable components,
owner systems, the `SystemEvents` deferred bus + managed `Emit`, and an MVVM ImGui sample). The
export tooling is a working `EditorPlugin` at `addons/paradise_export/` whose only compile-time
dependency is the `Paradise.Export` package. Next: productize that plugin into a publishable addon.

## Milestones

### Completed

- [x] Engine published to NuGet — all 16 libraries via tag-driven CI
  (engine [#115](https://github.com/ParadiseEngine/ParadiseEngine/pull/115), v0.2.0)
- [x] Consume engine packages; `Paradise.Sample.*` rename; cultivation → minimal ImGui sample
  ([#72](https://github.com/ParadiseEngine/ParadiseGodot/pull/72))
- [x] Export core shared across editors — moved to the engine repo as `Paradise.Export`
  (engine [#117](https://github.com/ParadiseEngine/ParadiseEngine/pull/117), v0.3.0;
  consumed in [#73](https://github.com/ParadiseEngine/ParadiseGodot/pull/73))
- [x] Rename `Paradise.Sample.Game*` → `Paradise.Sample.Pool*`; realign the sample family to the
  immortal-cultivation architecture — engine bump 0.3.0 → 0.5.2, single-variable components, the
  `SystemEvents` bus (pocketing→score reactor + managed reset), and the ImGui sample as MVVM over
  the sim. See CONVENTIONS.md ("Single-variable components", "SystemEvents", "UI — MVVM").

### In Progress — Addon publishing

- [x] **Phase 1 — Harden the addon as a product**
  - [x] Rename `addons/paradise_export/` → `addons/paradise/`; real plugin metadata; version
        0.3.0 (addon minor tracks the engine/data-contract minor it targets)
  - [x] Config via `ParadisePaths` + settings dialog (`paradise/export/data_dir`,
        `paradise/play/runtime_host`); `PARADISE_*` env vars remain headless/CI overrides
  - [x] "Play .NET" launcher resolves the runtime host from settings → repo sample project →
        installed `paradise-runtime` tool (no hardcoded path)
  - [x] ktx pre-flight warning before batch conversions; export degrades gracefully
  - [x] "Project Setup" action: pinned `Paradise.Export` PackageReference into the user's
        csproj, `data/` layout, default settings; load-time version-mismatch warning

- [x] **Phase 2 — Packaging & distribution**
  - [~] Addon zip + self-contained starter zip — **retired**. The addon ships as the
        `Paradise.Godot.Editor` NuGet package instead; its res:// half is two shims over types in
        the package assembly, so a zip of `addons/paradise/` no longer compiles standalone
  - [x] `paradise-runtime` dotnet tool (`Paradise.Sample.Runtime` PackAsTool) + OIDC publish
        workflow (`runtime-v*` tag); verified by local install + headless render
  - [x] Compatibility check in-plugin; statement in README/docs
  - [~] Godot Asset Library listing — **dropped** before submission. The library distributes
        source zips with no dependency resolution, which cannot express "needs
        `Paradise.Godot.Editor` X.Y.Z"

- [x] **Phase 3 — CI/CD**
  - [x] `ci.yml`: solution build + 3 test suites; addon package + dependency allowlist +
        materialization tests; headless Godot 4.7 import + scene-export smoke gated by the
        runtime contract tests
  - [x] `publish-addon-package.yml`: `addon-v*` tag → three-way version check, pack,
        package-contents gate, OIDC publish to nuget.org

- [x] **Phase 4 — Documentation**
  - [x] `README.md`, `docs/quickstart.md`, `docs/authoring.md`, `docs/contract.md`,
        `docs/troubleshooting.md`, `docs/publishing.md`

- [~] **Phase 5 — Post-v1**
  - [x] In-editor contract validator (**Paradise/Validate Export**): missing mesh refs,
        absent KTX2 sidecars via GLB image-uri scan, stale export/navmesh, non-identity root
  - [x] Version policy: contract = `Paradise.Export` major.minor; the addon reads the contract it
        was built against from its own assembly metadata and warns on mismatch (documented in
        `docs/contract.md`)
  - [ ] Play-mode preview framework: generalize the bridges (`EcsSceneBridge`,
        `ImGuiCanvasRenderer`, `NoesisTextureOverlay`) into an optional addon module.
        **Blocked on publishing the UI cores** (`Paradise.Sample.Ui`-equivalent) as packages —
        the addon's dependency allowlist (Godot + `Paradise.Export` only) is deliberate, so
        play-mode needs its own module with its own declared deps
  - [ ] Contract migration tooling once the first breaking data-format change lands
  - [ ] Multi-Godot-version support as 4.x evolves

## Technical Debt

- ~~Engine package versions split across 0.2.0 and 0.3.0; unify~~ — DONE: the whole family is on
  `Paradise.*` 0.5.2 (unified in the Sample.Pool realignment).
- `Paradise.Ui.Noesis` requires a NoesisGUI license; addon/docs must state this where relevant

## Notes

- The C# addon install caveat that shaped Phase 1 is gone: a package DOES reference its own
  dependencies, so "Project Setup" no longer writes a `Paradise.Export` reference into the user's
  csproj (it warns about a leftover hand-pinned one instead). Docs must still state the Godot
  .NET (mono) build requirement loudly.
- Two files of the addon must stay real `res://` scripts and cannot ever move into the package:
  scenes serialize a script binding as path + uid. `Paradise.Godot.Editor`'s `build/` targets
  place them; the uid stays whatever the consuming project minted.
- Monorepo decision: the addon stays in this repo (addon + sample co-evolve; it packs from the
  `Paradise.Godot.Editor/` subfolder). Revisit only if external contributions get painful.
