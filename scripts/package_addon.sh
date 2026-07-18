#!/usr/bin/env bash
# Build the distributable addon zip: addons/paradise/** plus LICENSE, laid out so unzipping
# at a Godot project root installs the addon. Usage: scripts/package_addon.sh [output.zip]
set -euo pipefail

cd "$(dirname "$0")/.."
out="${1:-paradise-addon.zip}"

version="$(sed -n 's/^version="\(.*\)"/\1/p' addons/paradise/plugin.cfg)"
if [ -z "$version" ]; then
  echo "Could not read version from addons/paradise/plugin.cfg" >&2
  exit 1
fi

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/addons"
cp -R addons/paradise "$stage/addons/paradise"
cp LICENSE "$stage/addons/paradise/LICENSE"
# Strip OS clutter. Keep *.uid — Godot 4.4+ resource UIDs ship with addons so script
# identity stays stable across installs.
find "$stage" -name '.DS_Store' -delete

(cd "$stage" && zip -qr addon.zip addons)
mv "$stage/addon.zip" "$out"
echo "Packaged addon v$version -> $out"
unzip -l "$out" | tail -3
