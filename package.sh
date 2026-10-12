#!/bin/bash
# Make the downloads, in dist/:
#   KSP-AI-Bridge-<version>.zip          the mod, ready built, with the tools for Claude or a local model
#   KSP-AI-Bridge-Voice-<version>.zip    the same plus the ship's voice and spoken commands
# Nothing in the game folder is touched.
set -euo pipefail
cd "$(dirname "$0")"

VERSION="$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' src/AIBridge/Core/BridgeHost.cs)"
[ -n "$VERSION" ] || { echo "error: could not read the version number" >&2; exit 1; }

./build.sh dist
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
mkdir -p dist

bundle() {  # bundle <folder name> <voice: yes|no>
  local dir="$STAGE/$1"
  mkdir -p "$dir/GameData" "$dir/mcp" "$dir/docs"
  cp -R GameData/AIBridge "$dir/GameData/"
  cp mcp/ksp_mcp.py mcp/ksp_local.py "$dir/mcp/"
  cp install.py README.md "$dir/"
  cp -R docs/. "$dir/docs/"
  if [ "$2" = yes ]; then
    cp mcp/ksp_voice.py mcp/voice_worker.py "$dir/mcp/"
    cp setup_voice.sh "$dir/"
  fi
}

zipup() {  # zipup <folder name> <zip name>
  rm -f "dist/$2"
  (cd "$STAGE" && zip -r -q -X "$OLDPWD/dist/$2" "$1" -x '*.DS_Store')
  echo "dist/$2  ($(du -h "dist/$2" | cut -f1 | tr -d ' '))"
}

bundle "KSP-AI-Bridge" no
zipup "KSP-AI-Bridge" "KSP-AI-Bridge-$VERSION.zip"
bundle "KSP-AI-Bridge-Voice" yes
zipup "KSP-AI-Bridge-Voice" "KSP-AI-Bridge-Voice-$VERSION.zip"
