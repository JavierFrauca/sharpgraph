# SharpGraph vs CodeGraph vs Sourcegraph MCP vs code-graph-mcp

Comparativa **pública y anonimizada** de servidores MCP de "code graph" para navegación
de código con LLMs. Su objetivo es ayudar a elegir herramienta según contexto, no declarar
un ganador universal: cada una tiene un ángulo distinto.

> Esta es la versión pública de la comparativa interna que mantiene el proyecto. Los
> ratios cuantitativos provienen de [`BENCHMARK.md`](BENCHMARK.md), reproducible sobre
> cualquier repositorio propio con `bench/benchmark.py`. La comparativa de **calidad**
> (outputs reales lado a lado) está en [`CALIDAD.md`](CALIDAD.md).

---

## Resumen ejecutivo

| Herramienta | Ámbito | Tesis | Cuándo elegirlo |
|---|---|---|---|
| **SharpGraph** | **C# / .NET** | Navegación token-efficient de relaciones (MediatR, DI, endpoints) | Proyectos .NET donde el coste de contexto del agente importa |
| **CodeGraph** (colbymchenry) | Multi-lenguaje | Grafo de callers/callees + `explore` (fuente verbatim multi-fichero) | Multi-lenguaje, lectura profunda de código |
| **Sourcegraph MCP** | Multi-lenguaje, escala | Búsqueda y lectura de código a escala empresarial (indexado de repos masivos) | Monorepos grandes, organización con Sourcegraph ya desplegado |
| **code-graph-mcp** (sdsrss) | Multi-lenguaje | AST knowledge graph + semántica + call graph + HTTP routes | Multi-lenguaje con rastreo HTTP cross-lang |

**No hay un único ganador universal.** La diferencia clave es el ángulo:

- **Multi-lenguaje** → CodeGraph / Sourcegraph / code-graph-mcp ganan.
- **Profundidad .NET + tokens mínimos** → SharpGraph gana (modela MediatR/DI/routing
  de forma nativa, devuelve metadatos compactos en vez de ficheros).

---

## Tabla cualitativa

| Característica | SharpGraph | CodeGraph | Sourcegraph MCP | code-graph-mcp |
|---|---|---|---|---|
| Lenguajes soportados | C# únicamente | ~38 | ~igual a Sourcegraph (muchos) | multi |
| Modelado MediatR/CQRS | **Sí, exacto** (`Sends`/`HandledBy`) | No específico | No | Parcial (call graph) |
| Resolución de DI (`AddScoped<I,C>`, `typeof`) | **Sí** | No | No | No |
| Endpoints ASP.NET Core (rutas + Minimal APIs) | **Sí, completos** | Genérico | Búsqueda textual | HTTP routes (cross-lang) |
| Call-sites a nivel de método (`file:line`) | **Sí** | Por símbolo | Por símbolo | Sí |
| `flow` (árbol de llamadas siguiendo DI, sin código) | **Sí** | No | No | No |
| Centralidad (PageRank / `hubs`) | **Sí** | — | — | No |
| Búsqueda semántica sin LLM/embeddings | BM25 en memoria | Varía | Sí (índice propio) | Sí |
| Filosofía de salida | Metadatos compactos (relación, línea) | Fuente verbatim multi-fichero | Fuente + contexto | Grafo + fuente |
| Persistencia / incrementalidad | Caché disco + watcher | Watcher | Índice servidor | Índice |
| Auto-escaneo al cambiar proyecto | **Sí** (hook Claude Code) | No | No | No |
| Madurez / comunidad | Proyecto joven, 1 mantenedor | Comunidad activa, issue tracker | Empresa, SLA | Proyecto open-source |
| Multiplataforma binario | win-x64 / linux-x64 / osx-arm64 | npm (Node) | Servidor | Varía |

---

## Tabla cuantitativa (ratios de tokens)

Reproducibles con `bench/benchmark.py` (tokenizador `tiktoken cl100k_base`). La batería
interna mezcla usos distintos; lo importante es el desglose por categoría.

| Categoría de pregunta | Ganador | Margen (tokens) |
|---|---|---|
| **Navegar / localizar / resolver** (deps, DI, call-sites, endpoints, hubs) | **SharpGraph** | **~7× vs CodeGraph · ~16× vs grep** |
| **Comprensión de flujo** (`flow`: árbol siguiendo DI) | **SharpGraph** | **~45×** vs leer la cadena |
| **Leer la clase completa** | empate | `understand` gana en clases grandes; leer el fichero gana las pequeñas |
| **Explicar la lógica de un método** | empate | requiere su fuente (`get_source`); techo = leerla |
| **Literales / strings** | **grep** | los grafos no indexan literales |

**Lectura.** Para el uso dominante de un agente —navegar código, localizar usos, resolver
DI, trazar a endpoints y entender flujos— SharpGraph ahorra ~un orden de magnitud de tokens
al entregar datos derivados del grafo en formato compacto. La filosofía CodeGraph (fuente
verbatim multi-fichero) gana cuando el objetivo es *leer* código a fondo; ahí es paridad.

> Los valores absolutos dependen de los repos; los **ratios** entre enfoques son robustos
> porque todos se miden con el mismo tokenizador sobre las mismas preguntas.

---

## Diferencias arquitecturales

### SharpGraph
- **AST sin compilar** (Roslyn `CSharpSyntaxTree.ParseText`), paralelo por fichero.
- Cada `.cs` produce un `FileFragment` aislado; el grafo los fusiona.
- Resolución de identidad por **FQN** (namespace + usings + prefijo compartido) con
  detección de ambigüedad.
- Modelado explícito de DI y MediatR como tipos de arista propios (`DiBound`, `Sends`,
  `HandledBy`).
- Caché en disco por solución, versionada por parser; watcher con debounce 400 ms.

### CodeGraph
- Grafo persistente que observa el proyecto y se actualiza en cada cambio de fichero.
- `callers`/`callees` por símbolo; `explore` devuelve fuente verbatim de los ficheros
  relevantes (filosofía "leer a fondo").
- Agnóstico del lenguaje: no modela patrones de framework específicos.

### Sourcegraph MCP
- Requiere instancia de Sourcegraph (o usa la pública). Pensado para código a escala:
  búsqueda cross-repo, símbolos indexados, referencias.
- No construye grafo de relaciones framework-aware; es más bien "grep + lectura a escala".

### code-graph-mcp
- AST knowledge graph genérico con traversal de call graph y rastreo de rutas HTTP.
- Más cercano a SharpGraph en espíritu (graph + routes), pero multi-lenguaje y sin
  modelado específico de DI/MediatR.

---

## Cuándo NO elegir SharpGraph

- Tu código no es C# / .NET.
- Tu caso de uso dominante es leer/comprender a fondo (CodeGraph gana o empata).
- Necesitas buscar literales/strings frecuentemente (grep).
- Estás en una organización con Sourcegraph ya desplegado a escala.

## Cuándo elegir SharpGraph

- Tu base de código es .NET con DI/MediatR/ASP.NET Core.
- Quieres que el agente navegue **sin leer ficheros enteros** (ahorro de tokens).
- Necesitas trazar "¿desde qué endpoint se llama a X?" de forma exacta.
- Valoras `flow()` para entender orquestación cruzando ficheros sin devolver código.
