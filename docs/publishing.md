# Publishing runbook

The addon publishes to NuGet from this repo. Engine packages release separately.

| Artifact | Trigger | Workflow |
| --- | --- | --- |
| `Paradise.Godot.Editor` | `addon-vX.Y.Z` tag | `publish-addon-package.yml` |
| Engine `Paradise.*` packages | `v*` tag in the engine repo | Engine `publish-nuget.yml` |

## Package layout and constraints

The package contains the addon assembly in `lib/`, two scripts and `plugin.cfg` in
`addon/`, and installation targets in `build/`. The targets copy the scripts into
`addons/paradise/` because Godot binds scripts by resource path and UID.
A zip of that directory alone cannot replace the package.

The scripts use composition: `AuthoredEntityNode` derives from `Node3D` and forwards
to `AuthoredEntityCore`; `ParadiseExportPlugin` derives from `EditorPlugin` and forwards
to `ExportPluginCore`. Inheriting a Godot type from the package assembly breaks
script registration on editor reload (`godotengine/godot#75352`). CI tests an actual
assembly reload because a cold start does not expose this failure.

Godot creates `.cs.uid` files per consuming project, and that project commits them.
The package must never contain UIDs, and the targets must never modify or delete
existing UIDs: scenes reference them. Package-content and materialization checks
protect this rule.

## Release steps

1. Set the same version in `Paradise.Godot.Editor/AddonVersion.props` and
   `Paradise.Godot.Editor/addon/plugin.cfg`. The addon minor tracks the targeted
   `Paradise.Export` minor. When changing engine versions, update package references
   and `ProjectSetup.SupportedExportVersion` too.
2. Run the checks below and merge with green CI: `test`, `addon-nuget`, and
   `editor-smoke`. These cover unit tests, package installation, and Godot reload.
3. Tag and push the release:

   ```bash
   git tag addon-vX.Y.Z
   git push origin addon-vX.Y.Z
   ```

4. Consumers update their `Paradise.Godot.Editor` reference and build. Review and
   commit the updated `addons/paradise/` payload; existing UIDs stay unchanged.

Publishing fails if the tag, `AddonVersion.props`, and source `plugin.cfg` disagree.

## Local checks

```bash
dotnet build ParadiseGodot.slnx
dotnet test --project Paradise.Godot.Editor.Tests/Paradise.Godot.Editor.Tests.csproj
bash scripts/check_addon_deps.sh
bash Paradise.Godot.Editor/tests/materialize-tests.sh
dotnet pack Paradise.Godot.Editor/Paradise.Godot.Editor.csproj -c Release -o /tmp/nupkg \
  -p:ParadiseUseEngineSource=false
```

Disable the local engine-source override when packing so the package records
published dependency versions. CI has no workspace override.

## Editing installed scripts

The targets preserve installed files while their version marker matches the package.
Edits to `Paradise.Godot.Editor/addon/` therefore do not automatically update this repo's
`addons/paradise/`. Delete the installed file and rebuild to restore it from the
source payload. Keep its `.uid` file.

## New and migrating projects

Add `Paradise.Godot.Editor` to the Godot csproj and build. Commit the installed
`addons/paradise/` files and the UIDs Godot creates on import.

For a vendored addon, remove old C# sources and UIDs except the two script bindings
that must survive: `ParadiseExportPlugin.cs.uid` and
`Authoring/AuthoredEntityNode.cs.uid`. The package replaces the two scripts but never
removes obsolete sources; leaving them can create duplicate types.

## NuGet trusted publishing setup

As the package owner, configure **Account > Trusted Publishing** on nuget.org for
repository `ParadiseEngine/ParadiseGodot`, package `Paradise.Godot.Editor`, and workflow
`publish-addon-package.yml` before publishing. The workflow uses OIDC to obtain a
short-lived API key. Optionally set `NUGET_USER`; it defaults to the repository owner.
