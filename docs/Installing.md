# Installing

**From a download** (the plugin is already built; needs KSP 1.12.x and Python 3.8+):

```bash
python3 install.py
```

That copies `GameData/AIBridge` into your KSP folder and writes `.mcp.json` so Claude finds the tools. If
KSP is not in Steam's usual folder: `python3 install.py "/path/to/Kerbal Space Program"`.

**From the source** (also needs the .NET SDK, `brew install dotnet`, to compile the plugin):

```bash
./setup.sh
```

Then:

1. Start (or restart) KSP. A small **AI Bridge** panel appears once the game has loaded.
2. Open a **new** Claude session in this folder and approve the `ksp` MCP server when asked.
3. Ask for something.

To use it from the regular Claude desktop chat instead, add the same `ksp` entry from `.mcp.json` to
`~/Library/Application Support/Claude/claude_desktop_config.json` and restart the app.
