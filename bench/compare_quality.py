#!/usr/bin/env python
"""
Comparativa cualitativa: ejecuta las mismas preguntas en SharpGraph y CodeGraph
sobre CleanArchitecture y vuelca los outputs lado a lado.
"""
import json, subprocess, sys, threading, time, os

LG_EXE = os.path.join(os.path.dirname(__file__), "..", "publish", "SharpGraph.exe")
CA_DIR = os.path.join(os.path.dirname(__file__), "_external", "CleanArchitecture")
CG_DIR = CA_DIR  # codegraph init ya ejecutado

def lg_query(tool, args, timeout_s=15):
    proc = subprocess.Popen([LG_EXE, CA_DIR], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, bufsize=1, encoding="utf-8", errors="replace")
    out = []
    threading.Thread(target=lambda: [out.append(l) for l in proc.stdout], daemon=True).start()
    def send(o): proc.stdin.write(json.dumps(o)+"\n"); proc.stdin.flush()
    send({"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"q","version":"1"}}})
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

def cg_query(mode, symbol=None, limit=20, term=None, files=None):
    """Ejecuta codegraph CLI y captura salida."""
    cwd = CG_DIR
    if mode == "callers":
        r = subprocess.run(["codegraph", "callers", symbol, "--limit", str(limit), "--json"],
                          cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        try:
            # reformatear igual que el MCP lo haría
            data = json.loads(r.stdout) if r.stdout.startswith("[") or r.stdout.startswith("{") else {}
            callers = data.get("callers", []) or []
            if not callers:
                return f"## Callers of {symbol} (0 found — no results)"
            body = f"## Callers of {symbol} ({len(callers)} found)\n" + "\n".join(
                f"- {c.get('name')} ({c.get('kind')}) - {c.get('filePath')}:{c.get('startLine')}" for c in callers)
            return body
        except Exception as e:
            return f"[codegraph error: {e}]"
    elif mode == "callees":
        r = subprocess.run(["codegraph", "callees", symbol, "--limit", str(limit), "--json"],
                          cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        try:
            data = json.loads(r.stdout) if r.stdout.startswith("[") or r.stdout.startswith("{") else {}
            callees = data.get("callees", []) or []
            if not callees:
                return f"## Callees of {symbol} (0 found)"
            body = f"## Callees of {symbol} ({len(callees)} found)\n" + "\n".join(
                f"- {c.get('name')} ({c.get('kind')}) - {c.get('filePath')}:{c.get('startLine')}" for c in callees)
            return body
        except Exception as e:
            return f"[codegraph error: {e}]"
    elif mode == "query":
        r = subprocess.run(["codegraph", "query", term, "--limit", str(limit), "--json"],
                          cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        try:
            items = json.loads(r.stdout) if r.stdout.startswith("[") else []
            nodes = [(i.get("node") or i) for i in items if (i.get("node") or i).get("kind") != "import"]
            body = f"## Search: {term} ({len(nodes)} found)\n" + "\n".join(
                f"### {n.get('name')} ({n.get('kind')})\n{n.get('filePath')}:{n.get('startLine')}" for n in nodes)
            return body
        except Exception as e:
            return f"[codegraph error: {e}]"
    elif mode == "explore":
        if not files: return "(no files)"
        body = f"## explore ({len(files)} files)\n"
        for relpath in files[:3]:
            p = os.path.join(cwd, relpath.replace("/", os.sep))
            try:
                lines = open(p, encoding="utf-8", errors="replace").read().splitlines()
                body += f"\n### {relpath} ({len(lines)} lines)\n"
                for i, ln in enumerate(lines):
                    body += f"{i+1}\t{ln}\n"
            except:
                body += f"\n### {relpath} (not found)\n"
        return body
    return f"(unknown mode: {mode})"


QUESTIONS = [
    ("Q01 callers", "LG: find_callers IApplicationDbContext depth=2\nCG: callers IApplicationDbContext",
     "find_callers", {"typeName": "IApplicationDbContext", "depth": 2},
     "callers", "IApplicationDbContext"),

    ("Q02 callsites", "LG: find_call_sites IApplicationDbContext member=SaveChangesAsync\nCG: callers SaveChangesAsync",
     "find_call_sites", {"typeName": "IApplicationDbContext", "member": "SaveChangesAsync"},
     "callers", "SaveChangesAsync"),

    ("Q04 mediatr", "LG: find_callers CreateTodoItemCommand depth=2\nCG: callers CreateTodoItemCommand",
     "find_callers", {"typeName": "CreateTodoItemCommand", "depth": 2},
     "callers", "CreateTodoItemCommand"),

    ("Q06 di", "LG: resolve_di IIdentityService\nCG: — (no tiene equivalente, usaría explore de DI)",
     "resolve_di", {"typeName": "IIdentityService"},
     "explore", None, {"files": ["src/Infrastructure/DependencyInjection.cs"]}),

    ("Q07 flow", "LG: flow CreateTodoItemCommandHandler.Handle depth=3\nCG: explore del fichero",
     "flow", {"typeName": "CreateTodoItemCommandHandler", "member": "Handle", "depth": 3},
     "explore", None, {"files": ["src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]}),

    ("Q09 source", "LG: get_source CreateTodoItemCommandHandler member=Handle\nCG: explore del fichero",
     "get_source", {"typeName": "CreateTodoItemCommandHandler", "member": "Handle"},
     "explore", None, {"files": ["src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]}),

    ("Q10 full-type", "LG: get_source CreateTodoItemCommandHandler maxBodyLines=400\nCG: explore del fichero",
     "get_source", {"typeName": "CreateTodoItemCommandHandler", "maxBodyLines": 400},
     "explore", None, {"files": ["src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]}),
]

print("# Comparativa cualitativa SharpGraph vs CodeGraph\n")
print("Repo: CleanArchitecture (Jason Taylor, MIT, 110 ficheros .cs)\n")

for label, desc, lg_tool, lg_args, cg_mode, cg_symbol, *rest in QUESTIONS:
    cg_kw = rest[0] if rest else {}
    print(f"## {label}")
    print(f"_Query:_ {desc}\n")

    print("### SharpGraph")
    lg_out = lg_query(lg_tool, lg_args)
    print("```")
    print(lg_out[:2000])
    if len(lg_out) > 2000:
        print(f"\n... (truncado, {len(lg_out)} chars total)")
    print("```\n")

    print("### CodeGraph")
    if cg_mode == "explore":
        cg_out = cg_query("explore", files=cg_kw.get("files"))
    else:
        cg_out = cg_query(cg_mode, symbol=cg_symbol)
    print("```")
    print(cg_out[:2000])
    if len(cg_out) > 2000:
        print(f"\n... (truncado, {len(cg_out)} chars total)")
    print("```\n")

    print("---\n")
