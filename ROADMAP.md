# Project Roadmap

> Last updated: 2026-07-18

## Vision

Make Godot the authoring editor for Paradise Engine: a user installs one Godot addon into
their own Godot .NET project and gets entity authoring, the asset pipeline (GLB → KTX2,
primitives, prefabs), scene/navmesh/material export to the engine-neutral data contract, and
one-click preview in the standalone .NET runtime — without cloning this repository. This repo
stays the flagship sample project and development workbench for the addon.

## Current Status

The engine is fully consumed as published NuGet packages (`Paradise.*` 0.2.0 + `Paradise.Export`
0.3.0). Game code lives under `Paradise.Sample.*`; the export tooling is a working `EditorPlugin`
at `addons/paradise_export/` whose only compile-time dependency is the `Paradise.Export` package.
Next: productize that plugin into a publishable addon (phases below).

## Milestones

### Completed

- [x] Engine published to NuGet — all 16 libraries via tag-driven CI
  (engine [#115](https://github.com/ParadiseEngine/ParadiseEngine/pull/115), v0.2.0)
- [x] Consume engine packages; `Paradise.Sample.*` rename; cultivation → minimal ImGui sample
  ([#72](https://github.com/ParadiseEngine/ParadiseGodot/pull/72))
- [x] Export core shared across editors — moved to the engine repo as `Paradise.Export`
  (engine [#117](https://github.com/ParadiseEngine/ParadiseEngine/pull/117), v0.3.0;
  consumed in [#73](https://github.com/ParadiseEngine/ParadiseGodot/pull/73))

### In Progress — Addon publishing

- [ ] **Phase 1 — Harden the addon as a product**
  - [ ] Rename `addons/paradise_export/` → `addons/paradise/`; real plugin metadata; version
        0.3.x aligned with the engine packages (addon minor tracks the engine/data-contract
        minor it targets)
  - [ ] Config via Godot `ProjectSettings` (`paradise/...` keys) surfaced in the settings
        dialog; `PARADISE_*` env vars demoted to headless/CI overrides
  - [ ] "Play .NET" launcher resolves the runtime host from settings (no hardcoded
        `Paradise.Sample.Runtime` path)
  - [ ] External-tool validation UX: verify ktx path on enable/before batch ops; export
        degrades gracefully (skip KTX2 + warning) instead of failing mid-pipeline
  - [ ] "Project Setup" action: adds the pinned `Paradise.Export` PackageReference to the
        user's csproj, creates the `data/` layout, writes default settings

- [ ] **Phase 2 — Packaging & distribution**
  - [ ] Release artifact: zip of `addons/paradise/**` only (+ LICENSE, third-party notices)
  - [ ] Channels: GitHub Releases on `addon-v*` tags (canonical) + Godot Asset Library
        (Tools category; .NET requirement stated; updates via asset-library API)
  - [ ] Compatibility statement + in-plugin check (Godot 4.7+ .NET, net10.0, supported
        `Paradise.Export` version range)
  - [ ] Runtime host as a dotnet tool (`paradise-runtime`) so "Play .NET" works in fresh
        projects; published from this repo's `Paradise.Sample.Runtime`
  - [ ] Starter template project (pre-wired csproj, empty `data/`, one exported scene) as a
        second release artifact

- [ ] **Phase 3 — CI/CD (this repo's first CI)**
  - [ ] PR gate: build `ParadiseGodot.slnx`, run all test suites, headless Godot import +
        scene-export smoke validated by the contract tests
  - [ ] Addon packaging job on every PR + dependency allowlist check (addon sources may only
        reference Godot + `Paradise.Export`)
  - [ ] Release workflow: `addon-v*` tag → zip + GitHub release (+ asset-library update)

- [ ] **Phase 4 — Documentation**
  - [ ] Install & 10-minute quickstart (install addon → Project Setup → tag `EntityExport` →
        save → `paradise-runtime` renders it)
  - [ ] Authoring guide: entity metadata/GUIDs, collision-layer contract, primitives vs source
        GLBs, KTX2 sidecar pipeline, navmesh baking
  - [ ] Contract reference: exported JSON/GLB/navmesh formats, right-handed Y-up −Z-forward
        convention, versioning
  - [ ] Troubleshooting: ktx/Blender install per OS, common export warnings

### Planned — Post-v1 (Phase 5)

- [ ] Play-mode preview framework: generalize the bridges (`EcsSceneBridge`,
      `ImGuiCanvasRenderer`, `NoesisTextureOverlay`) into an optional addon module with an
      extension point for the user's own simulation
- [ ] In-editor contract validator (missing mesh refs, absent KTX2 sidecars, stale navmesh,
      non-identity scene root)
- [ ] Contract migration tooling once the first breaking data-format change lands (until then:
      version stamp + compatibility check only)
- [ ] Multi-Godot-version support as 4.x evolves

## Technical Debt

- Engine package versions split across 0.2.0 (original 16) and 0.3.0 (`Paradise.Export`);
  unify to 0.3.0 in one PR when convenient (`Paradise.Export` has no 0.2.0 on nuget.org)
- `Paradise.Ui.Noesis` requires a NoesisGUI license; addon/docs must state this where relevant

## Notes

- C# addon install caveat: dropping an addon zip into a project does NOT edit the user's
  csproj — the Phase 1 "Project Setup" button exists precisely to close that gap, and docs
  must state the Godot .NET (mono) build requirement loudly.
- Monorepo decision: the addon stays in this repo (addon + sample co-evolve; CI zips the
  subfolder). Revisit only if Asset Library mirroring or external contributions get painful.
- Asset Library submission requires a manual one-time account/listing step by the maintainer;
  CI automates subsequent version updates.
