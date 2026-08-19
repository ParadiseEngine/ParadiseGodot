#!/usr/bin/env bash
# End-to-end test of build/Paradise.Godot.Editor.targets.
#
# Packs this package into a throwaway local feed, then drives a disposable Godot consumer project
# through every state the materializer can be in: fresh install, up-to-date, a deleted payload
# file, a local edit, a version bump, adoption of a hand-vendored copy, and a non-Godot consumer.
#
# The invariant it exists to defend: the targets must never write, delete or touch a *.cs.uid.
# Godot mints those per repo and scenes reference scripts by uid; rewriting one silently detaches
# every node that uses the script. Several assertions below check exactly that.
#
# Usage:  bash tests/materialize-tests.sh
# Needs:  dotnet SDK, network (or a warm NuGet cache) for GodotSharp.

set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
mkdir -p "$T/feed"
cp -R "$HERE" "$T/pkg"
rm -rf "$T/pkg/tests" "$T/pkg/bin" "$T/pkg/obj"

# Until the addon's 18 implementation files move into this package, the base classes the res://
# shims derive from do not exist yet. Stub them so the payload compiles and the targets can be
# exercised. Once the real sources land, this block no-ops.
if [ ! -f "$T/pkg/Authoring/AuthoredEntityNodeBase.cs" ]; then
  mkdir -p "$T/pkg/Authoring"
  cat > "$T/pkg/ParadiseExportPluginBase.cs" <<'EOF'
#if TOOLS
using Godot;
namespace ParadiseGodot { [Tool] public partial class ParadiseExportPluginBase : EditorPlugin { } }
#endif
EOF
  cat > "$T/pkg/Authoring/AuthoredEntityNodeBase.cs" <<'EOF'
#if TOOLS
using Godot;
namespace ParadiseGodot.Authoring
{
    [Tool] public partial class AuthoredEntityNodeBase : Node3D
    {
        public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList() =>
            new() { new Godot.Collections.Dictionary {
                { "name", "paradise.identity/Enabled" }, { "type", (int)Variant.Type.Bool },
                { "usage", (int)(PropertyUsageFlags.Default | PropertyUsageFlags.Storage) } } };
    }
}
#endif
EOF
fi

# The version under test comes from AddonVersion.props, so a release bump cannot strand the
# hardcoded numbers this file once carried. NEXT is the patch above it, used by the Update row.
V="$(sed -n 's|.*<ParadiseGodotAddonPackageVersion>\(.*\)</ParadiseGodotAddonPackageVersion>.*|\1|p' "$HERE/AddonVersion.props")"
NEXT="$(echo "$V" | awk -F. '{printf "%d.%d.%d", $1, $2, $3 + 1}')"
[ -n "$V" ] || { echo "FAIL  could not read AddonVersion.props"; exit 1; }

# The workspace source-override must not interfere: this packs against published packages.
( cd "$T/pkg" && dotnet pack -c Release -o "$T/feed" -p:ParadiseUseEngineSource=false ) >/dev/null 2>&1 \
  || { echo "FAIL  package did not pack"; exit 1; }
# Never resolve a stale copy of the package under test from the global cache.
rm -rf "$HOME/.nuget/packages/paradise.godot.editor"

FAIL=0
ok(){ if [ "$2" = "1" ]; then echo "PASS  $1"; else echo "FAIL  $1"; FAIL=$((FAIL+1)); fi; }
b(){ x(){ :; }; }

mkconsumer(){ # $1 = dir
  rm -rf "$1"; mkdir -p "$1"
  cat > "$1/project.godot" <<'EOF'
config_version=5
[application]
config/name="Consumer"
config/features=PackedStringArray("4.7", "C#", "Forward Plus")
[dotnet]
project/assembly_name="Consumer"
EOF
  cat > "$1/nuget.config" <<EOF
<configuration><packageSources>
  <add key="local" value="$T/feed" />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
EOF
  cat > "$1/Consumer.csproj" <<EOF
<Project Sdk="Godot.NET.Sdk/4.7.1">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Paradise.Godot.Editor" Version="$2" />
  </ItemGroup>
</Project>
EOF
}

A="$T/consumer"; ADDON="$A/addons/paradise"
UID_FILE="$ADDON/Authoring/AuthoredEntityNode.cs.uid"

echo "===== ROW 1: Install (fresh repo) ====="
mkconsumer "$A" "$V"
OUT=$(cd "$A" && dotnet build 2>&1); RC=$?
echo "$OUT" | grep -q "installing res:// payload $V" && ok "logs install" 1 || ok "logs install" 0
[ "$RC" = "0" ] && ok "first build succeeds" 1 || { ok "first build succeeds" 0; echo "$OUT" | tail -20; }
[ -f "$ADDON/plugin.cfg" ] && ok "plugin.cfg materialized" 1 || ok "plugin.cfg materialized" 0
[ -f "$ADDON/Authoring/AuthoredEntityNode.cs" ] && ok "AuthoredEntityNode.cs materialized" 1 || ok "AuthoredEntityNode.cs materialized" 0
[ "$(cat "$ADDON/.paradise-addon-version" 2>/dev/null)" = "$V" ] && ok "marker written = $V" 1 || ok "marker written = $V" 0
[ -z "$(find "$A" -name '*.cs.uid' -not -path '*/.godot/*' 2>/dev/null)" ] && ok "NO uid written by targets" 1 || ok "NO uid written by targets" 0
DLL=$(find "$A/.godot" -name 'Consumer.dll' | head -1)
strings "$DLL" 2>/dev/null | grep -q AuthoredEntityNode && ok "shim compiled on FIRST build" 1 || ok "shim compiled on FIRST build" 0

echo "===== ROW 2: None (up to date), after Godot mints the uid ====="
printf 'uid://TESTUID12345\n' > "$UID_FILE"
OUT=$(cd "$A" && dotnet build 2>&1)
echo "$OUT" | grep -q "Paradise addon:" && ok "second build stays silent" 0 || ok "second build stays silent" 1
echo "$OUT" | grep -q "CS2002" && ok "no CS2002 duplicate-Compile" 0 || ok "no CS2002 duplicate-Compile" 1
[ "$(cat "$UID_FILE")" = "uid://TESTUID12345" ] && ok "uid untouched" 1 || ok "uid untouched" 0

echo "===== ROW 3: repair a deleted payload file ====="
rm -f "$ADDON/Authoring/AuthoredEntityNode.cs"
OUT=$(cd "$A" && dotnet build 2>&1)
echo "$OUT" | grep -q "restoring deleted payload" && ok "logs repair" 1 || ok "logs repair" 0
[ -f "$ADDON/Authoring/AuthoredEntityNode.cs" ] && ok "file restored" 1 || ok "file restored" 0
[ "$(cat "$UID_FILE")" = "uid://TESTUID12345" ] && ok "uid survives repair" 1 || ok "uid survives repair" 0

echo "===== ROW 4: local edit preserved while version matches ====="
echo "// LOCAL EDIT" >> "$ADDON/Authoring/AuthoredEntityNode.cs"
(cd "$A" && dotnet build >/dev/null 2>&1)
grep -q "LOCAL EDIT" "$ADDON/Authoring/AuthoredEntityNode.cs" && ok "local edit kept at same version" 1 || ok "local edit kept at same version" 0

echo "===== ROW 5: Update (version bump) ====="
(cd "$T/pkg" && dotnet pack -c Release -o "$T/feed" -p:Version="$NEXT" -p:ParadiseUseEngineSource=false >/dev/null 2>&1)
# Portable in-place edit: `sed -i ''` is BSD-only and on GNU sed reads the '' as the script
# and the pattern as a filename, so the bump silently never happens (and only the Linux CI
# notices). Rewrite through a temp file instead.
sed "s/Version=\"$V\"/Version=\"$NEXT\"/" "$A/Consumer.csproj" > "$A/Consumer.csproj.tmp"
mv "$A/Consumer.csproj.tmp" "$A/Consumer.csproj"
OUT=$(cd "$A" && dotnet build 2>&1)
echo "$OUT" | grep -q "updating res:// payload $V -> $NEXT" && ok "logs update" 1 || ok "logs update" 0
grep -q "LOCAL EDIT" "$ADDON/Authoring/AuthoredEntityNode.cs" && ok "package overwrites on bump" 0 || ok "package overwrites on bump" 1
[ "$(cat "$ADDON/.paradise-addon-version")" = "$NEXT" ] && ok "marker bumped" 1 || ok "marker bumped" 0
[ "$(cat "$UID_FILE")" = "uid://TESTUID12345" ] && ok "uid survives version bump" 1 || ok "uid survives version bump" 0

echo "===== ROW 6: Adopt (today's hand-vendored state) ====="
B="$T/consumer-adopt"; BADDON="$B/addons/paradise"
mkconsumer "$B" "$V"
mkdir -p "$BADDON/Authoring"
echo "// hand-vendored 1130 lines" > "$BADDON/Authoring/AuthoredEntityNode.cs"
printf 'uid://VENDOREDUID99\n' > "$BADDON/Authoring/AuthoredEntityNode.cs.uid"
echo '[plugin]' > "$BADDON/plugin.cfg"
OUT=$(cd "$B" && dotnet build 2>&1)
echo "$OUT" | grep -q "adopting a hand-vendored copy" && ok "logs adopt" 1 || ok "logs adopt" 0
grep -q "hand-vendored" "$BADDON/Authoring/AuthoredEntityNode.cs" && ok "adopt replaces vendored source" 0 || ok "adopt replaces vendored source" 1
[ "$(cat "$BADDON/Authoring/AuthoredEntityNode.cs.uid")" = "uid://VENDOREDUID99" ] && ok "ADOPT PRESERVES EXISTING UID" 1 || ok "ADOPT PRESERVES EXISTING UID" 0
[ "$(cat "$BADDON/.paradise-addon-version")" = "$V" ] && ok "adopt writes marker" 1 || ok "adopt writes marker" 0

echo "===== ROW 7: non-Godot consumer is left alone ====="
C="$T/consumer-lib"; rm -rf "$C"; mkdir -p "$C"
cat > "$C/nuget.config" <<EOF
<configuration><packageSources>
  <add key="local" value="$T/feed" />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
EOF
cat > "$C/Lib.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Paradise.Godot.Editor" Version="$V" /></ItemGroup>
</Project>
EOF
(cd "$C" && dotnet build >/dev/null 2>&1)
[ -d "$C/addons" ] && ok "no addons/ scribbled into non-Godot project" 0 || ok "no addons/ scribbled into non-Godot project" 1

echo
[ "$FAIL" = "0" ] && echo "ALL PASS" || echo "$FAIL FAILED"
exit $FAIL
