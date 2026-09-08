# Paradise Starter

A minimal Godot .NET project referencing `Paradise.Godot.Editor` with the plugin
enabled. The first build creates `addons/paradise/`.

## Setup

Requires Godot 4.7+ **.NET build** and .NET SDK 10.0+.

1. Copy this template into your game project, keeping `project.godot` beside
   `assets/project.toml`. Create a game asset project with `paradise new <name>` if needed.
2. Build with `dotnet build` or Godot's hammer button, then reload. Until that build,
   Godot reports the plugin as missing.
3. Commit `addons/paradise/` and the `.uid` files Godot creates on import. The template's
   `.gitignore` ignores UIDs by default; remove that rule or add these files explicitly.
4. Run **Project > Tools > Paradise/Project Setup** to check the manifest and Godot ignores.
5. Install the CLI with `dotnet tool install --global Paradise.Cli`. Configure your
   launcher in the manifest and run `paradise host build` to generate its authoring schema.

## Edit and run

Use **Paradise/Open Document…** to open a `*.prefab` under `assets/`. Add an
`AuthoredEntityNode`, choose components, and select an extracted mesh document for
geometry. Save to update the prefab, then press **Play**.

The included `scenes/main.tscn` is an ordinary Godot scene, not a linked asset document.
See the [quickstart](../../docs/quickstart.md) for the full workflow and optional texture tools.
