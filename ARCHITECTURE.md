# Cómo funciona LocalGraph

## Visión general

```
  DESARROLLADOR
       │
       │  abre proyecto / cambia de repo
       ▼
┌─────────────────────────────────────────────────────────────────┐
│                        CLAUDE CODE                              │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  Hook CwdChanged  (configurado por install.ps1)          │  │
│  │                                                          │  │
│  │  Cuando el directorio de trabajo cambia, dispara:        │  │
│  │    mcp_tool → localgraph → Scan → path: "${cwd}"         │  │
│  └───────────────────────┬──────────────────────────────────┘  │
│                          │  (también: el LLM puede llamar      │
│                          │   scan() manualmente en cualquier   │
│                          │   momento)                          │
│                          │                                     │
│  ┌───────────────────────▼──────────────────────────────────┐  │
│  │  LLM  (Claude)                                           │  │
│  │                                                          │  │
│  │  trace_to_endpoints("IGrossService")                     │  │
│  │  find_callers("ISalaryService", depth: 3)                │  │
│  │  search("GrossService")                                  │  │
│  └───────────────────────┬──────────────────────────────────┘  │
│                          │  stdio — protocolo MCP              │
└──────────────────────────┼──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│  LocalGraph.exe   (proceso MCP, vive mientras Claude Code       │
│                    está abierto)                                │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  GraphTools  (capa MCP)                                  │  │
│  │  Recibe llamadas de herramienta y delega en CodeGraph    │  │
│  └───────────────────────┬──────────────────────────────────┘  │
│                          │                                     │
│            ┌─────────────┴──────────────┐                      │
│            │                            │                      │
│            ▼ scan()                     ▼ consultas            │
│  ┌─────────────────────┐   ┌────────────────────────────────┐  │
│  │  SolutionScanner    │   │  CodeGraph  (RAM)              │  │
│  │                     │   │                                │  │
│  │  1. Descubre *.cs   │   │  _out:  { A → [B, C, D] }     │  │
│  │     (excluye obj/   │──▶│  _in:   { B → [A, X] }        │  │
│  │      bin/ .git/)    │   │  _endpoints: { Ctrl → [GET /] }│  │
│  │                     │   │  _files: { A → "A.cs" }       │  │
│  │  2. Parallel.ForEach│   │                                │  │
│  │     (1 hilo/core)   │   │  CurrentPath: "C:\repo\Payroll"│  │
│  │                     │   └────────────────────────────────┘  │
│  │  3. Roslyn AST      │                                       │
│  │     (sin compilar)  │                                       │
│  │                     │                                       │
│  │  TypeReferenceVisitor                                       │
│  │  - clases/interfaces│                                       │
│  │  - herencia         │                                       │
│  │  - campos/props/ctor│                                       │
│  │  - new T()          │                                       │
│  │  - [HttpGet] etc.   │                                       │
│  └─────────────────────┘                                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## ¿Dónde se persiste la información?

**En ningún sitio.** No hay base de datos, no hay ficheros, no hay caché en disco.

El grafo vive exclusivamente en la memoria RAM del proceso `LocalGraph.exe`. Su ciclo de vida es:

```
Claude Code arranca
       │
       ▼
LocalGraph.exe arranca (grafo vacío)
       │
       │  CwdChanged hook / scan() manual
       ▼
Grafo construido en RAM  ◀─── único estado, siempre en memoria
       │
       │  Claude Code se cierra
       ▼
LocalGraph.exe muere → grafo destruido
```

Al reabrir Claude Code, el grafo se reconstruye desde cero (en ~250 ms para proyectos medianos).

---

## ¿Cómo se diferencia entre repositorios?

No hay índices separados por repo. El grafo es **uno único** que contiene lo que se escaneó por última vez. `CodeGraph.CurrentPath` registra qué ruta está indexada.

```
Proyecto A abierto          Proyecto B abierto
──────────────────          ──────────────────
scan("C:\repo\Payroll")     scan("C:\repo\OtroRepo")
       │                           │
       ▼                           ▼
grafo = Payroll             grafo = OtroRepo
                            (Payroll ya no está)
```

`scan()` siempre empieza llamando a `graph.Clear()`, por lo que el grafo anterior se descarta completamente antes de indexar el nuevo.

Con el hook `CwdChanged` esto es automático: al cambiar de carpeta en Claude Code, se dispara `scan("${cwd}")` sin intervención manual.

---

## ¿Cómo se actualiza tras cambios en el código?

El grafo no se actualiza en caliente. Si modificas el código fuente, hay que re-escanear:

```
Opción 1 — automática (con CwdChanged):
    cambias de directorio y vuelves → se re-escanea

Opción 2 — manual:
    LLM llama a scan("C:\repo\MiProyecto")
    o tú mismo lo pides: "re-escanea el proyecto"
```

El re-escaneo completo es barato (~250 ms), por lo que no hay ningún mecanismo de actualización incremental: siempre se parte de cero.

---

## Flujo completo de una consulta

```
LLM pregunta: "¿desde dónde se llama a IGrossService?"
       │
       ▼
find_callers("IGrossService", depth: 3)
       │
       ▼
CodeGraph._in["IGrossService"]
  → ComplementService, AggregateAccrualService, GrossService,
    IrpfService, BonusCalculationCommand.Handler, ...
       │
       │  para cada caller, recursivo hasta depth=3
       ▼
Resultado en texto plano (~300 tokens)
devuelto al LLM por stdio MCP
```
