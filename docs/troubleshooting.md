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
: Only `AuthoredEntityNode` nodes export. Wrap the model in one (tick `paradise.identity`) with
  `paradise.renderable` ticked and its `Mesh` pointed at the GLB — which must live **under
  `data/`**. Note the exporter no longer discovers a GLB child for you; pick it explicitly.

**Everything is offset in the runtime**
: The scene root has a non-identity transform. Reset it and re-save.

**Textures missing in the runtime, fine in Godot**
: The runtime reads only KTX2. Install [KTX-Software](https://github.com/KhronosGroup/KTX-Software),
  set the path in Paradise/Settings…, then **Paradise/Convert data GLBs → KTX2**. GLBs whose
  textures are external PNGs (shared atlases) are not covered by the sidecar pass.

**"ktx CLI not found" warnings**
: Exports still succeed; texture encoding is skipped. Per OS:
  macOS `brew install ktx` (or the official installer, typically `/usr/local/bin/ktx`);
  Linux: KTX-Software release packages; Windows: the official installer. Then set the path in
  Paradise/Settings….

**Collisions wrong in the runtime, correct in Godot**
: The contract keeps a single layer **index** from the lowest set bit of `collision_layer`.
  Use single-bit masks (bit 1 Floor, bit 2 Obstacle) on the collider's owning body.

## Play .NET

**"No runtime host found"**
: Install the preview tool `dotnet tool install --global Paradise.Sample.Runtime`, or set
  Paradise/Settings… > runtime host to your host executable / `.csproj`. Use a `res://` or
  relative path for a host that lives inside the project — it resolves against the project
  root and is saved to project.godot, so it works on every device that clones the repo.

**Button launches but no window / it dies immediately**
: Output goes to `<tmp>/paradise_play_dotnet.log` (GUI-launched processes have no console).
  First launch after a code change builds first — give it a few seconds. The runtime needs an
  existing export: save the scene first.

**Agent zig-zags or grinds along walls**
: Navmesh bake issues — `AgentRadius` must equal the capsule radius (never 0), and the baked
  `.bin` must be current (re-save the scene).

## Headless / CI

Import before exporting on a fresh checkout — the plugin's tasks need imported resources:

```bash
godot --headless --import --path .
PARADISE_EXPORT_SCENE=res://scenes/main.tscn godot --headless --editor --path .
```

A headless environment doesn't read your editor settings' tool paths — set `PARADISE_KTX_PATH`
explicitly when the run needs KTX2 encoding. Procedural (sub-resource) textures do not
rasterize without a GPU; bake concrete images for anything that must export.
