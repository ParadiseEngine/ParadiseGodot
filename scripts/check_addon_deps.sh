#!/usr/bin/env bash
# Dependency allowlist for the publishable addon: its C# sources may only reference Godot, the
# BCL, Paradise.Export, Paradise.Authoring, and themselves (ParadiseGodot namespace). Anything
# else (Paradise.Sample.*, Paradise.ECS, ...) means sample/game code leaked into the addon and it
# would not compile in a user's project.
#
# Scans BOTH halves of the addon, because it lives in two places now: the bulk in the
# Paradise.Godot.Editor package project, and the two res:// shims under addons/paradise that
# scenes bind to by path. Pointing this at addons/paradise alone - as it did before the sources
# moved into the package - would leave it green while guarding almost nothing.
#
# Paradise.Authoring is allowed because Paradise.Export DEPENDS on it, so it is already present in
# any project that can use this addon at all — it adds nothing to what a user must install. It is
# a separate package only so that a game's simulation assembly can carry [Authored] attributes
# without inheriting the export core's DotRecast and Blender/KTX dependencies.
#
# Paradise.Assets.Project and Zio are allowed as of the move to the document model: assets/ is the
# source of truth, and locating a project (AssetProjectLayout, ProjectMounts) and addressing files
# inside it (UPath) is how every part of the addon now reaches one. Unlike Paradise.Authoring these
# are a REAL addition to a user's closure — Paradise.Assets.Project brings Zio, Tomlyn and
# Microsoft.Extensions.Logging.Abstractions — and that cost is accepted rather than overlooked,
# because the authoring format is TOML read through a mounted file system and no smaller dependency
# expresses it. Zio is listed separately because it arrives transitively and is used by name.
#
# Paradise.Assets.Documents is allowed because it IS the authoring format: PrefabDocument and its
# serializer are what the addon reads and writes now that assets/ is the source of truth. It adds
# nothing to the closure — Paradise.Assets.Pipeline already depends on it.
#
# Paradise.Assets.Pipeline is allowed because it is where Paradise.Export's own Pipeline/ WENT at
# 0.34: KtxCreate split into KtxTool plus GlbTextureWorkflows, BlenderFbxGlb carried across
# unchanged. The addon has always depended on that code; only the package it lives in changed, so
# this widens the spelling rather than the closure.
set -euo pipefail

cd "$(dirname "$0")/.."

allowed='^using ([A-Za-z0-9_.]+ = )?(System|Godot|Zio|Paradise\.Export|Paradise\.Authoring|Paradise\.Assets\.(Project|Pipeline|Documents)|ParadiseGodot)([.;]|$)'
violations=0

while IFS= read -r file; do
  while IFS= read -r line; do
    if ! [[ "$line" =~ $allowed ]]; then
      echo "DISALLOWED in $file: $line"
      violations=$((violations + 1))
    fi
  done < <(grep -hE '^using [A-Za-z]' "$file" || true)
done < <(find Paradise.Godot.Editor addons/paradise -name '*.cs')

if [ "$violations" -gt 0 ]; then
  echo "Addon dependency check FAILED: $violations disallowed using directive(s)."
  exit 1
fi
echo "Addon dependency check passed."
