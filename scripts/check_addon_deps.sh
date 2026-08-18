#!/usr/bin/env bash
# Dependency allowlist for the publishable addon: addons/paradise C# sources may only
# reference Godot, the BCL, Paradise.Export, Paradise.Authoring, and themselves (ParadiseGodot
# namespace). Anything else (Paradise.Sample.*, Paradise.ECS, ...) means sample/game code leaked
# into the addon and the released zip would not compile in a user's project.
#
# Paradise.Authoring is allowed because Paradise.Export DEPENDS on it, so it is already present in
# any project that can use this addon at all — it adds nothing to what a user must install. It is
# a separate package only so that a game's simulation assembly can carry [Authored] attributes
# without inheriting the export core's DotRecast and Blender/KTX dependencies.
set -euo pipefail

cd "$(dirname "$0")/.."

allowed='^using ([A-Za-z0-9_.]+ = )?(System|Godot|Paradise\.Export|Paradise\.Authoring|ParadiseGodot)([.;]|$)'
violations=0

while IFS= read -r file; do
  while IFS= read -r line; do
    if ! [[ "$line" =~ $allowed ]]; then
      echo "DISALLOWED in $file: $line"
      violations=$((violations + 1))
    fi
  done < <(grep -hE '^using [A-Za-z]' "$file" || true)
done < <(find addons/paradise -name '*.cs')

if [ "$violations" -gt 0 ]; then
  echo "Addon dependency check FAILED: $violations disallowed using directive(s)."
  exit 1
fi
echo "Addon dependency check passed."
