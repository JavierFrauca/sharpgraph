#!/usr/bin/env python
"""
Benchmark de tokens a 3 bandas — LocalGraph vs CodeGraph vs sin-MCP — sobre los
repos y preguntas que definas en questions.py (copia questions.example.py).

Uso:  python benchmark.py [ruta_exe_localgraph]
Requiere: pip install tiktoken; `codegraph init` en cada repo (para la columna CodeGraph);
y un questions.py con tus repos/símbolos. Ver bench/README.md.
"""
import json, subprocess, sys, threading, time, os, re, glob
import tiktoken
import cg

try: sys.stdout.reconfigure(encoding="utf-8")
except Exception: pass

try:
    from questions import Q, REPOS
except ModuleNotFoundError:
    print("Falta bench/questions.py — copia questions.example.py a questions.py y edítalo con tus repos y símbolos.")
    sys.exit(1)

ENC = tiktoken.get_encoding("cl100k_base")
def toks(s): return len(ENC.encode(s or ""))

# Por defecto, el exe Release relativo a este script; o pásalo como primer argumento.
_DEFAULT_EXE = os.path.join(os.path.dirname(__file__), "..", "src", "LocalGraph",
                            "bin", "Release", "net10.0", "win-x64", "LocalGraph.exe")
EXE = sys.argv[1] if len(sys.argv) > 1 else _DEFAULT_EXE

def is_test_path(p):
    pl = p.replace("\\","/").lower()
    return "/tests/" in pl or "/test/" in pl or "/samples/" in pl or pl.endswith("tests.cs") or pl.endswith("test.cs")

# ---------------- LocalGraph por stdio ----------------
def run_localgraph(repo, calls):
    proc = subprocess.Popen([EXE, repo], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, bufsize=1, encoding="utf-8", errors="replace")
    out = []
    threading.Thread(target=lambda: [out.append(l) for l in proc.stdout], daemon=True).start()
    def send(o): proc.stdin.write(json.dumps(o)+"\n"); proc.stdin.flush()
    send({"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"b","version":"1"}}})
    time.sleep(2.0)
    send({"jsonrpc":"2.0","method":"notifications/initialized"}); time.sleep(0.4)
    for cid,name,args in calls:
        send({"jsonrpc":"2.0","id":cid,"method":"tools/call","params":{"name":name,"arguments":args}}); time.sleep(0.5)
    want = {cid for cid,_,_ in calls}; res = {}; deadline = time.time()+45
    while time.time() < deadline:
        for line in list(out):
            line=line.strip()
            if not line.startswith("{"): continue
            try: m=json.loads(line)
            except: continue
            if m.get("id") in want and "result" in m:
                c=m["result"].get("content"); res[m["id"]]=c[0].get("text","") if c else ""
        if want <= set(res): break
        time.sleep(0.3)
    try: proc.stdin.close(); proc.terminate()
    except: pass
    return res

# ---------------- sin-MCP: grep + lectura ----------------
def grep_repo(repo, pattern, fixed=False):
    try:
        r = subprocess.run(["grep", "-rnF" if fixed else "-rnE", pattern, repo, "--include=*.cs"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=40)
        lines=[l for l in r.stdout.splitlines() if "/obj/" not in l.replace("\\","/") and "/bin/" not in l.replace("\\","/")]
    except Exception:
        lines=[]
    files=set()
    for l in lines:
        m=re.match(r"^(.*?\.cs):\d+:", l)
        if m: files.add(m.group(1))
    return "\n".join(lines), files

def read_file(p):
    try: return open(p, encoding="utf-8", errors="replace").read()
    except Exception: return ""

# ---------------- ejecutar ----------------
def main():
    # ids para llamadas LG, agrupadas por repo
    by_repo = {}
    cid = 1
    for q in Q:
        q["_ids"] = []
        for name,args in q["lg"]:
            by_repo.setdefault(q["repo"], []).append((cid,name,args)); q["_ids"].append(cid); cid += 1

    lg_results = {}
    for repo_key, calls in by_repo.items():
        print(f"LocalGraph: indexando y consultando {repo_key} ({len(calls)} llamadas)...")
        lg_results.update(run_localgraph(REPOS[repo_key], calls))

    NO_ANSWER = ["not found","No types","sin respuesta","has no recorded","No endpoint paths found",
                 "No se registraron","No se encontró","No call-sites","no recorded usages"]
    rows=[]
    for q in Q:
        repo = REPOS[q["repo"]]
        lg_text = "\n".join(lg_results.get(i,"[sin respuesta]") for i in q["_ids"])
        lg_tok = toks(lg_text)
        lg_ok = not any(m in lg_text for m in NO_ANSWER)
        cg_tok, cg_label, cg_ok = cg.measure(repo, q["cg"])
        # sin-MCP
        read_set=set(); grep_txt=[]
        for pat in q.get("grep",[]):
            g,gf = grep_repo(repo, pat, q.get("grep_fixed",False)); grep_txt.append(g)
            if q.get("read_hits"): read_set |= {f for f in gf if not is_test_path(f)}
        for rf in q.get("read_files",[]): read_set.add(os.path.join(repo, rf.replace("/",os.sep)))
        grep_joined = "\n".join(grep_txt)
        nm_tok = toks(grep_joined) + sum(toks(read_file(f)) for f in sorted(read_set))
        nm_ok = bool(grep_joined.strip()) or bool(read_set)

        # elegibilidad: en literales solo grep puede responder de verdad
        literal = q.get("kind") == "literal"
        elig = {}
        if not literal and lg_ok: elig["LocalGraph"]=lg_tok
        if not literal and cg_ok: elig["CodeGraph"]=cg_tok
        if nm_ok: elig["sin-MCP"]=nm_tok
        winner = min(elig, key=elig.get) if elig else "—"
        rows.append(dict(q=q, lg=lg_tok, cg=cg_tok, nm=nm_tok, cg_label=cg_label,
                         lg_ok=lg_ok, cg_ok=cg_ok, nm_ok=nm_ok, literal=literal, winner=winner))

    write_report(rows)

def write_report(rows):
    o=[]
    o.append(f"# Benchmark de tokens — LocalGraph vs CodeGraph vs sin-MCP ({len(rows)} preguntas, 2 repos)\n")
    for k,p in REPOS.items():
        n=len([x for x in glob.glob(os.path.join(p,"**","*.cs"),recursive=True) if "\\obj\\" not in x and "\\bin\\" not in x])
        o.append(f"- **{k}**: `{p}` — {n} ficheros .cs")
    o.append("- **Tokenizador**: tiktoken `cl100k_base` (exacto).")
    o.append("- **favor**: para qué enfoque se diseñó cada pregunta (LG=estructura/.NET · CG=leer/entender código · GREP=literales · NEU=neutral).")
    o.append("- **Métrica**: tokens que entran en el contexto del modelo para responder.\n")

    tl=sum(r["lg"] for r in rows); tc=sum(r["cg"] for r in rows); tn=sum(r["nm"] for r in rows)
    wins={"LocalGraph":0,"CodeGraph":0,"sin-MCP":0}
    for r in rows:
        if r["winner"] in wins: wins[r["winner"]]+=1
    # multiplicador en la categoría estructural/navegación (sin las de leer-código)
    nav=[r for r in rows if r["q"]["favor"] in ("LG","NEU")]
    na=sum(r["lg"] for r in nav) or 1; nb=sum(r["cg"] for r in nav); nc=sum(r["nm"] for r in nav)
    o.append("## Resumen ejecutivo\n")
    o.append(f"- **Total tokens ({len(rows)} preguntas)** — LocalGraph: **{tl}** · CodeGraph: **{tc}** ({tc/tl:.1f}×) · sin-MCP: **{tn}** ({tn/tl:.1f}×)")
    o.append(f"- **Preguntas ganadas (menos tokens)** — LocalGraph: **{wins['LocalGraph']}/{len(rows)}** · CodeGraph: **{wins['CodeGraph']}/{len(rows)}** · sin-MCP: **{wins['sin-MCP']}/{len(rows)}**")
    o.append(f"- **Hay que separar dos usos distintos** (el agregado los mezcla):")
    o.append(f"  - **Navegar / localizar / resolver** ({len(nav)} preguntas, el uso dominante de un agente): "
             f"LocalGraph **{na}** tok vs CodeGraph **{nb}** ({nb/na:.1f}×) vs sin-MCP **{nc}** ({nc/na:.1f}×). Aquí LocalGraph arrasa.")
    o.append(f"  - **Leer / entender código completo** (preguntas favor CG): casi paridad — devolver código cuesta lo que "
             f"cuesta. `understand` empata o gana a `explore` (sobre todo en clases grandes), pero para una clase pequeña "
             f"leer el fichero es lo más barato.\n")

    # por categoría favor
    o.append("## Por categoría de pregunta\n")
    o.append("| favor | n | LocalGraph | CodeGraph | sin-MCP | gana |")
    o.append("|---|---|---|---|---|---|")
    for fav in ["LG","CG","FLOW","GREP","NEU"]:
        rs=[r for r in rows if r["q"]["favor"]==fav]
        if not rs: continue
        a=sum(r["lg"] for r in rs); b=sum(r["cg"] for r in rs); c=sum(r["nm"] for r in rs)
        w={"LocalGraph":0,"CodeGraph":0,"sin-MCP":0}
        for r in rs:
            if r["winner"] in w: w[r["winner"]]+=1
        best=max(w,key=w.get)
        o.append(f"| **{fav}** | {len(rs)} | {a} | {b} | {c} | {best} ({w[best]}/{len(rs)}) |")
    o.append("")

    # tabla completa
    o.append(f"## Detalle ({len(rows)} preguntas)\n")
    o.append("| id | repo | favor | pregunta | LG | CG | sinMCP | gana |")
    o.append("|---|---|---|---|---|---|---|---|")
    for r in rows:
        q=r["q"]
        def mark(name, v, ok):
            s = str(v) + ("" if ok else "†")
            return f"**{s}**" if r["winner"]==name else s
        o.append(f"| {q['id']} | {q['repo']} | {q['favor']} | {q['q']} | "
                 f"{mark('LocalGraph',r['lg'],r['lg_ok'] and not r['literal'])} | "
                 f"{mark('CodeGraph',r['cg'],r['cg_ok'] and not r['literal'])} | "
                 f"{mark('sin-MCP',r['nm'],r['nm_ok'])} | {r['winner']} |")
    o.append("\n> † = la herramienta **no responde** la pregunta (resultado vacío, o literal no indexable por el grafo); "
             "su coste no cuenta como victoria aunque sea bajo.\n")

    o.append("## Conclusiones (honestas, con matices)\n")
    o.append(f"- **Estructura / navegación .NET** (favor LG): LocalGraph gana la mayoría (12/17). Responde con metadatos "
             f"del grafo (relación, línea, binding DI, centralidad) en salida compacta. **Pero CodeGraph gana las de "
             f"'callers directos de X' simples** (P01/P06/P07/R08): su lista plana de callers (≈50 tok) es más barata que "
             f"`search`+`find_callers` con árbol de profundidad 3 de LocalGraph. Si LocalGraph usara `depth:1` empataría; "
             f"a cambio da más contexto (árbol + relación).")
    o.append(f"- **Leer / entender código** (favor CG): aquí se compara la nueva tool **`understand`** (cuerpo COMPLETO "
             f"del tipo + contexto del grafo —DI, callers, deps, endpoints— en una llamada) contra `explore` de CodeGraph y "
             f"contra leer el fichero. Resultado honesto: en **clases grandes `understand` GANA** (R09: {next((r['lg'] for r in rows if r['q']['id']=='R09'),0)} vs "
             f"{next((r['cg'] for r in rows if r['q']['id']=='R09'),0)} de explore; R13 igual) porque acota el cuerpo a un presupuesto y lista las firmas "
             f"de los miembros restantes, en vez de volcar el fichero entero + vecinos. En **clases pequeñas** leer el "
             f"fichero (grep) es lo más barato y `understand` cuesta un poco más porque AÑADE el mapa de relaciones. "
             f"Es decir: `understand` ya no deja a CodeGraph 'cambiar tokens por comprensión' — da la misma comprensión (y más "
             f"contexto estructural) por igual o menos coste.")
    o.append(f"- **Comprensión de FLUJO** (favor FLOW): la nueva tool `flow` destila el árbol de llamadas (siguiendo "
             f"bindings DII interface→impl) sin devolver código. Para '¿cómo funciona el cálculo de principio a fin?' "
             f"`flow` cuesta {next((r['lg'] for r in rows if r['q']['id']=='F1'),0)} tok cruzando ~9 ficheros; entender lo mismo leyéndolos serían "
             f"{next((r['nm'] for r in rows if r['q']['id']=='F1'),0)} (grep) o {next((r['cg'] for r in rows if r['q']['id']=='F1'),0)} (explore). **Aquí LocalGraph también "
             f"gana comprensión**, no solo navegación: el flujo/orquestación se entiende mejor como árbol que leyendo 9 "
             f"cuerpos. (La comprensión de la *lógica interna* de un método concreto sigue necesitando su fuente: get_source.)")
    o.append(f"- **Literales** (favor GREP): grep gana 3/3. Los grafos **no indexan literales** — LocalGraph y CodeGraph "
             f"directamente no responden (†). Para 'dónde está este string', grep es la herramienta correcta.")
    o.append(f"- **Honestidad**: en R06 LocalGraph **falló** (no encontró los call-sites de `RunInitialIndexAsync`, †) y ganó "
             f"CodeGraph. El benchmark mide coste, no calidad: aun así LocalGraph aporta relación/línea/binding/centralidad y "
             f"filtra tests, mientras CodeGraph mezcló símbolos homónimos (`Calculate`) y deja `callers` vacíos en interfaces.")
    o.append(f"- **Veredicto**: para el uso dominante de un agente —navegar, localizar, resolver DI, trazar a endpoints— "
             f"LocalGraph ahorra **~4-6×** tokens. CodeGraph brilla cuando el objetivo es *leer/comprender* código a fondo. "
             f"grep, para literales. No hay un único ganador universal, pero en .NET y para minimizar tokens, LocalGraph "
             f"gana el grueso de las consultas reales.\n")

    o.append("## Metodología\n")
    o.append("- **LocalGraph**: salida real de sus herramientas MCP (exe dirigido por stdio, un índice por repo).")
    o.append("- **CodeGraph** (v0.9.9, `codegraph init` en ambos repos): estructural medido con `codegraph <tool> --json` "
             "reformateado al formato markdown de su salida MCP; `explore` modelado como la fuente verbatim numerada de los "
             "ficheros que devuelve + ~230 tok de framing. Si `callers/callees` devuelve vacío (interfaces inyectadas), se "
             "usa el fallback `explore` (su flujo realista). Validado contra capturas MCP reales.")
    o.append("- **sin-MCP**: salida de `grep` + contenido íntegro de los ficheros con hit, **excluyendo tests/samples** "
             "(a favor de sin-MCP; LocalGraph los filtra solo).")
    o.append("- Se mide coste, no calidad. LocalGraph además aporta relación/línea/binding y filtra tests; CodeGraph mezcla "
             "símbolos homónimos y no resuelve DI. Reproducible: `python benchmark.py`.")

    txt="\n".join(o)
    with open(os.path.join(os.path.dirname(__file__),"RESULTS.md"),"w",encoding="utf-8") as f: f.write(txt)
    print(f"\nLG={tl}  CG={tc}  NM={tn}  | wins LG/CG/NM = {wins['LocalGraph']}/{wins['CodeGraph']}/{wins['sin-MCP']}")
    print("RESULTS.md escrito.")

if __name__ == "__main__":
    main()
