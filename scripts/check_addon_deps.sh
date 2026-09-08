#!/usr/bin/env bash
# Check both the package and installed scripts so game/runtime dependencies cannot leak in.
# Allowed dependencies: Godot, the BCL, Paradise authoring/assets/export packages, and Zio
# for mounted asset paths. ParadiseGodot types may reference each other.
set -euo pipefail

cd "$(dirname "$0")/.."

sources=()
while IFS= read -r -d '' file; do
  sources+=("$file")
done < <(find Paradise.Godot.Editor addons/paradise \
  \( -name bin -o -name obj \) -prune -o -name '*.cs' -print0)

awk '
  /^using [A-Za-z]/ && !/^using ([A-Za-z0-9_.]+ = )?(System|Godot|Zio|Paradise\.Export|Paradise\.Authoring|Paradise\.Assets\.(Project|Pipeline|Documents)|ParadiseGodot)([.;]|$)/ {
    print "DISALLOWED in " FILENAME ": " $0
    violations++
  }
  END {
    if (violations) {
      print "Addon dependency check FAILED: " violations " disallowed using directive(s)."
      exit 1
    }
    print "Addon dependency check passed."
  }
' "${sources[@]}"
