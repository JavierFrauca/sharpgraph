#!/usr/bin/env python
"""Medición fiel del coste en tokens de CodeGraph (lo que consume el modelo vía MCP).

- Estructural (callers/callees/query): se ejecuta el CLI `codegraph ... --json`,
  y se REFORMATEA al formato markdown que devuelve la herramienta MCP (sin ANSI),
  luego se cuenta con tiktoken. Validado contra capturas MCP reales (Q1≈187, Q3≈555).
- explore: CodeGraph devuelve fuente verbatim de ficheros enteros; se modela leyendo
  esos ficheros de disco, numerados como hace explore, + framing fijo (~230 tok).
- Si callers/callees devuelve vacío (típico en interfaces inyectadas), el flujo
  realista cae en explore → se usan los fallback_files.
"""
import json, subprocess, os, re
import tiktoken

ENC = tiktoken.get_encoding("cl100k_base")
def toks(s): return len(ENC.encode(s or ""))
FRAMING = 230  # cabecera + blast-radius + disclaimer de explore

_ANSI = re.compile(r"\x1b\[[0-9;]*m")

def _cli_json(repo, args):
    # En Windows 'codegraph' es un .cmd → hace falta shell=True para resolverlo.
    def q(a): return f'"{a}"' if " " in a else a
    cmd = "codegraph " + " ".join(q(a) for a in args) + " --json"
    try:
        r = subprocess.run(cmd, cwd=repo, shell=True, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=60)
        s = _ANSI.sub("", r.stdout)
        i = min([p for p in (s.find("{"), s.find("[")) if p >= 0], default=-1)
        return json.loads(s[i:]) if i >= 0 else None
    except Exception:
        return None

def callers(repo, symbol, limit=20):
    d = _cli_json(repo, ["callers", symbol, "--limit", str(limit)])
    lst = (d or {}).get("callers", []) or []
    body = f"## Callers of {symbol} ({len(lst)} found)\n" + "\n".join(
        f"- {c.get('name')} ({c.get('kind')}) - {c.get('filePath')}:{c.get('startLine')}" for c in lst)
    return toks(body), len(lst)

def callees(repo, symbol, limit=20):
    d = _cli_json(repo, ["callees", symbol, "--limit", str(limit)])
    lst = (d or {}).get("callees", []) or []
    body = f"## Callees of {symbol} ({len(lst)} found)\n" + "\n".join(
        f"- {c.get('name')} ({c.get('kind')}) - {c.get('filePath')}:{c.get('startLine')}" for c in lst)
    return toks(body), len(lst)

def query(repo, term, limit=10):
    d = _cli_json(repo, ["query", term, "--limit", str(limit)])
    items = d or []
    nodes = [(it.get("node") or it) for it in items]
    nodes = [n for n in nodes if n.get("kind") != "import"]  # imports = ruido, MCP no los muestra
    body = f"## Search Results ({len(nodes)} found)\n" + "\n".join(
        f"### {n.get('name')} ({n.get('kind')})\n{n.get('filePath')}:{n.get('startLine')}" for n in nodes)
    return toks(body), len(nodes)

def explore_cost(repo, files):
    """Modela explore: fuente verbatim numerada de los ficheros indicados + framing."""
    total = FRAMING
    for relpath in files:
        p = os.path.join(repo, relpath.replace("/", os.sep))
        try:
            lines = open(p, encoding="utf-8", errors="replace").read().splitlines()
        except Exception:
            continue
        total += toks("\n".join(f"{i+1}\t{ln}" for i, ln in enumerate(lines)))
    return total

def measure(repo, cgspec):
    """cgspec: dict con 'mode'. Devuelve (tokens, etiqueta, answered)."""
    mode = cgspec["mode"]
    if mode == "callers":
        t, n = callers(repo, cgspec["symbol"], cgspec.get("limit", 20))
        if n == 0 and cgspec.get("fallback_files"):
            return explore_cost(repo, cgspec["fallback_files"]), "callers vacío → explore", True
        return t, f"callers ({n})", n > 0
    if mode == "callees":
        t, n = callees(repo, cgspec["symbol"], cgspec.get("limit", 20))
        if n == 0 and cgspec.get("fallback_files"):
            return explore_cost(repo, cgspec["fallback_files"]), "callees vacío → explore", True
        return t, f"callees ({n})", n > 0
    if mode == "query":
        t, n = query(repo, cgspec["term"], cgspec.get("limit", 10))
        return t, f"query ({n})", n > 0
    if mode == "explore":
        return explore_cost(repo, cgspec["files"]), f"explore ({len(cgspec['files'])} fich.)", True
    return 0, "?", False
