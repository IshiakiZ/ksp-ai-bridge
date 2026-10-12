#!/bin/bash
# Build the AI Bridge plugin against the local KSP install.
#
#   ./build.sh            release build into GameData/AIBridge here, then install it into KSP
#   ./build.sh dev        hot-reload build: KSP picks it up while running (needs one KSP restart the first time)
#   ./build.sh check      compile only (both variants), install nothing
#   ./build.sh dist       build into GameData/ here without installing anything
#
# Needs the .NET SDK (`brew install dotnet`). Override the game location with KSP_DIR=/path/to/KSP.
set -euo pipefail

cd "$(dirname "$0")"
MODE="${1:-release}"
. tools/build/common.sh
GAMEDATA="$KSP_DIR/GameData/AIBridge"

case "$MODE" in
  check)
    # Both variants: the development build has extra commands the release build leaves out.
    compile "$TMP/AIBridge.dll" "" src/AIBridge
    compile "$TMP/AIBridge.Dev.dll" "DEV" src/AIBridge
    echo "ok: compiles"
    ;;
  dev)
    STAMP="$(date +%Y%m%d%H%M%S)"
    compile "$TMP/AIBridge.Core.$STAMP.dll" "DEV" src/AIBridge
    mkdir -p "$GAMEDATA/PluginData/dev"
    rm -f "$GAMEDATA/AIBridge.dll"
    if [ ! -f "$GAMEDATA/AIBridgeDev.dll" ] || [ src/DevLoader/DevLoader.cs -nt "$GAMEDATA/AIBridgeDev.dll" ]; then
      compile "$TMP/AIBridgeDev.dll" "" src/DevLoader
      cp "$TMP/AIBridgeDev.dll" "$GAMEDATA/AIBridgeDev.dll"
      echo "installed dev loader (restart KSP once to pick it up)"
    fi
    # Keep only the two most recent hot-reload builds, then publish the new one atomically.
    { ls -1 "$GAMEDATA/PluginData/dev"/AIBridge.Core.*.bin 2>/dev/null || true; } | sort | sed '$d' | while IFS= read -r old; do rm -f "$old"; done
    cp "$TMP/AIBridge.Core.$STAMP.dll" "$GAMEDATA/PluginData/dev/.incoming"
    mv "$GAMEDATA/PluginData/dev/.incoming" "$GAMEDATA/PluginData/dev/AIBridge.Core.$STAMP.bin"
    echo "ok: dev build $STAMP published"
    ;;
  release|dist)
    mkdir -p GameData/AIBridge
    compile "GameData/AIBridge/AIBridge.dll" "" src/AIBridge
    if [ "$MODE" = dist ]; then echo "ok: built into GameData/AIBridge"; exit 0; fi
    mkdir -p "$GAMEDATA"
    rm -f "$GAMEDATA/AIBridgeDev.dll"; rm -rf "$GAMEDATA/PluginData/dev"
    cp GameData/AIBridge/AIBridge.dll "$GAMEDATA/AIBridge.dll"
    echo "ok: release build installed to $GAMEDATA (restart KSP to load it)"
    ;;
  *)
    echo "usage: ./build.sh [release|dev|check|dist]" >&2; exit 2
    ;;
esac
