# Paradise Starter

A minimal Godot .NET project pre-wired for [Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine)
authoring. The Paradise addon ships in `addons/paradise/` and is already enabled.

## Requirements

- Godot 4.7+ **.NET build** (the standard build cannot run C# addons)
- .NET SDK 10.0+
- Optional, for KTX2 texture encoding: [KTX-Software](https://github.com/KhronosGroup/KTX-Software) (`ktx` CLI)

## First run

1. Open the project in Godot (.NET build). Build the C# project once
   (the hammer icon, or `dotnet build`).
2. Run **Project > Tools > Paradise/Project Setup** — verifies the `Paradise.Export`
   package reference and creates the `data/` layout.
3. Install the preview runtime: `dotnet tool install --global Paradise.Sample.Runtime`
   (provides the `paradise-runtime` command the **Play .NET** toolbar button launches).

## Author your first entity

1. Open `scenes/main.tscn`.
2. Add a `Node3D`, attach the `AuthoredEntityNode` script
   (`addons/paradise/Authoring/AuthoredEntityNode.cs`), then tick `paradise.identity` and
   `paradise.renderable` and point the latter's `Mesh` at a GLB under `data/`.
3. Save the scene — the engine-neutral contract is exported to `data/scenes/main.json`
   automatically on every save.
4. Press **Play .NET** in the toolbar to run the exported scene in the standalone runtime.

See the addon documentation for the authoring guide (entity kinds, collision layers,
KTX2 texture pipeline, navmesh baking).
