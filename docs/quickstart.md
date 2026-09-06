# Quickstart — first entity in ten minutes

Goal: a Godot project where saving a scene exports Paradise Engine data, and one button runs
that data in the standalone .NET runtime.

## 1. Prerequisites

- **Godot 4.7+ .NET build** (the standard build cannot load C# addons)
- **.NET SDK 10.0+** (`dotnet --version`)

## 2. Get a project

Easiest: copy [`templates/starter/`](../templates/starter) — already wired, skip to step 4 after
building once.

For an existing Godot .NET project:

1. If your project has no csproj yet: Project > Tools > C# > Create C# solution.
2. Add the addon to it:

   ```xml
   <PackageReference Include="Paradise.Godot.Editor" Version="0.14.0" />
   ```

3. Build once (hammer icon or `dotnet build`). **This is what installs the addon**: the package
   writes its `res://` half into `addons/paradise/` — `plugin.cfg` and the two scripts your
   scenes will bind entities to. Reload the project afterwards.
4. Enable **Paradise Engine Tools** in Project Settings > Plugins.

Commit `addons/paradise/`, including the `.uid` files Godot mints beside the scripts on import.
A scene stores a script binding as a res:// path *and* a uid, so those files are how your scenes
keep hold of their authored entities. Don't hand-edit the scripts either — the package rewrites
them whenever you bump its version.

You do not add a `Paradise.Export` reference. It comes with the addon, at the version the addon
was built against, which is the only version guaranteed to match the contract it writes.

## 3. Project Setup

Run **Project > Tools > Paradise/Project Setup**. It is idempotent and:

- creates the `data/` layout (`scenes/`, `materials/`, `Models/`, `primitives/`, `sprites/`),
- persists the default settings (`paradise/export/data_dir = res://data`),
- warns if your csproj still pins `Paradise.Export` by hand (remove it — see above).

## 4. Install the engine CLI

```bash
dotnet tool install --global Paradise.Cli
```

This provides `paradise`, which the **Play** toolbar button and **Paradise/Extract Models** run
(found on PATH or in `~/.dotnet/tools`; Paradise/Settings… > "paradise CLI" overrides). Name
your game's launcher in `assets/project.toml`:

```toml
[host]
project = "MyGame.Launcher/MyGame.Launcher.csproj"
scene = "scenes/main.prefab"
```

## 5. Author and run an entity

1. In your scene, add a **Node3D**, attach `addons/paradise/Authoring/AuthoredEntityNode.cs`,
   and tick the components it should carry (`paradise.identity` and `paradise.renderable` for
   a plain prop).
2. Give it geometry: tick `paradise.renderable` and point `Mesh` at a GLB under `data/Models/`,
   or leave it to the primitive pipeline.
3. **Save the scene.** The contract is exported automatically:
   `data/scenes/<SceneName>.json` (+ materials, navmesh when present).
4. Press **Play** in the toolbar — `paradise host play` builds the assets into `.editor/play/`,
   brings the launcher up to date and runs it on the open document. **Stop** ends it.

## 6. Optional tooling

- **KTX2 textures**: install [KTX-Software](https://github.com/KhronosGroup/KTX-Software);
  `paradise assets build` finds `ktx` on PATH (or `PARADISE_KTX_PATH`) and cooks every texture
  the runtime reads. Without it the build still runs, textures just stay uncooked.
- **Blender** for FBX → GLB, through the engine's `paradise assets` verbs.

Next: the [authoring guide](authoring.md) for entity kinds, physics bodies, collision layers,
sprites/particles, and navmesh baking.
