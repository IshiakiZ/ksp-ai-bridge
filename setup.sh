#!/bin/bash
# One-time setup: build the plugin, install it into KSP, and register the MCP server for Claude in this folder.
#
#   ./setup.sh            build + install + write .mcp.json
#   ./setup.sh --mcp      only (re)write .mcp.json
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(pwd)"

# The desktop app does not inherit your shell PATH, so the config needs absolute paths.
PYTHON=""
for candidate in /usr/bin/python3 /opt/homebrew/bin/python3 /usr/local/bin/python3 "$(command -v python3 || true)"; do
  if [ -n "$candidate" ] && [ -x "$candidate" ] && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' 2>/dev/null; then PYTHON="$candidate"; break; fi
done
[ -n "$PYTHON" ] || { echo "error: Python 3.8+ not found" >&2; exit 1; }

if [ "${1:-}" != "--mcp" ]; then ./build.sh release; fi

"$PYTHON" - "$ROOT" "$PYTHON" "${KSP_DIR:-}" <<'PY'
import json, os, sys
root, python, ksp_dir = sys.argv[1], sys.argv[2], sys.argv[3]
# "timeout" is Claude Code's per-server limit for one tool call, in milliseconds (one hour here).
server = {"command": python, "args": [os.path.join(root, "mcp", "ksp_mcp.py")], "timeout": 3600000}
if ksp_dir:
    server["env"] = {"KSP_DIR": ksp_dir}
path = os.path.join(root, ".mcp.json")
config = {}
if os.path.exists(path):
    try:
        with open(path) as f:
            config = json.load(f)
    except ValueError:
        config = {}
config.setdefault("mcpServers", {})["ksp"] = server
with open(path, "w") as f:
    json.dump(config, f, indent=2)
    f.write("\n")
print("wrote", path)
PY
echo "Done. Open a new Claude session in this folder and approve the 'ksp' MCP server when asked."
