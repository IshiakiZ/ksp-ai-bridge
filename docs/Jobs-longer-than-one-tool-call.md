# Jobs longer than one tool call

Claude's apps limit how long a single tool call may run (the desktop app has been reported to cut calls
at about a minute), and a Mun transfer takes longer than that. Three things keep that from mattering:

* **Flight plans.** `ksp_flight_plan` hands the game a list of steps ("launch, reach orbit, transfer,
  capture, save") and the plugin runs them one after another by itself. Nothing is waiting on a call.
* **A measured call budget.** The server does not guess the limit. It starts at a safe 45 seconds, tries
  longer waits when nothing is at stake, notices when the client cancels or abandons a call, and remembers
  the result per client in `PluginData/mcp_call_limits.json`. `ksp_wait` accepts timeouts up to an hour and
  simply returns "call again" at the measured budget. `ksp_call_limit` shows or re-measures it.
* **A raised ceiling where the client allows it.** `setup.sh` writes `"timeout": 3600000` for the server in
  `.mcp.json`, and for clients with a shell there is `python3 mcp/ksp_mcp.py wait '{"sequence_done": true}' 1800`,
  which blocks in the background until the game reports the condition.
