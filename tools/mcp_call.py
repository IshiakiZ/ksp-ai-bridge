#!/usr/bin/env python3
"""Developer helper: call one MCP tool through the real stdio protocol and print what the model would see.
   tools/mcp_call.py ksp_status
   tools/mcp_call.py ksp_parts '{"search": "terrier"}'
"""
import base64, json, os, subprocess, sys

here = os.path.dirname(os.path.abspath(__file__))
server = os.path.join(here, "..", "mcp", "ksp_mcp.py")


def call(name, arguments=None, show=True):
    msgs = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "mcp_call", "version": "1"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": name, "arguments": arguments or {}}},
    ]
    p = subprocess.run([sys.executable, server], input="\n".join(json.dumps(m) for m in msgs) + "\n", capture_output=True, text=True, timeout=900)
    reply = None
    for line in p.stdout.splitlines():
        m = json.loads(line)
        if m.get("id") == 2:
            reply = m
    if reply is None:
        raise RuntimeError("no reply: " + p.stderr)
    result = reply["result"]
    out = []
    for c in result["content"]:
        if c["type"] == "text":
            out.append(c["text"])
        elif c["type"] == "image":
            path = os.environ.get("SHOT", "/tmp/ksp_mcp_shot.jpg")
            with open(path, "wb") as f:
                f.write(base64.b64decode(c["data"]))
            out.append("[image saved to %s]" % path)
    text = ("ERROR: " if result.get("isError") else "") + "\n".join(out)
    if show:
        print(text)
    return text


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {})
