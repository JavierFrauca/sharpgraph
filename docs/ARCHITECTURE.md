# Cómo funciona SharpGraph

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
│  │    mcp_tool → sharpgraph → Scan → path: "${cwd}"         │  │
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
│  SharpGraph.exe   (proceso MCP, vive mientras Claude Code       │
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
│  │  1. Descubre *.cs   │   │  _out:        { A → [B, C, D] }│  │
│  │     (excluye obj/   │──▶│  _in:         { B → [A, X] }   │  │
│  │      bin/ .git/)    │   │  _endpoints:  { Ctrl → [GET /] }   │
│  │                     │   │  _callsByCallee/Caller: call-sites│
│  │  2. Parallel.ForEach│   │  _diByService/Impl: bindings DI │  │
│  │     (1 hilo/core)   │   │  _members:    MemberSpan (get_source)│
│  │                     │   │  _rank:       PageRank (hubs)   │  │
│  │  3. Roslyn AST      │   │  _docs + BM25 (search_semantic) │  │
│  │     (sin compilar)  │   │                                │  │
│  │                     │   │  CurrentPath: "C:\repo\Payroll"│  │
│  │  TypeReferenceVisitor   └─────────────┬──────────────────┘  │
│  │  - clases/interfaces│                  │ (load/save)        │
│  │  - herencia/baseList│                  ▼                    │
│  │  - campos/props/ctor│   ┌────────────────────────────────┐  │
│  │  - new T() / call   │   │  GraphStore (caché en disco)   │  │
│  │  - [HttpGet]/routing│   │  %LOCALAPPDATA%\SharpGraph\    │  │
│  │  - MediatR Send/    │   │    cache\<hash>.json           │  │
│  │    IRequestHandler  │   │  (versionado por ParserVersion)│  │
│  │  - DI AddScoped<,>/ │   └────────────────────────────────┘  │
│  │    typeof / keyed   │                                       │
│  │  - Minimal APIs     │   ┌────────────────────────────────┐  │
│  │  - nested FQN       │   │  ProjectWatcher (en caliente)  │  │
│  │  - summary XML      │   │  FileSystemWatcher + debounce  │  │
│  └─────────────────────┘   └────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## ¿Dónde se persiste la información?

El grafo vive en **memoria RAM** durante la sesión, pero se **cachéa en disco** entre sesiones. No hay base de datos: la caché es JSON por solución en `%LOCALAPPDATA%\SharpGraph\cache\` (ver `Persistence/GraphStore.cs`).

```
Claude Code arranca
       │
       ▼
SharpGraph.exe arranca (grafo vacío)
       │
       │  CwdChanged hook / scan() manual
       ▼
1. graph.Clear(path)
2. store.TryLoad(path)  ──► si hay caché válida: MergeFragments(cached)  (instantáneo)
3. scanner.ScanIncrementalAsync(path)
       │  - descubre *.cs
       │  - para cada fichero, compara su hash con el del fragmento cacheado
       │  - solo re-parsea (Roslyn AST, paralelo) los nuevos o modificados
       │  - elimina del grafo los ficheros que ya no existen
       ▼
4. store.Save(path, fragments)  ──► sobrescribe la caché en disco
5. watcher.Watch(path)  ──► FileSystemWatcher con debounce 400 ms
```

El sobre (`Envelope`) de la caché lleva una versión de parser (`ParserVersion`, hoy `6` en `GraphStore.cs`). Cuando se cambia la lógica de parsing o el modelo, se sube la versión y **todas las cachés viejas se invalidan** automáticamente al cargar, aunque el hash de los ficheros coincida.

---

## ¿Cómo se diferencia entre repositorios?

No hay índices separados por repo en disco: la caché es **un fichero por ruta escaneada** (la clave es el SHA1 del path absoluta, ver `GraphStore.CacheFileFor`). En memoria, el grafo es **uno único** que contiene lo que se escaneó por última vez. `CodeGraph.CurrentPath` registra qué ruta está indexada.

```
Proyecto A abierto          Proyecto B abierto
──────────────────          ──────────────────
scan("C:\repo\Payroll")     scan("C:\repo\OtroRepo")
       │                           │
       ▼                           ▼
grafo = Payroll             grafo = OtroRepo
                            (Payroll ya no está en RAM,
                             pero su caché sigue en disco)
```

`scan()` siempre empieza llamando a `graph.Clear()`, por lo que el grafo anterior se descarta completamente antes de indexar el nuevo. Al volver a abrir un proyecto ya escaneado antes, el arranque en frío es instantáneo (se cargan los fragmentos cacheados y solo se re-parsean los ficheros con hash distinto).

Con el hook `CwdChanged` esto es automático: al cambiar de carpeta en Claude Code, se dispara `scan("${cwd}")` sin intervención manual.

---

## ¿Cómo se actualiza tras cambios en el código?

En caliente, mediante `ProjectWatcher` (un `FileSystemWatcher` con debounce de 400 ms sobre los `.cs` de la raíz). Al guardar un fichero:

```
fichero .cs guardado
       │
       ▼
ProjectWatcher.OnChanged  ──►  encola la ruta
       │  (debounce 400 ms: si llegan varios cambios, se procesan juntos)
       ▼
Flush()  ──►  para cada ruta pendiente:
               scanner.RescanFile(path)   // re-parsea SOLO ese fichero
             store.Save(scanPath, ...)    // actualiza la caché en disco
```

Solo se re-parsea el fichero cambiado (no el proyecto entero), lo que es prácticamente instantáneo. Las carpetas `obj/`, `bin/`, `.git/`, `node_modules/` y `.vs/` se excluyen del watcher.

También se puede forzar un re-escaneo completo llamando a `scan(path)` manualmente.

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
