#!/usr/bin/env python
"""
Batería comparativa SharpGraph vs CodeGraph (latencia/escala, no tokens):
  1. indexado en frío (cachés borradas) — mediana de N pasadas
  2. arranque caliente (cargar índice + reportar estado)
  3. latencia de respuesta por query (CLI, incluye arranque del proceso)
  4. huella en disco del índice
  5. latencia MCP por tool de SharpGraph (lo que siente el agente por llamada)

Uso:  python compare_perf.py [exe_sharpgraph]
Corpora: bench/_external/CleanArchitecture (110 .cs) y %TEMP%/SharpGraphCorpus
(generable con %TEMP%/sgcorpus_gen.py, 5001 .cs) si existe.
"""
import hashlib
import json
import os
import shutil
import statistics
import subprocess
import sys
import threading
import time

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

SG = (sys.argv[1] if len(sys.argv) > 1 else
      os.path.join(os.path.dirname(__file__), "..", "publish", "SharpGraph.exe"))
SG = os.path.abspath(SG)
CA = os.path.abspath(os.path.join(os.path.dirname(__file__), "_external", "CleanArchitecture"))
CORPUS = os.path.join(os.environ.get("TEMP", "."), "SharpGraphCorpus")
CACHE_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SharpGraph", "cache")
CG = shutil.which("codegraph")
PASSES = 3


def sg_key(repo):
    # .NET hace Path.GetFullPath, que EXPANDE nombres corto 8.3 a la forma larga;
    # os.path.realpath es el equivalente en python — si no, la clave no coincide
    # y el wipe/size mira ficheros equivocados.
    return hashlib.sha1(os.path.realpath(repo).lower().encode()).hexdigest().upper()


def wipe_sg(repo):
    key = sg_key(repo)
    for name in os.listdir(CACHE_DIR) if os.path.isdir(CACHE_DIR) else []:
        if name.startswith(key):
            try:
                os.remove(os.path.join(CACHE_DIR, name))
            except OSError:
                pass


def wipe_cg(repo):
    shutil.rmtree(os.path.join(repo, ".codegraph"), ignore_errors=True)


def run(cmd, cwd, timeout=180):
    """cmd ya es lista; codegraph es un .cmd de npm → vía cmd /c."""
    if cmd[0].lower().endswith((".cmd", ".bat")):
        cmd = ["cmd", "/c", os.path.abspath(cmd[0])] + cmd[1:]
    t0 = time.perf_counter()
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=timeout)
    return time.perf_counter() - t0, p


def median_of(fn, n=PASSES):
    xs = [fn() for _ in range(n)]
    return xs


def fmt(xs):
    return f"{statistics.median(xs)*1000:8.0f} ms  (pasadas: " + " ".join(f"{x*1000:.0f}" for x in xs) + ")"


def dir_size(path):
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def sg_mcp_latencies(repo, calls):
    """Latencia por tool vía JSON-RPC sobre stdio (lo que paga el agente por llamada)."""
    proc = subprocess.Popen([SG, repo], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, bufsize=1,
                            encoding="utf-8", errors="replace")
    responses = {}
    lock = threading.Lock()

    def reader():
        for line in proc.stdout:
            try:
                m = json.loads(line)
            except (json.JSONDecodeError, ValueError):
                continue
            if isinstance(m, dict) and "id" in m and m["id"] is not None:
                with lock:
                    responses[m["id"]] = (time.perf_counter(), m)

    threading.Thread(target=reader, daemon=True).start()

    def send(o):
        proc.stdin.write(json.dumps(o) + "\n")
        proc.stdin.flush()

    def wait_for(rid, timeout_s=30):
        t0 = time.perf_counter()
        while time.perf_counter() - t0 < timeout_s:
            with lock:
                if rid in responses:
                    return responses[rid]
            time.sleep(0.002)
        return None

    send({"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {
        "protocolVersion": "2024-11-05", "capabilities": {},
        "clientInfo": {"name": "perf", "version": "1"}}})
    wait_for(0)
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    results = []
    rid = 1
    for tool, args in calls:
        t0 = time.perf_counter()
        send({"jsonrpc": "2.0", "id": rid, "method": "tools/call",
              "params": {"name": tool, "arguments": args}})
        got = wait_for(rid, 60)
        dt = (got[0] - t0) if got else float("nan")
        results.append((tool, dt))
        rid += 1
    try:
        proc.stdin.close()
        proc.terminate()
    except OSError:
        pass
    return results


def main():
    repos = [("CleanArchitecture (110 .cs)", CA)]
    if os.path.isdir(CORPUS):
        repos.append(("Corpus sintético (5.001 .cs)", CORPUS))

    print(f"SharpGraph: {SG}")
    print(f"CodeGraph : {CG}")
    print(f"Pasadas por medición: {PASSES} (se muestra mediana y pasadas)\n")

    # ---------- 1. indexado en frío ----------
    print("== 1. INDEXADO EN FRÍO (cachés borradas antes de cada pasada) ==")
    for label, repo in repos:
        sg_xs = median_of(lambda: (wipe_sg(repo), wipe_cg(repo),
                                run([SG, "scan", repo], repo)[0])[2])
        cg_xs = median_of(lambda: (wipe_sg(repo), wipe_cg(repo),
                                run([CG, "init", repo], repo)[0])[2])
        print(f"  {label}")
        print(f"    sharpgraph scan : {fmt(sg_xs)}")
        print(f"    codegraph init  : {fmt(cg_xs)}")

    # ---------- 2. arranque caliente ----------
    print("\n== 2. ARRANQUE CALIENTE (cargar índice existente + estado) ==")
    for label, repo in repos:
        # asegurar índices presentes
        run([SG, "scan", repo], repo)
        run([CG, "init", repo], repo)
        sg_xs = median_of(lambda: run([SG, "stats"], repo)[0])
        cg_xs = median_of(lambda: run([CG, "status"], repo)[0])
        print(f"  {label}")
        print(f"    sharpgraph stats: {fmt(sg_xs)}")
        print(f"    codegraph status: {fmt(cg_xs)}")

    # ---------- 3. latencia de queries CLI ----------
    print("\n== 3. LATENCIA DE RESPUESTA (CLI completo: proceso + índice + query) ==")
    queries_ca = [
        ("callers de IApplicationDbContext",
         [SG, "callers", "IApplicationDbContext"], [CG, "callers", "IApplicationDbContext", "--json"]),
        ("dependencias de CreateTodoListCommandHandler",
         [SG, "usages", "CreateTodoListCommandHandler"], [CG, "callees", "CreateTodoListCommandHandler", "--json"]),
        ("blast radius de IApplicationDbContext",
         [SG, "impact", "IApplicationDbContext"], [CG, "callers", "IApplicationDbContext", "--json"]),
    ]
    for label, sg_cmd, cg_cmd in queries_ca:
        sg_xs = median_of(lambda: run(sg_cmd, CA)[0])
        cg_xs = median_of(lambda: run(cg_cmd, CA)[0])
        print(f"  CA · {label}")
        print(f"    sharpgraph: {fmt(sg_xs)}")
        print(f"    codegraph : {fmt(cg_xs)}")

    if os.path.isdir(CORPUS):
        q50 = "Corp.Payroll.D050.Repositories.Repo0"
        queries_corpus = [
            (f"callers de {q50}",
             [SG, "callers", q50], [CG, "callers", "Repo0", "--json"]),
            (f"impact de {q50}",
             [SG, "impact", q50], [CG, "callers", "Repo0", "--json"]),
        ]
        for label, sg_cmd, cg_cmd in queries_corpus:
            sg_xs = median_of(lambda: run(sg_cmd, CORPUS)[0])
            cg_xs = median_of(lambda: run(cg_cmd, CORPUS)[0])
            print(f"  Corpus · {label}")
            print(f"    sharpgraph: {fmt(sg_xs)}")
            print(f"    codegraph : {fmt(cg_xs)}")

    # ---------- 4. huella en disco ----------
    print("\n== 4. HUELLA EN DISCO DEL ÍNDICE ==")
    for label, repo in repos:
        run([SG, "scan", repo], repo)
        run([CG, "init", repo], repo)
        sg_bytes = 0
        for name in os.listdir(CACHE_DIR):
            if name.startswith(sg_key(repo)):
                sg_bytes += os.path.getsize(os.path.join(CACHE_DIR, name))
        cg_bytes = dir_size(os.path.join(repo, ".codegraph"))
        print(f"  {label}: sharpgraph {sg_bytes/1024/1024:6.2f} MB · codegraph {cg_bytes/1024/1024:6.2f} MB")

    # ---------- 5. latencia MCP por tool (SharpGraph) ----------
    print("\n== 5. LATENCIA MCP POR TOOL — SharpGraph (por llamada, servidor ya vivo) ==")
    mcp_calls_ca = [
        ("search", {"pattern": "Todo"}),
        ("find_callers", {"typeName": "IApplicationDbContext"}),
        ("get_usages", {"typeName": "CreateTodoListCommandHandler"}),
        ("understand", {"typeName": "CreateTodoListCommandHandler", "bodyBudget": 200}),
        ("flow", {"typeName": "CreateTodoListCommandHandler", "member": "Handle"}),
        ("trace_to_endpoints", {"typeName": "IApplicationDbContext"}),
        ("impact", {"typeName": "IApplicationDbContext"}),
    ]
    mcp_calls_corpus = [
        ("find_callers", {"typeName": q50}),
        ("impact", {"typeName": q50}),
        ("search_semantic", {"query": "calculo de impuestos dominio", "topK": 10}),
        ("understand", {"typeName": "Corp.Payroll.D050.Services.Service3", "bodyBudget": 200}),
    ]
    for label, repo, calls in [("CA", CA, mcp_calls_ca)] + (
            [("Corpus", CORPUS, mcp_calls_corpus)] if os.path.isdir(CORPUS) else []):
        print(f"  {label}:")
        for tool, dt in sg_mcp_latencies(repo, calls):
            print(f"    {tool:22s} {dt*1000:7.1f} ms")


if __name__ == "__main__":
    main()
