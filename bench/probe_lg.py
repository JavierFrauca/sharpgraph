#!/usr/bin/env python
"""Probe rápido: ejecuta una query LocalGraph y muestra el output."""
import json, subprocess, sys, threading, time, os

EXE = os.environ.get("LG_EXE", os.path.join(os.path.dirname(__file__), "..", "publish", "LocalGraph.exe"))
REPO = os.environ.get("LG_REPO", os.path.join(os.path.dirname(__file__), "_external", "CleanArchitecture"))

def call(tool, args, timeout_s=15):
    proc = subprocess.Popen([EXE, REPO], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, bufsize=1, encoding="utf-8", errors="replace")
    out = []
    threading.Thread(target=lambda: [out.append(l) for l in proc.stdout], daemon=True).start()
    def send(o): proc.stdin.write(json.dumps(o)+"\n"); proc.stdin.flush()
    send({"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}})
    time.sleep(1.5)
    send({"jsonrpc":"2.0","method":"notifications/initialized"})
    time.sleep(0.3)
    send({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":tool,"arguments":args}})
    time.sleep(timeout_s)
    proc.stdin.close(); proc.terminate()
    for line in out:
        line=line.strip()
        if not line.startswith("{"): continue
        try:
            m=json.loads(line)
            if m.get("id")==1 and "result" in m:
                c=m["result"].get("content")
                return c[0].get("text","") if c else ""
        except: pass
    return ""

if __name__ == "__main__":
    tool = sys.argv[1] if len(sys.argv) > 1 else "stats"
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    print(call(tool, args))
