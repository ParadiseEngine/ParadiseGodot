# Paradise Starter

A minimal Godot .NET project pre-wired for [Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine)
authoring. The Paradise addon comes from the `Paradise.Godot.Editor` NuGet package and is already
enabled; `addons/paradise/` is created by the first build, not checked in here.

## Requirements

- Godot 4.7+ **.NET build** (the standard build cannot run C# addons)
- .NET SDK 10.0+
- Optional, for KTX2 texture encoding: [KTX-Software](https://github.com/KhronosGroup/KTX-Software) (`ktx` CLI)

## First run

1. Open the project in Godot (.NET build). Build the C# project once
   (the hammer icon, or `dotnet build`). **The first build is what installs the addon** - it
   writes `addons/paradise/` from the package, so Godot reports the plugin as missing until it
   has run. Reload the project afterwards. Commit `addons/paradise/`, including the `.uid` files
   Godot mints beside the scripts on import.
2. Run **Project > Tools > Paradise/Project Setup** — verifies the `Paradise.Export`
   package reference and the project settings.
3. Install the engine CLI: `dotnet tool install --global Paradise.Cli`
   (provides `paradise`, which the **Play** toolbar button runs through `paradise host play`
   and **Paradise/Extract Models** runs as `paradise assets extract --all`).

## Author your first entity

1. Open `scenes/main.tscn`.
2. Add a `Node3D`, attach the `AuthoredEntityNode` script
   (`addons/paradise/Authoring/AuthoredEntityNode.cs`), then tick your game's components and
   point its mesh field at a `.mesh` document under `assets/` (or the GLB it was extracted from).
3. Save the scene — the document under `assets/scenes/` is written back on every save.
4. Press **Play** in the toolbar to run the open document through `paradise host play`.

See the addon documentation for the authoring guide (entity kinds, collision layers,
KTX2 texture pipeline, navmesh baking).
