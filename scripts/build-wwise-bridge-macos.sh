#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEFAULT_SDK_ROOT="$REPO_ROOT/third_party/wwise/SDK"
SDK_ROOT="${PARADISE_WWISE_SDK:-${WWISESDK:-$DEFAULT_SDK_ROOT}}"
WWISE_PLATFORM_DIR="${WWISE_MAC_PLATFORM_DIR:-Mac_Xcode2600}"
WWISE_CONFIG="${WWISE_CONFIG:-Release}"
SDK_CONFIG_ROOT="$SDK_ROOT/$WWISE_PLATFORM_DIR/$WWISE_CONFIG"
LIB_DIR="$SDK_CONFIG_ROOT/lib"
# NOTE: WWISE_CONFIG defaults to Release while OUT_DIR defaults to the Debug runtime output —
# a deliberate dev convenience (Release bridge, Debug runtime). Override either, or point
# PARADISE_WWISE_BRIDGE at the dylib directly for -c Release runs.
OUT_DIR="${PARADISE_WWISE_BRIDGE_OUT_DIR:-$REPO_ROOT/ParadiseRuntime/bin/Debug/net10.0}"
OUT_PATH="$OUT_DIR/libBankHeist.WwiseBridge.dylib"
SAMPLE_DLL_ROOT="$SDK_ROOT/samples/DynamicLibraries/AkSoundEngineDLL"
SAMPLE_SOUNDENGINE_ROOT="$SDK_ROOT/samples/SoundEngine"

required_files=(
  "$SDK_ROOT/include/AK/SoundEngine/Common/AkSoundEngine.h"
  "$SAMPLE_SOUNDENGINE_ROOT/POSIX/AkDefaultIOHookDeferred.cpp"
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkFileLocationBase.cpp"
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkGeneratedSoundBanksResolver.cpp"
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkMultipleFileLocation.cpp"
  "$LIB_DIR/libAkSoundEngine.a"
  "$LIB_DIR/libAkMemoryMgr.a"
  "$LIB_DIR/libAkStreamMgr.a"
  "$LIB_DIR/libAkVorbisDecoder.a"
  "$LIB_DIR/libAkOpusDecoder.a"
  "$LIB_DIR/libAkMeterFX.a"
)

for path in "${required_files[@]}"; do
  if [[ ! -f "$path" ]]; then
    echo "Missing Wwise bridge dependency: $path" >&2
    exit 1
  fi
done

mkdir -p "$OUT_DIR"

clang++ \
  -dynamiclib \
  -std=c++17 \
  -O2 \
  -DNDEBUG \
  -DAK_OPTIMIZED \
  -DAKSOUNDENGINE_EXPORTS \
  -DAKSOUNDENGINE_DLL \
  -I"$SDK_ROOT/include" \
  -I"$SAMPLE_SOUNDENGINE_ROOT/POSIX" \
  -I"$SAMPLE_SOUNDENGINE_ROOT/Common" \
  "$REPO_ROOT/native/Paradise.WwiseBridge/ParadiseWwiseBridge.cpp" \
  "$SAMPLE_SOUNDENGINE_ROOT/POSIX/AkDefaultIOHookDeferred.cpp" \
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkFileLocationBase.cpp" \
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkGeneratedSoundBanksResolver.cpp" \
  "$SAMPLE_SOUNDENGINE_ROOT/Common/AkMultipleFileLocation.cpp" \
  -L"$LIB_DIR" \
  -lAkMeterFX \
  -lAkOpusDecoder \
  -lAkSoundEngine \
  -lAkStreamMgr \
  -lAkVorbisDecoder \
  -lAkMemoryMgr \
  -framework AudioToolbox \
  -framework AudioUnit \
  -framework AVFAudio \
  -framework CoreAudio \
  -framework Foundation \
  -Wl,-install_name,@rpath/libBankHeist.WwiseBridge.dylib \
  -o "$OUT_PATH"

echo "Built Wwise bridge: $OUT_PATH"
