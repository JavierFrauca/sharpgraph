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

El sobre (`Envelope`) de la caché lleva una versión de parser (`ParserVersion`, hoy `7` en `GraphStore.cs`). Cuando se cambia la lógica de parsing o el modelo, se sube la versión y **todas las cachés viejas se invalidan** automáticamente al cargar, aunque el hash de los ficheros coincida.

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
Flush()  ──►  scanner.RescanFiles(pendientes)   // re-parsea SOLO esos ficheros
               graph.MergeFragments(lote)        // UNA fusión para todo el lote
               SaveThrottled()                   // caché: máx. 1 save / 10 s, atómico
```

La fusión (`GraphEngine.MergeFragments`) elige entre dos niveles:

- **Delta**: si todos los ficheros del lote siguen declarando los mismos tipos y exponiendo
  las mismas firmas de retorno, la tabla de símbolos global no cambia y basta restar las
  contribuciones del fragmento viejo y sumar las del nuevo — milisegundos. Es el caso común
  (editar cuerpos de métodos, llamadas, miembros).
- **Rebuild**: cualquier cambio estructural (tipo nuevo/borrado/renombrado, firma con otro
  tipo de retorno, fichero nuevo) reconstruye todos los índices UNA vez por lote.

`Flush` nunca se solapa consigo mismo (guarda anti-reentrada), de modo que una ráfaga de
guardados no encadena pipelines. El PageRank solo se recalcula en el rebuild: tras deltas,
el orden de sugerencias queda ligeramente desactualizado hasta el próximo cambio estructural
o `scan` (no afecta a la corrección de aristas ni call-sites).

Solo se re-parsean los ficheros cambiados (no el proyecto entero). Las carpetas `obj/`, `bin/`, `.git/`, `node_modules/` y `.vs/` se excluyen del watcher.

También se puede forzar un re-escaneo completo llamando a `scan(path)` manualmente.

---

## Índice de documentación (DocIndex)

Junto al grafo de código, `scan` indexa la documentación del proyecto en un índice
ligero y aparte (`Docs/DocIndex.cs`): `.md`/`.markdown`/`.txt` y configs JSON conocidas
(`appsettings*`, `launchSettings`). No pasa por `FileFragment` ni por la caché de
fragmentos: parsear texto plano es barato, así que se rehace en cada scan (milisegundos)
sin caché en disco.

- **`search_docs` / `sharpgraph docs`**: BM25 sobre el contenido, con título y secciones
  (`#`/`##`, fuera de code fences) ponderados x3. Devuelve ruta + sección (`§ …`), nunca
  contenido: leer el fichero es trabajo del cliente.
- **Menciones código↔docs**: los identificadores del texto se cruzan con la tabla de
  símbolos del grafo (solo nombres PascalCase/camelCase de 4+ caracteres, para no casar
  palabras comunes del prose). `search()` marca los tipos con `[docs:N]` y `understand()`
  lista los docs que los mencionan.
- **Watcher**: una segunda instancia de `FileSystemWatcher` (filter `"*"`) enruta los
  eventos por extensión a la cola de docs; el `Flush` reindexa el lote con símbolos
  frescos. Como los docs no viven en la caché de fragmentos, un cambio solo de docs no
  dispara el `Save` de `GraphStore`.

Se excluyen `obj/`, `bin/`, `.git/`, `node_modules/`, `.vs/` y los directorios ocultos
de herramientas (`.zcode`, `.claude`, `.vscode`, …); `.github` sí se indexa. Ficheros de
más de 1 MB se ignoran.

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
