#!/usr/bin/env python3
"""Developer helper: start a bridge command and print brief telemetry until the autopilot program ends."""
import importlib.machinery, importlib.util, json, sys, time, os
here = os.path.dirname(os.path.abspath(__file__))
loader = importlib.machinery.SourceFileLoader("rpc", os.path.join(here, "rpc")); spec = importlib.util.spec_from_loader("rpc", loader); rpc = importlib.util.module_from_spec(spec); loader.exec_module(rpc)

def watch(max_real_s=600, step=8):
    t0 = time.time(); last_event = None
    while time.time() - t0 < max_real_s:
        r = rpc.call("wait", {"timeout_s": step, "until": {"program_done": True}, "stop_on_event": True}, timeout=step + 20)
        if not r.get("ok"): print("ERR", r); return r
        r = r["result"]; v = r.get("vessel", {})
        for e in r.get("events", []): print("   EVENT", e["met"], e["event"])
        print(f"t={time.time()-t0:5.0f}s met={v.get('met_s')} alt={v.get('altitude_m')} v={v.get('surface_speed')} vs={v.get('vertical_speed')} ap={v.get('apoapsis_m')} pe={v.get('periapsis_m')} stg={v.get('stage')} thr={v.get('throttle')} F={v.get('thrust_kn')} dv={v.get('dv_left')} | {v.get('autopilot') or v.get('last_program')}", flush=True)
        if r["stopped_because"].startswith("program finished"):
            print("DONE:", r["stopped_because"]); return r
    print("watch timeout"); return None

if __name__ == "__main__":
    if len(sys.argv) > 1:
        params = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
        print(json.dumps(rpc.call(sys.argv[1], params))[:600])
    watch(float(os.environ.get("MAX", "600")), float(os.environ.get("STEP", "8")))
