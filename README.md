# LocalGraph

Servidor MCP que construye un grafo de dependencias de un proyecto C# y lo expone como herramientas para LLMs. Permite responder preguntas como _"¿desde qué endpoint se llama a este servicio?"_ sin necesidad de leer decenas de ficheros.

---

## El problema que resuelve

En proyectos grandes con CQRS y MediatR, la cadena de llamadas no es directa:

```
[GET /salary]  ←  Controller  ──▶  Query  ◀──  Query.Handler  ──▶  ISalaryService
```

Un LLM que intente trazar esta ruta leyendo código necesitaría abrir varios ficheros y podría perder conexiones. LocalGraph escanea el proyecto una vez y responde a esas preguntas en milisegundos.

---

## Herramientas MCP

| Herramienta | Uso |
|---|---|
| `scan(path)` | Indexa un `.sln`, `.csproj` o carpeta. **Persistente e incremental** (caché en disco + watcher). |
| `trace_to_endpoints(typeName)` | Traza el camino desde un tipo hasta los endpoints HTTP. MediatR/buses modelados de forma **exacta**. |
| `find_callers(typeName, depth)` | Árbol de quién usa un tipo, con la **relación** de cada arista (`ctor-param`, `call`, `sends`…). |
| `get_usages(typeName)` | De qué tipos depende un tipo, con relación, líneas y marca `(external)`. |
| `find_call_sites(typeName, member)` | **Dónde se invoca de verdad** un método, con `fichero:línea`. Distingue inyección de llamada real. |
| `get_source(typeName, member)` | Devuelve el **código fuente** de un miembro concreto, sin leer el fichero entero. El gran ahorro de tokens. |
| `understand(typeName)` | **Comprender un tipo en 1 llamada**: cuerpo completo + contexto del grafo (DI, callers, deps, endpoints). Alternativa compacta a volcar ficheros. |
| `flow(typeName, member, depth)` | **¿Cómo funciona?** Destila el árbol de llamadas salientes (siguiendo bindings DI interface→impl) con `fichero:línea`, sin devolver código. Comprensión de flujo a una fracción de los tokens. |
| `resolve_di(typeName)` | Resuelve la **inyección de dependencias**: `IFoo → Foo [scoped]`. Sin leer `Program.cs`. |
| `search(pattern)` | Busca tipos por nombre parcial (solo tipos definidos en la solución). |
| `explore_context(typeOrPattern)` | Contexto bidireccional cercano: callers, dependencias, endpoints, DI. |
| `hubs(topK)` | Tipos más **centrales** (PageRank): por dónde empezar a entender un codebase. |
| `search_semantic(query, topK)` | Búsqueda semántica sin LLM (BM25) sobre nombre + summary + miembros + dependencias. |
| `stats()` | Tipos, aristas, endpoints, call-sites, bindings DI y ficheros indexados. |

### Ahorro de tokens

El patrón clave: en vez de `Read` de ficheros enteros para entender el código, usa
`find_call_sites` para localizar la invocación exacta y `get_source(tipo, miembro)`
para ver **solo ese método**. El grafo guarda la relación y la línea de cada arista,
modela MediatR/buses y el contenedor DI explícitamente, y devuelve código puntual —
de modo que el LLM rara vez necesita abrir un fichero completo.

---

## Arquitectura

```
Claude Code
    │  stdio (MCP)
    ▼
LocalGraph.exe
    ├── GraphTools       ← herramientas MCP (entrada/salida)
    ├── CodeGraph        ← grafo bidireccional en memoria
    └── SolutionScanner  ← escaneo Roslyn en paralelo
```

### Escaneo

`SolutionScanner` recorre todos los `.cs` del proyecto (excluyendo `obj/`, `bin/`, `.git/`,
`node_modules/` y ficheros generados) y los parsea en paralelo con Roslyn `CSharpSyntaxTree`.
No usa modelo semántico ni compilación: solo AST, lo que permite escanear cientos de ficheros
en menos de un segundo. Cada fichero produce un `FileFragment` aislado (sin locks), que el grafo
fusiona; esto habilita la **reconstrucción incremental por fichero**.

Por cada fichero, `TypeReferenceVisitor` extrae:
- **Nodos**: clases, interfaces, records, structs (con su span de líneas para `get_source`).
- **Aristas con relación y línea**: `ctor-param`, `field`, `property`, `new`, `inherits`,
  `implements`, `call` (invocación real), `sends`/`handled-by` (MediatR), `di-bound` (DI).
- **Call-sites**: invocaciones reales `_servicio.Metodo()` resueltas por el tipo del campo/var.
- **Endpoints**: métodos `[HttpGet]`/etc. (ruta **concatenada** con el `[Route]` de la clase) y
  **minimal APIs** `app.MapGet("/x", handler)`. Los parámetros tipados del lambda handler se
  resuelven, de modo que la traza llega hasta el endpoint minimal API.
- **Bindings DI**: `AddScoped<I,C>()`, `AddSingleton(typeof(I), typeof(C))`, etc.
- **Clases anidadas cualificadas**: `OuterClass.Handler` en lugar de `Handler`.

Genéricos contenedor (`Task`, `List`, `Func`…) y primitivos se filtran como nodos pero se
desciende a sus argumentos de tipo, evitando ruido en el grafo.

### Identidad de símbolo (colisiones de nombre)

Los tipos se indexan por **nombre cualificado** (`Sales.Order` vs `Purchasing.Order`), no por
nombre simple, así dos tipos homónimos en namespaces distintos no se fusionan. La resolución
es por AST (sin compilación): namespace del tipo declarante + `using` del fichero + tabla global
de símbolos, y ocurre en el **rebuild** (no en el parse), de modo que sigue siendo correcta bajo
escaneo incremental. La salida muestra el nombre simple salvo que colisione, donde cualifica.
Si consultas un nombre ambiguo, la herramienta te pide que lo cualifiques.

### Persistencia e incrementalidad

`GraphStore` cachea los fragmentos en disco (JSON, por solución, versionado por parser).
Al reabrir el proyecto, el arranque en frío es instantáneo: se cargan los fragmentos y solo
se re-parsean los ficheros con hash distinto. `ProjectWatcher` (FileSystemWatcher con debounce)
mantiene el grafo al día al guardar ficheros, re-parseando solo el fichero cambiado.

### Búsqueda semántica

`CodeGraph` construye un índice sparse (BM25) por tipo público combinando nombre, summary XML,
nombres de miembros y dependencias. Permite búsquedas por intención sin embeddings externos.

### Centralidad

En cada reconstrucción se calcula **PageRank** sobre las aristas. Sirve para ordenar
resultados por relevancia (en `search` y `find_callers`, en vez de alfabético) y alimenta
la herramienta `hubs`, que lista los tipos núcleo del sistema.

### El problema del diamante MediatR

```
Controller ──▶ CreateUserCommand ◀── CreateUserCommand.Handler
                                                │
                                                ▼
                                         IUserService
```

Buscando _"¿quién llama a IUserService?"_ hacia atrás, el DFS llega al `Handler` y muere: no hay callers reales de producción. Para resolverlo, el DFS implementa un **pivote**:

1. Al llegar a un dead-end, sigue las aristas hacia delante del nodo actual.
2. Si encuentra un tipo referenciado por 2 o más callers distintos (indicador de Command/Query compartido), pivota a él.
3. Continúa el recorrido hacia atrás desde ese tipo, llegando ahora al Controller.
4. Solo se permite un pivote por camino para evitar explosión combinatoria.

---

## Benchmark de tokens

¿Cuánto ahorra de verdad? En [`bench/`](bench/README.md) hay una batería reproducible que
compara, con cuenta exacta de tokens (`tiktoken`), **LocalGraph vs CodeGraph vs grep+lectura**
sobre tus propios repos. Resumen de la batería interna (2 repos .NET, 31 preguntas):

- **Navegar / localizar / resolver** (deps, DI, call-sites, endpoints, hubs): **~7× menos
  tokens que CodeGraph y ~16× menos que grep+lectura**.
- **Comprensión de flujo** (`flow`): **~45×** más barato que reconstruir la cadena leyendo ficheros.
- **Leer código completo**: paridad (`understand` gana en clases grandes). **Literales**: gana grep.

Cómo reproducirlo sobre tu código: ver [bench/README.md](bench/README.md).

---

## Desarrollo

```powershell
# Compilar y publicar (detiene el proceso en ejecucion si existe)
.\publish.ps1
```

Requiere .NET 10 SDK y Claude Code instalados.

---

## Instalación para usuarios finales

Ver [INSTALL.md](INSTALL.md).
