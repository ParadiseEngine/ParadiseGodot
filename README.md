# ParadiseGodot — Godot as the Paradise Engine editor

**The Paradise addon** (`Paradise.Godot.Editor`, on nuget.org): a Godot EditorPlugin that turns a
Godot .NET project into an authoring editor for
[Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine). A game is an **asset
project** — `assets/project.toml`, `*.prefab` documents and the assets they reference — and
this addon opens those documents as scenes, edits them against the game's own `[Authored]`
components, saves them back, and runs the game through the engine's `paradise` CLI. Godot is the
editor over that tree, never its owner; `paradise assets build` writes what a runtime reads.

The engine is consumed as published NuGet packages (`Paradise.*`); nothing here needs the
engine repository checked out.

## Install the addon (your own project)

Add one package reference to your Godot project's csproj:

```xml
<PackageReference Include="Paradise.Godot.Editor" Version="0.40.0" />
```

Build once. That first build installs the addon's `res://` half into `addons/paradise/` —
`plugin.cfg` and two small scripts. Reload the project, enable the plugin in
Project Settings > Plugins, then run **Project > Tools > Paradise/Project Setup** to check the
asset project (`assets/project.toml`, from `paradise new`) and keep Godot out of its trees.
`Paradise.Export` arrives with the package at the version the addon was built against, so you
never pin it yourself.

Commit `addons/paradise/`, including the `.uid` files Godot mints beside the scripts on import.
Godot binds a script to a node by res:// path **and** uid, so those files are how your scenes
keep hold of their authored entities.

The rest of the addon is in the package assembly rather than in your repo. Only these two scripts
have to be real files, because a type that lives only in an assembly cannot be attached to a node.

Or start from [`templates/starter/`](templates/starter) — the same wiring, already done.

Requirements: Godot 4.7+ **.NET build**, .NET SDK 10.0+, and the engine CLI the **Play** button
and **Extract Models** run (`dotnet tool install --global Paradise.Cli` → `paradise`). Optional:
[KTX-Software](https://github.com/KhronosGroup/KTX-Software) (`ktx`, used by
`paradise assets build`; `paradise tools install ktx` fetches it).

Start with the **[quickstart](docs/quickstart.md)**, then the
[authoring guide](docs/authoring.md), [data contract reference](docs/contract.md), and
[troubleshooting](docs/troubleshooting.md).

## Working on the addon

```bash
dotnet build ParadiseGodot.slnx        # the addon, its tests, and this repo's Godot assembly
dotnet test --project Paradise.Godot.Editor.Tests/Paradise.Godot.Editor.Tests.csproj
bash scripts/check_addon_deps.sh       # the package's dependency allowlist
bash Paradise.Godot.Editor/tests/materialize-tests.sh   # the res:// payload materializer
```

The repo's own `project.godot` is the smallest consumer of the addon: it authors nothing, and
exists so the payload materializer, the two res:// shims and an editor assembly reload are
exercised here the way every game repo exercises them (CI's `editor-smoke` job). The Godot edge
of the addon — anything touching a `Variant`, `_GetPropertyList`, `GltfDocument` — is only
provable inside an editor; a throwaway `[Tool] EditorPlugin` under `addons/probe/`, enabled in
`project.godot` and run with `godot --headless --editor --path . --quit-after N`, is the way
(see `.claude/lessons.md`).

### Layout

- `Paradise.Godot.Editor/` — the publishable addon, packaged (only depends on Godot and the
  `Paradise.*` packages the allowlist names; CI enforces this). `addon/` inside it is the res://
  payload it installs into consuming repos, and `build/` the targets that place it
- `Paradise.Godot.Editor.Tests/` — the Godot-free half under test: path arithmetic, document
  merge, the edits overlay, payload reading, sidecars, the CLI's argument shapes
- `addons/paradise/` — this repo's own installed copy of that payload: `plugin.cfg` and the two
  res:// scripts, placed by the same targets every consumer uses
- `templates/starter/` — the starter project (references the addon package)
- `docs/` — user documentation; `docs/publishing.md` is the maintainer release runbook

## Releasing (maintainers)

Tag `addon-vX.Y.Z` — it must match `Paradise.Godot.Editor/AddonVersion.props` and
`Paradise.Godot.Editor/addon/plugin.cfg`, or CI refuses to publish. The addon's minor tracks the
engine's. Details, including the one-time NuGet trusted-publishing setup:
[docs/publishing.md](docs/publishing.md).
