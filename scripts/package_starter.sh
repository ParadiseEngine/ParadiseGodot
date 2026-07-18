#!/usr/bin/env bash
# Build the self-contained starter project zip: templates/starter/** with the current
# addon baked into addons/paradise. Usage: scripts/package_starter.sh [output.zip]
set -euo pipefail

cd "$(dirname "$0")/.."
out="${1:-paradise-starter.zip}"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/paradise-starter"
cp -R templates/starter/. "$stage/paradise-starter/"
mkdir -p "$stage/paradise-starter/addons"
cp -R addons/paradise "$stage/paradise-starter/addons/paradise"
cp LICENSE "$stage/paradise-starter/addons/paradise/LICENSE"
find "$stage" -name '.DS_Store' -delete

(cd "$stage" && zip -qr starter.zip paradise-starter)
mv "$stage/starter.zip" "$out"
echo "Packaged starter -> $out"
unzip -l "$out" | tail -3
