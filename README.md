# LocalGraph

**Servidor MCP que indexa proyectos C# (.NET) en un grafo de dependencias y lo expone a LLMs para navegar código sin leer ficheros enteros.** Token-efficient navigation of .NET codebases: MediatR/CQRS, DI, ASP.NET Core routing.

- **Lenguaje:** C# únicamente (vía Roslyn AST).
- **Tesis:** minimizar tokens. El agente navega por relaciones (callers, DI, endpoints, flujo de llamadas) usando metadatos compactos en vez de volcar ficheros enteros.
- **Clientes:** cualquiera que hable MCP por stdio (Claude Code, Cursor, Cline, Continue, Zed…). Ver [`docs/CLIENTS.md`](docs/CLIENTS.md).

Permite responder preguntas como _"¿desde qué endpoint se llama a este servicio?"_ en milisegundos y sin abrir decenas de ficheros.

---

## Quickstart (5 minutos)

1. **Descarga el binario** para tu plataforma desde la última [Release](https://github.com/JavierFrauca/localgraph/releases):
   - Windows: `LocalGraph-win-x64.zip`
   - macOS (Apple Silicon): `LocalGraph-osx-arm64.tar.gz`
   - Linux: `LocalGraph-linux-x64.tar.gz`
2. **Descomprime** en cualquier carpeta.
3. **Registra el servidor MCP** en tu cliente (elige uno):
   - **Claude Code** (Windows): `.\install.ps1`
   - **Claude Code** (macOS/Linux): `./install.sh`
   - **Otros clientes** (Cursor, Cline, Continue, Zed): pasa `-Client <cursor|cline|...>` al instalador, o sigue [`docs/CLIENTS.md`](docs/CLIENTS.md) para registrarlo a mano.
4. **Reinicia tu cliente MCP** y abre un proyecto C#.
5. **Pídele al LLM**:
   ```
   scan this C# project
   ```
   Y a continuación:
   ```
   trace to endpoints from IApplicationDbContext
   ```
   o
   ```
   show me the flow of TodoService.CreateAsync
   ```

Si `scan()` devuelve 0 tipos, verifica que apuntas a una carpeta con `.cs` (las carpetas `obj/`, `bin/` se ignoran automáticamente).

¿No te convence? Mira la [comparativa](docs/COMPARATIVA.md) con CodeGraph, Sourcegraph MCP y code-graph-mcp, el [benchmark de tokens](docs/BENCHMARK.md), o la demo:

```powershell
# Windows PowerShell
.\demo.ps1
```
```bash
# macOS / Linux
./demo.sh
```

La demo escanea [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture) y responde 5 preguntas en segundos.

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

## Limitaciones conocidas

LocalGraph parsea con **Roslyn AST sin modelo semántico** (sin compilar). Es una decisión
consciente para mantener escaneo paralelo y arranque instantáneo (~250 ms para proyectos
medianos), a costa de precisión en construcciones que requieren resolver tipos por compilación.

### Patrones de call-site que SÍ se resuelven

La detección de invocaciones (`find_call_sites`) cubre, además del caso básico
`_svc.Metodo()`, los siguientes patrones comunes en código real:

- ✅ **Null-conditional**: `_svc?.Metodo()`
- ✅ **Factory / chaining**: `_factory.Get().Metodo()`, `a.B().C().M()`
- ✅ **Member-access profundo**: `_outer.Inner.Metodo()`
- ✅ **var con await no genérico**: `var svc = await _factory.GetAsync(); svc.Metodo()`
- ✅ **Lambdas**: `() => svc.Metodo()`, `Task.Run(() => svc.Metodo())`

La resolución de receptores complejos se hace en **dos pasadas**: el visitor
serializa la expresión del receptor como una secuencia de pasos (`Local`,
`MethodReturn`, `PropertyAccess`) y el grafo la resuelve en el rebuild usando
una tabla de signaturas de retorno indexada por `(tipo, miembro)`.

### Lo que NO se resuelve (trade-off AST)

- **Sobrecargas de métodos** no se distinguen (se enlaza por nombre).
- **Métodos de extensión**: no se resuelven como aristas de llamada reales.
- **Indexer receiver** (`_map[key].M()`): requiere resolver el tipo del elemento
  del contenedor genérico (`Dictionary<K, V>` → `V`), fuera de alcance sin modelo
  semántico. Usar `find_callers` como aproximación estructural.
- **Top-level statements** en `Program.cs` sin clase envolvente: el visitor no
  mantiene tabla de locals fuera de un tipo contenedor, así que `LookupLocal`
  devuelve null. Requeriría tabla de locals a nivel fragmento.
- **DI chaining sobre tipos externos** (`host.Services.GetRequiredService<T>()`):
  los tipos BCL/NuGet (`IHost`, `IServiceProvider`) no están en el grafo y sus
  signaturas de retorno no se conocen.
- **Lambdas complejas / `dynamic` / reflexión** arbitrarias.
- **Routing**: la sustitución del token `[controller]` no se lowercasesa como hace
  ASP.NET en runtime (`PayrollController` → `Payroll`, no `payroll`).

Cuando la precisión AST no alcance, la salida de las herramientas (líneas, callers,
callees) sigue siendo una guía válida; y `get_source(tipo, miembro)` recupera el
código real para inspección manual puntual sin necesidad de aristas perfectas.

---

## Benchmark de tokens

¿Cuánto ahorra de verdad? En [`docs/BENCHMARK.md`](docs/BENCHMARK.md) hay una batería reproducible que
compara, con cuenta exacta de tokens (`tiktoken`), **LocalGraph vs CodeGraph vs grep+lectura**
sobre tus propios repos. Resumen de la batería interna (2 repos .NET, 31 preguntas):

- **Navegar / localizar / resolver** (deps, DI, call-sites, endpoints, hubs): **~7× menos
  tokens que CodeGraph y ~16× menos que grep+lectura**.
- **Comprensión de flujo** (`flow`): **~45×** más barato que reconstruir la cadena leyendo ficheros.
- **Leer código completo**: paridad (`understand` gana en clases grandes). **Literales**: gana grep.

Cómo reproducirlo sobre tu código: ver [docs/BENCHMARK.md](docs/BENCHMARK.md).

---

## Desarrollo

```powershell
# Compilar y publicar para win-x64 (atóajo local; detiene el proceso en ejecucion si existe)
.\publish.ps1

# Compilar para las 3 plataformas soportadas y empaquetar (dist/LocalGraph-<rid>.{zip,tar.gz})
.\publish-all.ps1

# Ejecutar los tests
dotnet test
```

Requiere .NET 10 SDK. Para auto-escaneo al cambiar de proyecto, Claude Code instalado.

---

## Instalación para usuarios finales

- **Windows**: `install.ps1` (ver [docs/INSTALL.md](docs/INSTALL.md))
- **macOS / Linux**: `install.sh`
- **Registro en cada cliente MCP** (Claude Code, Cursor, Cline, Continue, Zed…): [docs/CLIENTS.md](docs/CLIENTS.md)
