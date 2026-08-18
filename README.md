# ParadiseGodot — Godot as the Paradise Engine editor

This repository is two things:

1. **The Paradise addon** (`Paradise.Godot.Editor`, on nuget.org) — a Godot EditorPlugin that turns any Godot
   .NET project into an authoring editor for [Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine):
   entity authoring, asset pipeline (GLB → KTX2, primitives, model prefabs), and export of the
   engine-neutral data contract (scene JSON, navmesh, materials) that the engine runtime loads.
2. **The flagship sample project** — this Godot project itself, with the `Paradise.Sample.*`
   .NET projects (game simulation, standalone SDL/WebGPU runtime host, UI cores) exercising the
   full authoring → export → run loop.

The engine is consumed as published NuGet packages (`Paradise.*`); nothing here needs the
engine repository checked out.

## Install the addon (your own project)

Add one package reference to your Godot project's csproj:

```xml
<PackageReference Include="Paradise.Godot.Editor" Version="0.13.0" />
```

Build once. That first build installs the addon's `res://` half into `addons/paradise/` —
`plugin.cfg` and two small scripts. Reload the project, enable the plugin in
Project Settings > Plugins, then run **Project > Tools > Paradise/Project Setup** to create the
`data/` layout. `Paradise.Export` arrives with the package at the version the addon was built
against, so you never pin it yourself.

Commit `addons/paradise/`, including the `.uid` files Godot mints beside the scripts on import.
Godot binds a script to a node by res:// path **and** uid, so those files are how your scenes
keep hold of their authored entities.

The rest of the addon is in the package assembly rather than in your repo. Only these two scripts
have to be real files, because a type that lives only in an assembly cannot be attached to a node.

Or start from [`templates/starter/`](templates/starter) — the same wiring, already done.

Requirements: Godot 4.7+ **.NET build**, .NET SDK 10.0+. Optional:
[KTX-Software](https://github.com/KhronosGroup/KTX-Software) (`ktx` CLI) for KTX2 texture
encoding, Blender for FBX conversion, and the preview runtime
(`dotnet tool install --global Paradise.Sample.Runtime` → `paradise-runtime`).

Start with the **[quickstart](docs/quickstart.md)**, then the
[authoring guide](docs/authoring.md), [data contract reference](docs/contract.md), and
[troubleshooting](docs/troubleshooting.md).

## This repo as the sample project

```bash
dotnet build ParadiseGodot.slnx        # everything, including the Godot assembly
dotnet test --project Paradise.Sample.Pool.Tests/Paradise.Sample.Pool.Tests.csproj
dotnet test --project Paradise.Sample.Ui.Tests/Paradise.Sample.Ui.Tests.csproj
dotnet test --project Paradise.Sample.Runtime.Tests/Paradise.Sample.Runtime.Tests.csproj

# Run an exported scene in the standalone runtime host
dotnet run --project Paradise.Sample.Runtime/Paradise.Sample.Runtime.csproj -- \
  --scene data/scenes/sample.json

# The ImGui MVVM samples (no exported scene needed): a sci-fi "Space Odyssey" or the pool demo
dotnet run --project Paradise.Sample.Runtime/Paradise.Sample.Runtime.csproj -- --game odyssey
dotnet run --project Paradise.Sample.Runtime/Paradise.Sample.Runtime.csproj -- --game pool
```

Open the project in Godot to author: saving a scene auto-exports its contract to `data/`;
the **Play .NET** toolbar button launches the export in the standalone runtime.

### Layout

- `Paradise.Godot.Editor/` — the publishable addon, packaged (only depends on Godot +
  `Paradise.Export`; CI enforces this). `addon/` inside it is the res:// payload it installs into
  consuming repos, and `build/` the targets that place it
- `addons/paradise/` — this repo's own installed copy of that payload: `plugin.cfg` and the two
  shim scripts, placed by the same targets every consumer uses
- `Paradise.Sample.Pool(.Tests)` / `.Navigation.Detour` — engine-agnostic game simulation
  (Paradise.ECS), shared by the Godot bridge and the runtime host
- `Paradise.Sample.Runtime(.Tests)` — standalone SDL/WebGPU runtime host; also packs as the
  `paradise-runtime` dotnet tool
- `Paradise.Sample.Odyssey(.Tests)` — engine-agnostic "Space Odyssey" progression sim (Paradise.ECS)
- `Paradise.Sample.Ui(.Tests)` / `Paradise.Sample.ImGui` — renderer-independent UI cores (the pool +
  odyssey MVVM ViewModels/Views) and the shared ImGui sim-thread driver
- `runtime/`, `scripts/`(godot), `scenes/` — Godot-side bridges and sample scenes
- `templates/starter/` — the starter project (references the addon package)
- `docs/` — user documentation; `docs/publishing.md` is the maintainer release runbook

## Releasing (maintainers)

- Addon package: tag `addon-vX.Y.Z` (must match `Paradise.Godot.Editor/AddonVersion.props`
  and `Paradise.Godot.Editor/addon/plugin.cfg` — CI refuses to publish otherwise)
- `paradise-runtime` tool: tag `runtime-vX.Y.Z`
- Details, including the one-time NuGet trusted-publishing setup:
  [docs/publishing.md](docs/publishing.md)

See [ROADMAP.md](ROADMAP.md) for where this is heading.
