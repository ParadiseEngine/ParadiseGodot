# Publishing runbook (maintainers)

## Release channels

| Artifact | Trigger | Workflow |
| --- | --- | --- |
| `paradise-addon-vX.Y.Z.zip` + `paradise-starter-vX.Y.Z.zip` (GitHub release) | tag `addon-vX.Y.Z` | `addon-release.yml` |
| Godot Asset Library version update | same tag (optional step) | `addon-release.yml` |
| `Paradise.Sample.Runtime` dotnet tool (`paradise-runtime`) on nuget.org | tag `runtime-vX.Y.Z` | `publish-runtime-tool.yml` |
| Engine packages (`Paradise.*`) | `v*` tag **in the engine repo** | engine `publish-nuget.yml` |

## Cutting an addon release

1. Bump `version=` in `addons/paradise/plugin.cfg` (the release workflow fails on mismatch)
   and `ProjectSetup.SupportedExportVersion` if the targeted `Paradise.Export` changed.
   Policy: **addon minor tracks the Paradise.Export/contract minor** it targets.
2. Merge to `main` with green CI (the export smoke is the addon's real gate).
3. `git tag addon-vX.Y.Z && git push origin addon-vX.Y.Z`.

## One-time setup

### NuGet trusted publishing (runtime tool)

`publish-runtime-tool.yml` uses OIDC like the engine repo. On nuget.org (as the package
owner): Account > Trusted Publishing > add a policy for `ParadiseEngine/ParadiseGodot`,
workflow `publish-runtime-tool.yml`. Optionally set the `NUGET_USER` repository variable
(defaults to the repo owner).

### Godot Asset Library

The **initial listing is manual** (one-time):

1. Account on https://godotengine.org/asset-library (GitHub sign-in works).
2. Submit asset: category **Tools**, Godot version 4.7, license **MIT**,
   repository URL `https://github.com/ParadiseEngine/ParadiseGodot`, download = the
   `addon-vX.Y.Z` release zip URL, icon + screenshots, and a description that states the
   **Godot .NET build requirement** and the Project Setup step loudly.
3. Wait for moderation.

After approval, automate updates: set the repository variable `ASSETLIB_ASSET_ID` (the
numeric id from the listing URL) and the secret `ASSETLIB_TOKEN` (asset-library API token) —
the release workflow then posts each new version.

## Version alignment cheat-sheet

| Thing | Version source | Current |
| --- | --- | --- |
| Contract | `Paradise.Export` major.minor | 0.3 |
| Addon | `plugin.cfg` + `SupportedExportVersion` | 0.3.0 |
| Runtime tool | `runtime-v*` tag | 0.3.0 |
| Engine packages | engine `v*` tag | 0.2.0 / 0.3.0 |
