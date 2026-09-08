#!/usr/bin/env bash
# Test installation, repair, updates, adoption, and UID preservation in disposable projects.
# Run from the repo root: bash Paradise.Godot.Editor/tests/materialize-tests.sh
# Requires the .NET SDK and NuGet access.
set -euo pipefail

PACKAGE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT
# Keep packages under test out of the user's cache, including older builds of the same version.
export NUGET_PACKAGES="$TEST_ROOT/packages"
mkdir -p "$TEST_ROOT/feed"
cp -R "$PACKAGE_ROOT" "$TEST_ROOT/pkg"
rm -rf "$TEST_ROOT/pkg/tests" "$TEST_ROOT/pkg/bin" "$TEST_ROOT/pkg/obj"

VERSION="$(sed -n 's|.*<ParadiseGodotAddonPackageVersion>\(.*\)</ParadiseGodotAddonPackageVersion>.*|\1|p' "$PACKAGE_ROOT/AddonVersion.props")"
NEXT_VERSION="$(echo "$VERSION" | awk -F. '{printf "%d.%d.%d", $1, $2, $3 + 1}')"

cat > "$TEST_ROOT/nuget.config" <<EOF
<configuration><packageSources><clear />
  <add key="local" value="$TEST_ROOT/feed" />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
EOF

FAILURES=0
check() {
  local description="$1"
  shift
  if "$@"; then
    echo "PASS  $description"
  else
    echo "FAIL  $description"
    FAILURES=$((FAILURES + 1))
  fi
}
not() {
  local status=0
  "$@" || status=$?
  [[ "$status" == 1 ]]
}
contains() { [[ "$OUTPUT" == *"$1"* ]]; }
file_equals() { [[ "$(cat "$1")" == "$2" ]]; }
run() {
  if ! OUTPUT=$("$@" 2>&1); then
    echo "$OUTPUT"
    exit 1
  fi
}
pack() {
  run dotnet pack "$TEST_ROOT/pkg/Paradise.Godot.Editor.csproj" -c Release \
    -o "$TEST_ROOT/feed" -p:Version="$1" -p:ParadiseUseEngineSource=false
}
consumer() {
  local directory="$1" sdk="${3:-Godot.NET.Sdk/4.7.1}"
  mkdir -p "$directory"
  if [[ "$sdk" == Godot.NET.Sdk/* ]]; then
    cat > "$directory/project.godot" <<'EOF'
config_version=5
[application]
config/name="Consumer"
config/features=PackedStringArray("4.7", "C#", "Forward Plus")
[dotnet]
project/assembly_name="Consumer"
EOF
  fi
  cat > "$directory/Consumer.csproj" <<EOF
<Project Sdk="$sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Paradise.Godot.Editor" Version="$2" /></ItemGroup>
</Project>
EOF
}

pack "$VERSION"
GAME="$TEST_ROOT/consumer"
ADDON="$GAME/addons/paradise"
SCRIPT="$ADDON/Authoring/AuthoredEntityNode.cs"
UID_FILE="$SCRIPT.uid"
MARKER="$ADDON/.paradise-addon-version"

echo "===== Install ====="
consumer "$GAME" "$VERSION"
run dotnet build "$GAME/Consumer.csproj"
check "logs install" contains "installing res:// payload $VERSION"
check "plugin.cfg materialized" test -f "$ADDON/plugin.cfg"
check "entity script materialized" test -f "$SCRIPT"
check "marker written" file_equals "$MARKER" "$VERSION"
check "no UID shipped" test -z "$(find "$ADDON" -name '*.uid')"
DLL=$(find "$GAME/.godot" -name 'Consumer.dll' | head -1)
run strings "$DLL"
check "shim compiled on first build" contains "AuthoredEntityNode"

echo "===== Up to date ====="
printf 'uid://TESTUID12345\n' > "$UID_FILE"
run dotnet build "$GAME/Consumer.csproj"
check "no installation log" not contains "Paradise addon:"
check "no duplicate Compile items" not contains "CS2002"
check "UID untouched" file_equals "$UID_FILE" "uid://TESTUID12345"

echo "===== Repair ====="
rm "$SCRIPT"
run dotnet build "$GAME/Consumer.csproj"
check "logs repair" contains "restoring deleted payload"
check "script restored" test -f "$SCRIPT"
check "UID survives repair" file_equals "$UID_FILE" "uid://TESTUID12345"

echo "===== Local edit ====="
echo "// LOCAL EDIT" >> "$SCRIPT"
run dotnet build "$GAME/Consumer.csproj"
check "local edit preserved" grep -Fq "LOCAL EDIT" "$SCRIPT"

echo "===== Update ====="
pack "$NEXT_VERSION"
consumer "$GAME" "$NEXT_VERSION"
run dotnet build "$GAME/Consumer.csproj"
check "logs update" contains "updating res:// payload $VERSION -> $NEXT_VERSION"
check "local edit replaced" not grep -Fq "LOCAL EDIT" "$SCRIPT"
check "marker updated" file_equals "$MARKER" "$NEXT_VERSION"
check "UID survives update" file_equals "$UID_FILE" "uid://TESTUID12345"

echo "===== Adopt ====="
GAME="$TEST_ROOT/consumer-adopt"
ADDON="$GAME/addons/paradise"
SCRIPT="$ADDON/Authoring/AuthoredEntityNode.cs"
consumer "$GAME" "$VERSION"
mkdir -p "$(dirname "$SCRIPT")"
echo "// hand-vendored" > "$SCRIPT"
printf 'uid://VENDOREDUID99\n' > "$SCRIPT.uid"
echo '[plugin]' > "$ADDON/plugin.cfg"
run dotnet build "$GAME/Consumer.csproj"
check "logs adopt" contains "adopting a hand-vendored copy"
check "vendored source replaced" not grep -Fq "hand-vendored" "$SCRIPT"
check "existing UID preserved" file_equals "$SCRIPT.uid" "uid://VENDOREDUID99"
check "adoption marker written" file_equals "$ADDON/.paradise-addon-version" "$VERSION"

echo "===== Non-Godot project ====="
GAME="$TEST_ROOT/consumer-library"
consumer "$GAME" "$VERSION" Microsoft.NET.Sdk
run dotnet build "$GAME/Consumer.csproj"
check "no addon installed" not test -d "$GAME/addons"

if (( FAILURES > 0 )); then
  echo "$FAILURES FAILED"
  exit 1
fi
echo "ALL PASS"
