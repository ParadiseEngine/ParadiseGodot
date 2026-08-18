# Publishing runbook (maintainers)

## Release channels

| Artifact | Trigger | Workflow |
| --- | --- | --- |
| `Paradise.Godot.Editor` (the addon) on nuget.org | tag `addon-vX.Y.Z` | `publish-addon-package.yml` |
| `Paradise.Sample.Runtime` dotnet tool (`paradise-runtime`) on nuget.org | tag `runtime-vX.Y.Z` | `publish-runtime-tool.yml` |
| Engine packages (`Paradise.*`) | `v*` tag **in the engine repo** | engine `publish-nuget.yml` |

The addon zip and the Godot Asset Library listing were retired when the addon became a package.
They cannot be brought back as they were: the addon's res:// half is now two shim scripts that
derive from types in `Paradise.Godot.Editor.dll`, so a zip of `addons/paradise/` does not compile
on its own. A standalone zip would have to regenerate the full source, which is exactly the
duplication packaging removed.

## How the addon is laid out

Two halves, and the split is not arbitrary:

- **`Paradise.Godot.Editor/`** — the package project. Everything Godot never names by path, which
  is all but two files. Ships as `lib/`.
- **`addons/paradise/`** — the res:// half: `plugin.cfg` and the two shim scripts. It exists
  because Godot serializes a script binding as a res:// **path plus uid**, so a type that lives
  only in an assembly cannot be attached to a node at all. The package carries copies under
  `addon/` and its `build/` targets place them into every consuming repo.

`.cs.uid` files are **minted per project by the Godot editor and committed by that project**. The
package never ships one and the targets never writes, deletes, or touches one — rewriting a uid
would silently detach every node in every scene that references the script. `check_addon_deps.sh`
and the package-contents gate in `publish-addon-package.yml` both enforce this.

## Cutting an addon release

1. Bump the version in **both** places, to the same value:
   - `Paradise.Godot.Editor/AddonVersion.props` (`ParadiseGodotAddonPackageVersion`) — the package
     version and the marker consumers compare against.
   - `Paradise.Godot.Editor/addon/plugin.cfg` (`version=`) — what Godot displays.

   Also bump `ProjectSetup.SupportedExportVersion` if the targeted `Paradise.Export` changed.
   Policy: **addon minor tracks the `Paradise.Export`/contract minor** it targets.

   `publish-addon-package.yml` refuses to publish if the tag and those two disagree. That guard is
   not cosmetic: a marker that never matches the installed package makes the payload
   re-materialize on every build in every consuming repo.

2. Merge to `main` with green CI. Two jobs are the real gate — `export-smoke` (a real headless
   Godot editor runs the plugin and exports a scene) and `addon-nuget` (packs, checks the package
   actually contains its payload and targets, and runs the materialization tests).

3. `git tag addon-vX.Y.Z && git push origin addon-vX.Y.Z`.

4. Consuming game repos pick it up by bumping their `Paradise.Godot.Editor` PackageReference. The
   next build rewrites their `addons/paradise/` payload and bumps their marker; their `.uid` files
   are left alone. Review that diff like any other.

## Local checks before tagging

```bash
bash scripts/check_addon_deps.sh
bash Paradise.Godot.Editor/tests/materialize-tests.sh
# Packing locally needs the engine-source override OFF, or the package records the local
# source build (0.1.1) instead of the real Paradise.Export version:
dotnet pack Paradise.Godot.Editor/Paradise.Godot.Editor.csproj -c Release -o /tmp/nupkg \
  -p:ParadiseUseEngineSource=false
```

CI never sees that override — `Directory.Build.targets` lives outside every repo — so CI packs
correctly without the flag.

## Onboarding a new consuming repo

```xml
<PackageReference Include="Paradise.Godot.Editor" Version="X.Y.Z" />
```

Build once. The targets writes `addons/paradise/` and Godot mints the `.uid` files on the next
import; commit both. Nothing else is copied by hand.

Migrating a repo that still has the addon **vendored** needs one extra manual step: delete its
`addons/paradise/**/*.cs` and the `.cs.uid` files for all but the two shims. The targets never
deletes anything, so leftover vendored sources would duplicate every type in the package. Keep
`ParadiseExportPlugin.cs.uid` and `Authoring/AuthoredEntityNode.cs.uid` — the scenes reference
those uids.

## One-time setup

### NuGet trusted publishing

Both nuget.org workflows use OIDC, so there is no API key to store. On nuget.org, as the package
owner: **Account > Trusted Publishing**, add a policy per workflow for repository
`ParadiseEngine/ParadiseGodot`:

| Package | Workflow file |
| --- | --- |
| `Paradise.Godot.Editor` | `publish-addon-package.yml` |
| `Paradise.Sample.Runtime` | `publish-runtime-tool.yml` |

`Paradise.Godot.Editor` has never been published, so the first run also claims the package id —
make sure the policy exists before the first `addon-v*` tag, or the push fails on an unowned id.

Optionally set the `NUGET_USER` repository variable (defaults to the repo owner).

## Version alignment cheat-sheet

| Thing | Version source |
| --- | --- |
| Contract | `Paradise.Export` major.minor |
| Addon package | `AddonVersion.props` + `addon/plugin.cfg` + `addon-v*` tag (all three must match) |
| Addon's targeted contract | `ProjectSetup.SupportedExportVersion` |
| Runtime tool | `runtime-v*` tag |
| Engine packages | engine `v*` tag |
