# Quickstart

Set up Godot to edit Paradise asset documents and run the game through its launcher.

## 1. Install the addon

Requires Godot 4.7+ **.NET build** and .NET SDK 10.0+.
Copy the [starter project](../templates/starter) or add this to an existing Godot csproj:

```xml
<PackageReference Include="Paradise.Godot.Editor" Version="0.40.0" />
```

If needed, create the csproj with **Project > Tools > C# > Create C# solution**.
Build once, reload, and enable **Paradise Engine Tools** in Project Settings > Plugins.
The starter already enables the plugin.

The build installs `addons/paradise/`. Commit it, including the `.uid` files Godot
creates on import. Package updates replace the scripts, so keep custom code elsewhere.
`Paradise.Export` comes with the addon; remove any redundant direct reference.

## 2. Set up the asset project

Install the engine CLI:

```bash
dotnet tool install --global Paradise.Cli
```

Use a game project created with `paradise new <name>`, with `assets/project.toml`
beside the Godot project. Source assets, prefab documents, and their `.meta`
sidecars belong under `assets/` and should be committed.

Run **Project > Tools > Paradise/Project Setup**. It checks the manifest and adds
`.gdignore` files to `assets/`, `build/`, and `.editor/`. It is safe to run again.

Configure your launcher in `assets/project.toml`:

```toml
[host]
project = "MyGame.Launcher/MyGame.Launcher.csproj"
scene = "scenes/main.prefab"
```

Run `paradise host build` to build the launcher and write the game's component
schema to `.editor/authoring-schema.json`.

## 3. Edit a document

1. Choose **Paradise/Open Document…** and select a `*.prefab` under `assets/`.
2. Add an `AuthoredEntityNode` for each new entity. Use **Add Component** to select
   components from the game's schema; clear a component's **Enabled** toggle to remove it.
3. For geometry, set the mesh field to a `.mesh` or `.skinnedmesh` document, or its
   source GLB. For a new GLB, run **Paradise/Extract Models** or `paradise assets watch`
   to create the mesh document first.
4. Save the scene to write changes back to the prefab document. Opening an ordinary
   `.tscn` does not link it to an asset document.

By default, opening a document starts **Watch**, which maintains sidecars and rebuilds changed
assets. One watcher runs per project and stops when its owning editor closes.
**Paradise/Convert Project** creates working scenes and reusable model scenes under
`.editor/godot/`; see [working scenes](authoring.md#working-scenes) before deleting them.

## 4. Run the game

Press **Play** in the toolbar. `paradise host play` builds assets into `.editor/play/`,
updates the launcher, and runs the open document. **Stop** ends the process.

The addon reads the engine version from `Directory.Packages.props` or agreeing
`Paradise.*` package references and installs that CLI in `~/.paradise/cli/<version>/`.
Otherwise it uses the installed CLI. **Paradise/Settings… > paradise CLI** overrides
this selection, for example to use a source build.

For texture encoding, install [KTX-Software](https://github.com/KhronosGroup/KTX-Software)
with `paradise tools install ktx`. The asset build finds `ktx` through PATH or
`PARADISE_KTX_PATH`. FBX conversion also needs Blender.

Next: [authoring](authoring.md) and [troubleshooting](troubleshooting.md).
