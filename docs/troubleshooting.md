# Troubleshooting

## Install / build

**The addon doesn't compile after installing the zip** (`Paradise.Export` not found)
: An addon zip cannot edit your csproj. Run **Project > Tools > Paradise/Project Setup**
  (adds the package reference), or add `<PackageReference Include="Paradise.Export"
  Version="0.3.0" />` yourself, then rebuild.

**No Paradise menu appears**
: You need the Godot **.NET build** and a built C# project. Check Project Settings > Plugins
  (Paradise Engine Tools enabled) and build once with the hammer icon.

**Plugin warns about a Paradise.Export version mismatch**
: The addon targets a specific contract major.minor. Align the package version (re-run
  Project Setup, which pins the supported one) or update the addon.

## Export

**My model shows in the editor but not in the runtime**
: Only `AuthoredEntityNode`s are in the document. Give the object one with your mesh component
  ticked and its mesh field pointed at the model's `.mesh` document (or the GLB it was extracted
  from) — which must live **under `assets/`**, and must have been extracted: run
  `paradise assets watch` or **Paradise/Extract Models** once for a freshly dropped GLB.

**Everything is offset in the runtime**
: The scene root has a non-identity transform. Reset it and re-save.

**Textures missing in the runtime, fine in Godot**
: The runtime reads only what `paradise assets build` cooked. Run the build (or leave
  `paradise assets watch` running); the `ktx` CLI it needs is found through `PARADISE_KTX_PATH`
  or PATH — `brew install ktx` on macOS, the KTX-Software packages elsewhere.

**Collisions wrong in the runtime, correct in Godot**
: The contract keeps a single layer **index** from the lowest set bit of `collision_layer`.
  Use single-bit masks (bit 1 Floor, bit 2 Obstacle) on the collider's owning body.

## Play

**"No `paradise` CLI found"**
: Play runs `paradise host play`. Install the CLI (`dotnet tool install --global Paradise.Cli`)
  or set its path in Paradise/Settings… > paradise CLI. A GUI-launched editor does not inherit
  your shell's PATH, which is why `~/.dotnet/tools` is probed directly.

**"declares no [host] project"**
: `paradise host play` needs `[host] project = "<launcher>.csproj"` in `assets/project.toml`
  (relative to the project root). Add `scene = "scenes/<doc>.prefab"` for a default the tray
  can play too.

**Button launches but no window / it dies immediately**
: Output goes to `<tmp>/paradise_godot_play.log` (GUI-launched processes have no console).
  The CLI builds the assets into `.editor/play/` and rebuilds the launcher when stale before the
  window appears — the first Play after a code change takes a while. Exit 130 is a Stop, not a
  failure.

**Agent zig-zags or grinds along walls**
: Navmesh bake issues — `AgentRadius` must equal the capsule radius (never 0), and the baked
  `.bin` must be current (re-save the scene).

## Headless / CI

A fresh checkout needs no Godot import before the addon works: the addon reads documents through
the asset project's mounts and never imports `assets/`, `build/` or `.editor/` (it writes a
`.gdignore` into each at load). What CI does need is the build:

```bash
dotnet tool install --global Paradise.Cli
paradise tools install ktx            # KTX-Software, for textures
paradise assets build                 # build/ — what a runtime reads
```

`paradise assets verify` and `paradise assets prefab-check` are the two checks worth running on
every push: the first names a broken reference or a missing sidecar, the second refuses a document
that is not in canonical form.
