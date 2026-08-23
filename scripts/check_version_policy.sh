#!/usr/bin/env bash
# Version policy for the addon, checkable without a tag — so it runs on every PR, not only at
# publish time.
#
# The addon states its version in two files and TARGETS a contract stated in a third, and the
# policy in docs/publishing.md ties them together: the addon's major.minor tracks the
# Paradise.Export major.minor it is built against. Nothing enforced that, and it drifted: 0.15.0
# shipped against a 0.17.0 contract, and the constant that used to restate the targeted version
# said 0.14.0. That constant is gone — ProjectSetup now reads what the compiler recorded — but
# the package version is still typed by a human, so this guard exists to catch the half that
# derivation cannot.
#
# A version that is wrong here is not a cosmetic mislabel. Consumers compare the installed
# payload's marker against the package version to decide whether to re-materialize, and the
# contract version a game reads is the addon's major.minor. Both are load-bearing.
#
# Every parse below is checked for EMPTINESS before it is compared. A renamed property or a
# reformatted csproj would otherwise make two sides equal by both being the empty string, and
# this script would go green while guarding nothing at all.
set -euo pipefail

cd "$(dirname "$0")/.."

fail=0

require() {
  local label="$1" value="$2" file="$3"
  if [ -z "$value" ]; then
    echo "PARSE FAILED: could not read $label from $file — the format changed; fix this script."
    fail=1
  fi
}

# major.minor, with any prerelease suffix stripped first so 0.18.0-beta.1 reads as 0.18.
minor_of() {
  local base="${1%%-*}"
  echo "${base%.*}"
}

props_file=Paradise.Godot.Editor/AddonVersion.props
cfg_file=Paradise.Godot.Editor/addon/plugin.cfg
addon_csproj=Paradise.Godot.Editor/Paradise.Godot.Editor.csproj
starter_csproj=templates/starter/ParadiseStarter.csproj

props_version="$(sed -n 's|.*<ParadiseGodotAddonPackageVersion>\(.*\)</ParadiseGodotAddonPackageVersion>.*|\1|p' "$props_file")"
cfg_version="$(sed -n 's/^version="\(.*\)"/\1/p' "$cfg_file")"
export_version="$(sed -n 's|.*Include="Paradise\.Export"[^>]*Version="\([^"]*\)".*|\1|p' "$addon_csproj")"
starter_version="$(sed -n 's|.*Include="Paradise\.Godot\.Editor"[^>]*Version="\([^"]*\)".*|\1|p' "$starter_csproj")"

require "ParadiseGodotAddonPackageVersion" "$props_version"  "$props_file"
require "version="                         "$cfg_version"    "$cfg_file"
require "Paradise.Export version"          "$export_version" "$addon_csproj"
require "Paradise.Godot.Editor version"    "$starter_version" "$starter_csproj"
[ "$fail" -eq 0 ] || { echo "Version policy check FAILED."; exit 1; }

echo "AddonVersion.props:      $props_version"
echo "addon/plugin.cfg:        $cfg_version"
echo "targets Paradise.Export: $export_version"
echo "starter template pins:   $starter_version"

# 1. The addon's two self-declarations must agree. publish-addon-package.yml also compares both
#    against the tag, which only exists at publish; this half is checkable now.
[ "$props_version" = "$cfg_version" ] || {
  echo "MISMATCH: AddonVersion.props '$props_version' != addon/plugin.cfg '$cfg_version'"
  fail=1
}

# 2. The policy itself: addon minor tracks the contract minor it targets.
addon_minor="$(minor_of "$props_version")"
export_minor="$(minor_of "$export_version")"
[ "$addon_minor" = "$export_minor" ] || {
  echo "POLICY: addon is $addon_minor.x but targets Paradise.Export $export_minor.x."
  echo "        docs/publishing.md: the addon's minor tracks the contract minor it targets."
  echo "        Either bump the addon to $export_minor.0, or target a $addon_minor.x Paradise.Export."
  fail=1
}

# 3. The starter template hands a new project a version of this addon; pinning one that was never
#    published leaves `dotnet restore` dead on the template's first build. It pinned 0.17.0 — an
#    engine version, never an addon one — until this check existed.
[ "$starter_version" = "$props_version" ] || {
  echo "MISMATCH: $starter_csproj pins Paradise.Godot.Editor '$starter_version', but this addon is '$props_version'."
  fail=1
}

[ "$fail" -eq 0 ] || { echo "Version policy check FAILED."; exit 1; }
echo "Version policy check passed."
