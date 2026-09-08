# ParadiseGodot — Godot editor for Paradise Engine

`Paradise.Godot.Editor` turns a Godot .NET project into an editor for
[Paradise Engine](https://github.com/ParadiseEngine/ParadiseEngine) asset projects.
It opens `*.prefab` documents as scenes, edits the game's `[Authored]` components,
saves changes to `assets/`, and runs the game through the `paradise` CLI.
`paradise assets build` produces the data the runtime loads.

## Install

Requires Godot 4.7+ **.NET build** and .NET SDK 10.0+. Add this to your Godot csproj:

```xml
<PackageReference Include="Paradise.Godot.Editor" Version="0.40.0" />
```

Build once, reload the project, and enable **Paradise Engine Tools** in
Project Settings > Plugins. Run **Project > Tools > Paradise/Project Setup**
to check `assets/project.toml` and exclude engine assets and build output from Godot's scan.

The build installs `plugin.cfg` and two scripts into `addons/paradise/`.
Commit these files and the `.uid` files Godot creates on import: scenes bind scripts
by both path and UID. Package updates replace the scripts while preserving UIDs.
The remaining addon code and its `Paradise.*` dependencies come from NuGet;
an engine checkout or separate `Paradise.Export` reference is unnecessary.

Install the CLI for Play, Watch, and Extract Models:

```bash
dotnet tool install --global Paradise.Cli
```

Start with the [quickstart](docs/quickstart.md) or [starter project](templates/starter).
See [authoring](docs/authoring.md), the [data contract](docs/contract.md), and
[troubleshooting](docs/troubleshooting.md) for details.

## Development

```bash
dotnet build ParadiseGodot.slnx
dotnet test --project Paradise.Godot.Editor.Tests/Paradise.Godot.Editor.Tests.csproj
bash scripts/check_addon_deps.sh
bash Paradise.Godot.Editor/tests/materialize-tests.sh
```

| Path | Purpose |
| --- | --- |
| `Paradise.Godot.Editor/` | Addon assembly; `addon/` holds the installed scripts, `build/` installs them |
| `Paradise.Godot.Editor.Tests/` | Tests for document editing, paths, sidecars, and CLI arguments |
| `addons/paradise/` | This repo's installed scripts |
| `templates/starter/` | Minimal consuming project |
| `docs/` | User guides and release runbook |

This repo's `project.godot` exercises addon installation and editor reload without
game content. Tests cover code that runs outside Godot. Changes involving `Variant`,
inspector hooks, or `GltfDocument` need a headless editor probe; see
[project lessons](.claude/lessons.md).

## Releases

Tag `addon-vX.Y.Z`, matching `Paradise.Godot.Editor/AddonVersion.props` and
`Paradise.Godot.Editor/addon/plugin.cfg`.
The addon minor tracks the engine minor. Follow the [publishing runbook](docs/publishing.md).
