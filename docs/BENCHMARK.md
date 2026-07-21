# Benchmark de tokens — LocalGraph vs CodeGraph vs sin-MCP

Mide, con cuenta **exacta** de tokens (`tiktoken`, `cl100k_base`), cuántos tokens
entran en el contexto del modelo para responder una misma batería de preguntas con
tres enfoques:

- **LocalGraph** — sus herramientas MCP (se dirige el ejecutable por stdio).
- **CodeGraph** — su CLI `codegraph … --json` reformateado al formato de su salida MCP;
  el coste de su herramienta `explore` se modela como la fuente verbatim numerada de los
  ficheros que devuelve + ~230 tokens de framing.
- **sin-MCP** — salida de `grep` + el contenido íntegro de los ficheros que habría que leer.

Solo se mide **coste** (no calidad), y una herramienta solo "gana" una pregunta si
**responde de verdad**: en preguntas de literal, por ejemplo, únicamente grep es elegible
(los grafos no indexan literales); los resultados vacíos se marcan y no cuentan como victoria.

---

## Resultados públicos — CleanArchitecture (110 ficheros .cs)

Batería de **12 preguntas** sobre
[CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture)
(un repo .NET real, MIT, con CQRS/MediatR/DI/minimal APIs).

### Resumen ejecutivo

- **Total tokens (12 preguntas)** — LocalGraph: **1494** · CodeGraph: **5637** (3.8×) · sin-MCP: **11808** (7.9×)
- **Preguntas ganadas (menos tokens)** — LocalGraph: **9/12** · CodeGraph: **1/12** · sin-MCP: **2/12** (grep para literales)

### Desglose por categoría

| favor | n | LocalGraph | CodeGraph | sin-MCP | Gana |
|---|---|---|---|---|---|
| **LG** (navegar/localizar/DI/MediatR) | 7 | 992 | 3428 | 10737 | LocalGraph (6/7) |
| **FLOW** (comprensión de flujo) | 2 | 56 | 1147 | 575 | LocalGraph (2/2) |
| **CG** (leer/entender código) | 2 | 428 | 928 | 462 | sin-MCP (1/2) |
| **GREP** (literales) | 1 | 18† | 134† | 34 | sin-MCP (1/1) |

> † = la herramienta **no responde** (los grafos no indexan literales). Su coste no cuenta.

### Tabla por pregunta

| id | favor | pregunta | LG | CG | sinMCP | gana |
|---|---|---|---|---|---|---|
| Q01 | LG | ¿Qué tipos dependen de IApplicationDbContext? | **263** | 548 | 4322 | LocalGraph |
| Q02 | LG | ¿Dónde se invoca SaveChangesAsync? | **183** | 219 | 2372 | LocalGraph |
| Q03 | LG | ¿De qué depende CreateTodoItemCommandHandler? | 112 | **33** | 273 | CodeGraph |
| Q04 | LG | ¿Quién invoca CreateTodoItemCommand (MediatR)? | **52** | 464 | 1577 | LocalGraph |
| Q05 | LG | ¿Por dónde empezar? (hubs) | **254** | 1159 | 762 | LocalGraph |
| Q06 | LG | ¿Qué implementación se inyecta para IIdentityService? | **26** | 801 | 867 | LocalGraph |
| Q07 | FLOW | ¿Cómo funciona CreateTodoItemCommandHandler.Handle? | **33** | 464 | 183 | LocalGraph |
| Q08 | FLOW | ¿Qué hace GetTodosQueryHandler.Handle? | **23** | 683 | 392 | LocalGraph |
| Q09 | CG | Cuerpo del método Handle | **146** | 464 | 231 | LocalGraph |
| Q10 | CG | Clase completa + contexto (understand) | 282 | 464 | **231** | sin-MCP |
| Q11 | LG | Búsqueda semántica (persistencia) | **102** | 204 | 564 | LocalGraph |
| Q12 | GREP | String literal "Todo Lists" | 18† | 134† | **34** | sin-MCP |

### Lo que dicen los números

- **Navegar / localizar / resolver** (Q01-Q06): LocalGraph responde con metadatos del grafo
  (relación, línea, binding DI, PageRank) en salida compacta. Gana 6 de 7; la única que
  pierde (Q03, dependencias de un handler) es porque CodeGraph lo lista en 33 tok con un solo
  campo `callees`, mientras LocalGraph da la lista con la relación de cada arista. Si
  LocalGraph usara `depth:1` sin marcar la relación, empataría.

- **Comprensión de flujo** (Q07-Q08): la tool `flow` destila el árbol de llamadas
  (33 y 23 tokens) siguiendo bindings DI. CodeGraph necesita `explore` del fichero
  entero (464-683 tokens) para el mismo entendimiento. ~15-20× más barato.

- **Leer código** (Q09-Q10): aquí hay casi paridad. `get_source` (Q09, 146 tok) gana
  a leer el fichero. `understand` (Q10, 282 tok) pierde contra leer el fichero en una
  clase pequeña, pero ganaría en una clase grande.

- **Literales** (Q12): grep gana (34 tok). Los grafos no indexan literales. Es la
  herramienta correcta.

---

## Reproducir

```bash
# 1. clona el repo de benchmark
git clone --depth 1 https://github.com/JasonTaylorDev/CleanArchitecture.git bench/_external/CleanArchitecture
pip install tiktoken

# 2. instala CodeGraph si quieres esa columna:
npm i -g codegraph
codegraph init bench/_external/CleanArchitecture

# 3. compila LocalGraph
dotnet publish -c Release -r win-x64 --self-contained -o publish

# 4. ejecuta
cd bench
python benchmark.py ../publish/LocalGraph.exe questions.cleanarchitecture.py
```

Genera `bench/RESULTS.md`. El fichero de preguntas está en
[`bench/questions.cleanarchitecture.py`](../bench/questions.cleanarchitecture.py) (12 preguntas).
El módulo `questions.py` y `RESULTS.md` están en `.gitignore` (datos específicos del usuario).

Para usar **tu propio repositorio**, copia `questions.example.py` a `questions.py`, edita
los símbolos y rutas, y ejecuta `python benchmark.py`.

---

## Metodología

- **LocalGraph**: salida real de sus herramientas MCP (exe dirigido por stdio, un índice por repo).
- **CodeGraph** (v0.9.9): estructural medido con `codegraph <tool> --json` reformateado al
  formato markdown de su salida MCP; `explore` modelado como la fuente verbatim numerada de los
  ficheros que devuelve + ~230 tok de framing. Si `callers/callees` devuelve vacío (interfaces
  inyectadas), se usa el fallback `explore` (su flujo realista).
- **sin-MCP**: salida de `grep` + contenido íntegro de los ficheros con hit, **excluyendo
  tests/samples**.
- Se mide coste, no calidad. LocalGraph además aporta relación/línea/binding y filtra tests;
  CodeGraph mezcla símbolos homónimos y no resuelve DI.
