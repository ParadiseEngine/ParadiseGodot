# Quickstart — first entity in ten minutes

Goal: a Godot project where saving a scene exports Paradise Engine data, and one button runs
that data in the standalone .NET runtime.

## 1. Prerequisites

- **Godot 4.7+ .NET build** (the standard build cannot load C# addons)
- **.NET SDK 10.0+** (`dotnet --version`)

## 2. Get a project

Easiest: download `paradise-starter-*.zip` from the releases page and unzip — skip to step 4.

For an existing Godot .NET project:

1. Download `paradise-addon-*.zip` and unzip at the project root (adds `addons/paradise/`).
2. If your project has no csproj yet: Project > Tools > C# > Create C# solution.
3. Enable **Paradise Engine Tools** in Project Settings > Plugins.

## 3. Project Setup

Run **Project > Tools > Paradise/Project Setup**. It is idempotent and:

- adds the pinned `Paradise.Export` PackageReference to your csproj (the addon's C# sources
  will not compile without it — an addon zip cannot edit your csproj, this closes the gap),
- creates the `data/` layout (`scenes/`, `materials/`, `Models/`, `primitives/`, `sprites/`),
- persists the default settings (`paradise/export/data_dir = res://data`).

Build the project once (hammer icon or `dotnet build`) so the plugin compiles.

## 4. Install the preview runtime

```bash
dotnet tool install --global Paradise.Sample.Runtime
```

This provides `paradise-runtime`, which the **Play .NET** toolbar button auto-detects
(`~/.dotnet/tools`). Alternatively point Paradise/Settings… > "runtime host" at your own host
executable or `.csproj`.

## 5. Author and run an entity

1. In your scene, add a **Node3D**, attach `addons/paradise/Authoring/AuthoredEntityNode.cs`,
   and tick the components it should carry (`paradise.identity` and `paradise.renderable` for
   a plain prop).
2. Give it geometry: tick `paradise.renderable` and point `Mesh` at a GLB under `data/Models/`,
   or leave it to the primitive pipeline.
3. **Save the scene.** The contract is exported automatically:
   `data/scenes/<SceneName>.json` (+ materials, navmesh when present).
4. Press **Play .NET** in the toolbar — the exported scene opens in an SDL window with the
   engine PBR renderer and the sample simulation (WASD + click-to-path when a player/agent
   and navmesh exist).

## 6. Optional tooling

- **KTX2 textures**: install [KTX-Software](https://github.com/KhronosGroup/KTX-Software) and
  set the `ktx` path in **Paradise/Settings…**. Any GLB (re)imported under `data/` then gets
  its textures transcoded to KTX2 sidecars automatically; without it exports still work,
  textures just stay unconverted (the runtime needs KTX2).
- **Blender** for FBX → GLB (**Paradise/Convert Models**).

Next: the [authoring guide](authoring.md) for entity kinds, physics bodies, collision layers,
sprites/particles, and navmesh baking.
