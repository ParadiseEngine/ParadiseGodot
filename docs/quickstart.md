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

Your game is an **asset project**: `assets/project.toml` beside `project.godot` (create one with
`paradise new <name>`), source assets under `assets/` — GLBs, textures, `*.material` and
`*.prefab` documents, each with a `.meta` sidecar the tooling mints — and `paradise assets build`
producing what the runtime reads. Godot is the editor over that tree, never its owner.

Run **Project > Tools > Paradise/Project Setup**. It is idempotent and:

- checks the asset project is there and writes a `.gdignore` into `assets/`, `build/` and
  `.editor/` so Godot never scans them (the addon also does this at every load),
- warns if your csproj still pins `Paradise.Export` by hand (remove it — see above).

## 4. Install the engine CLI

```bash
dotnet tool install --global Paradise.Cli
```

This provides `paradise`, which the **Play** and **Watch** toolbar buttons and
**Paradise/Extract Models** run. A global install is the fallback: when your project pins a
version, the addon fetches exactly that one into `~/.paradise/cli/<version>/` and runs it instead,
so two projects on different engine versions never fight over one slot. Paradise/Settings… >
"paradise CLI" overrides everything, for pointing at a source build. Name
your game's launcher in `assets/project.toml`:

```toml
[host]
project = "MyGame.Launcher/MyGame.Launcher.csproj"
scene = "scenes/main.prefab"
```

## 5. Author and run an entity

1. **Paradise/Open Document…** and pick a `*.prefab` under `assets/scenes/`; it opens as a
   scene whose nodes are `AuthoredEntityNode`s. Add one for a new object and tick the components
   it should carry — your game's own `[Authored]` records, read from the schema its launcher build
   dumps to `.editor/authoring-schema.json`.
2. Give it geometry: pick the model's `.mesh` document, or the GLB it was extracted from. Drop a
   GLB under `assets/models/` and `paradise assets watch` (or **Paradise/Extract Models**) mints
   the document beside it.
3. **Save the scene.** Ctrl+S writes the document back to `assets/scenes/<name>.prefab`.
4. Press **Watch** in the toolbar (or just open a document — it starts on its own) to run
   `paradise assets watch` for the project: it mints sidecars, rebuilds the play tree on every
   change, and puts a status icon in the system tray. One watcher per project; it stops when the
   editor closes. The CLI it runs is the one the project PINS — read from
   `Directory.Packages.props` or the csprojs, fetched once into `~/.paradise/cli/<version>/` —
   so a CLI older than the tree can never write documents the runtime cannot read.
5. **Paradise/Convert Project** builds the Godot side of the whole project under
   `.editor/godot/`: a working scene per document, and a Godot scene per model so a GLB is parsed
   once rather than once per entity per open. Derived and gitignored; delete it and it rebuilds.
6. Press **Play** in the toolbar — `paradise host play` builds the assets into `.editor/play/`,
   brings the launcher up to date and runs it on the open document. **Stop** ends it.

## 6. Optional tooling

- **KTX2 textures**: install [KTX-Software](https://github.com/KhronosGroup/KTX-Software);
  `paradise assets build` finds `ktx` on PATH (or `PARADISE_KTX_PATH`) and cooks every texture
  the runtime reads. Without it the build still runs, textures just stay uncooked.
- **Blender** for FBX → GLB, through the engine's `paradise assets` verbs.

Next: the [authoring guide](authoring.md) for entity kinds, physics bodies, collision layers,
sprites/particles, and navmesh baking.
