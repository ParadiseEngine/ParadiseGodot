# ParadiseGodot — Godot as the Paradise Engine editor

This repository is two things:

1. **The Paradise addon** (`addons/paradise/`) — a Godot EditorPlugin that turns any Godot
   .NET project into an authoring editor for [Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine):
   entity authoring, asset pipeline (GLB → KTX2, primitives, model prefabs), and export of the
   engine-neutral data contract (scene JSON, navmesh, materials) that the engine runtime loads.
2. **The flagship sample project** — this Godot project itself, with the `Paradise.Sample.*`
   .NET projects (game simulation, standalone SDL/WebGPU runtime host, UI cores) exercising the
   full authoring → export → run loop.

The engine is consumed as published NuGet packages (`Paradise.*`); nothing here needs the
engine repository checked out.

## Install the addon (your own project)

**Option A — starter project**: download `paradise-starter-*.zip` from
[Releases](../../releases), unzip, open in Godot 4.7+ (.NET build). The addon is baked in.

**Option B — existing project**: download `paradise-addon-*.zip` from [Releases](../../releases),
unzip at your project root (it contains `addons/paradise/`), enable the plugin in
Project Settings > Plugins, then run **Project > Tools > Paradise/Project Setup** — it adds the
`Paradise.Export` package reference to your csproj (an addon zip cannot) and creates the
`data/` layout.

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

# The minimal ImGui integration sample (no exported scene needed)
dotnet run --project Paradise.Sample.Runtime/Paradise.Sample.Runtime.csproj -- --game imgui
```

Open the project in Godot to author: saving a scene auto-exports its contract to `data/`;
the **Play .NET** toolbar button launches the export in the standalone runtime.

### Layout

- `addons/paradise/` — the publishable addon (only depends on Godot + `Paradise.Export`;
  CI enforces this)
- `Paradise.Sample.Pool(.Tests)` / `.Navigation.Detour` — engine-agnostic game simulation
  (Paradise.ECS), shared by the Godot bridge and the runtime host
- `Paradise.Sample.Runtime(.Tests)` — standalone SDL/WebGPU runtime host; also packs as the
  `paradise-runtime` dotnet tool
- `Paradise.Sample.Ui(.Tests)` / `Paradise.Sample.ImGui` — renderer-independent UI cores and
  the minimal ImGui sample
- `runtime/`, `scripts/`(godot), `scenes/` — Godot-side bridges and sample scenes
- `templates/starter/` — the starter project (release zip bakes the addon in)
- `docs/` — user documentation; `docs/publishing.md` is the maintainer release runbook

## Releasing (maintainers)

- Addon + starter zips: tag `addon-vX.Y.Z` (must match `addons/paradise/plugin.cfg`)
- `paradise-runtime` tool: tag `runtime-vX.Y.Z`
- Details, including the one-time Asset Library and NuGet trusted-publishing setup:
  [docs/publishing.md](docs/publishing.md)

See [ROADMAP.md](ROADMAP.md) for where this is heading.
