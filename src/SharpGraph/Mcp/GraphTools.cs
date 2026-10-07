using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Scanner;
using SharpGraph.Watcher;
using ModelContextProtocol.Server;

namespace SharpGraph.Mcp;

[McpServerToolType]
public class GraphTools(GraphEngine graph, GraphStore store, ProjectWatcher watcher)
{
    [McpServerTool(Title = "Configurar auto-escaneo (solo Claude Code)", Idempotent = true), Description("""
        PRIMERA CONFIGURACIÓN — llama a esta herramienta la primera vez que usas SharpGraph
        CON Claude Code. (Solo aplica a Claude Code: otros clientes no soportan el hook
        CwdChanged y esta herramienta no tendrá efecto en ellos. Ver docs/CLIENTS.md.)

        Configura el escaneo automático de proyectos: edita el fichero
        ~/.claude/settings.json añadiendo un hook 'CwdChanged' que invoca scan()
        automáticamente cada vez que Claude Code cambia de directorio de trabajo.

        Tras ejecutarla, reinicia Claude Code. A partir de ese momento el grafo
        se construirá en segundo plano al abrir cualquier proyecto C#.

        Es idempotente: si el hook ya existe no hace ningún cambio.
        """)]
    public string ConfigureAutoScan()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

        // Si no estamos en Claude Code (no existe la carpeta ~/.claude y no hay settings),
        // no intentar escribir ciegamente: devolver aviso en vez de fallar o crear basura.
        var claudeDir = Directory.GetParent(settingsPath)?.FullName;
        if (claudeDir is not null && !Directory.Exists(claudeDir))
        {
            return "Esta herramienta configura el hook CwdChanged de Claude Code en " +
                   $"{settingsPath}, pero la carpeta {claudeDir} no existe: parece que " +
                   "no estás usando Claude Code. En otros clientes, registra el servidor " +
                   "MCP manualmente (ver docs/CLIENTS.md) y llama a scan(path) cuando " +
                   "necesites indexar un proyecto.";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        var raw = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
        var root = (JsonNode.Parse(raw) as JsonObject) ?? new JsonObject();

        if (root["hooks"] is not JsonObject hooksObj)
        {
            hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
        }
        if (hooksObj["CwdChanged"] is not JsonArray cwdArray)
        {
            cwdArray = new JsonArray();
            hooksObj["CwdChanged"] = cwdArray;
        }

        foreach (var item in cwdArray)
            if (item?["hooks"] is JsonArray inner)
                foreach (var h in inner)
                    if (h?["server"]?.GetValue<string>() == "sharpgraph")
                        return "El hook CwdChanged ya estaba configurado. No se ha realizado ningún cambio.";

        cwdArray.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mcp_tool",
                    ["server"] = "sharpgraph",
                    ["tool"] = "scan",
                    ["input"] = new JsonObject { ["path"] = "${cwd}" },
                    ["async"] = true,
                    ["statusMessage"] = "SharpGraph indexing..."
                }
            }
        });

        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return $"Hook configurado en {settingsPath}. Reinicia Claude Code para activarlo.";
    }

    [McpServerTool(Title = "Escanear proyecto C#", Idempotent = true), Description("""
        Escanea un proyecto C# y construye/actualiza el grafo de dependencias en memoria.

        Acepta .sln, .csproj o carpeta. Excluye obj/, bin/, .git/, node_modules/
        y ficheros generados (.g.cs, .Designer.cs).

        Es INCREMENTAL y PERSISTENTE:
          - Carga una caché en disco del último escaneo (arranque en frío instantáneo).
          - Solo re-parsea ficheros nuevos o modificados (hash de contenido).
          - Activa un watcher que mantiene el grafo al día al guardar ficheros.

        Devuelve estadísticas: tipos definidos, aristas, endpoints HTTP, call-sites
        (invocaciones reales) y bindings de DI detectados.

        Cuándo llamarlo:
          - Si stats() devuelve 0 tipos.
          - Al cambiar de proyecto (si no tienes el hook automático).
        """)]
    public async Task<string> Scan(
        [Description("Ruta al .sln, .csproj o carpeta raíz del proyecto C#")] string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return $"Path no encontrado: {path}";

        graph.Clear(path);
        if (store.TryLoad(path, out var cached))
        {
            graph.MergeFragments(cached);
            await Console.Error.WriteLineAsync($"Cache hit: {cached.Count} fragments loaded.");
        }

        var scanner = new SolutionScanner(graph);
        await scanner.ScanIncrementalAsync(path);

        store.Save(path, graph.Fragments());
        watcher.Watch(path);

        return graph.Stats();
    }

    [McpServerTool(Title = "Traza hasta endpoints HTTP", ReadOnly = true, Idempotent = true), Description("""
        Traza el camino desde un tipo hasta los endpoints HTTP que lo invocan.
        "¿Desde qué endpoint se llama a este servicio?".

        Navega el grafo hacia atrás hasta métodos [HttpGet]/[HttpPost]/etc.
        Las rutas incluyen el prefijo [Route("api/[controller]")] de la clase.

        Modela MediatR/buses de forma EXPLÍCITA: las aristas Controller -sends->
        Command -handled-by-> Handler hacen que la cadena sea exacta, no heurística.
        Cada camino se etiqueta:
          [direct] / [nearby]      → cadena estructural directa
          [exact-mediatr]          → pasa por un Send/Handler modelado (alta confianza)
          [heuristic]              → pivote por Command/Query compartido (verificar)

        Formato:
          [exact-mediatr] [POST /api/payroll/calc] PayrollController.Calc ← ⇒CalcCommand ← Handler ← IGrossService

        Devuelve hasta 15 caminos. Usa search() primero si no sabes el nombre exacto.
        """)]
    public string TraceToEndpoints(
        [Description("Nombre del tipo (ej: IGrossService). Sin namespace.")] string typeName,
        [Description("Profundidad máxima hacia atrás (defecto 8).")] int maxDepth = 8)
        => graph.TraceToEndpoints(typeName, maxDepth);

    [McpServerTool(Title = "Impacto de cambio (blast radius)", ReadOnly = true, Idempotent = true), Description("""
        ANTES de tocar un tipo: calcula el RADIO DE IMPACTO transitivo — todo lo
        que se rompe o hay que revisar si lo cambias. "¿Qué afecto si modifico X?".

        Recorre el grafo hacia arriba (quién depende de quién, transitivo) y lo
        destila en un RESUMEN POR NIVELES, no un árbol:
          - tipos afectados por nivel con la relación [ctor-param]/[call]/[sends]...
          - endpoints HTTP en riesgo (con verbo y ruta)
          - tests que ejercitan el área afectada

        Propaga DI: si cambias una IMPLEMENTACIÓN, el impacto sube a la interfaz
        que registra y de ahí a todos sus consumidores.

        Ejemplo:
          impact("Repo0")
            87 tipos en 6 niveles · 8 endpoints HTTP · 2 tests
            Nivel 1: IRepo0 [di-impl], Service0 [ctor-param]
            Nivel 2: Controller0 [sends] [ENDPOINT: POST /d001/op0]
            Endpoints en riesgo (8): [POST /d001/op0] Controller0.Op0 ...

        Para el árbol detallado usa find_callers; para el camino exacto a HTTP,
        trace_to_endpoints.
        """)]
    public string Impact(
        [Description("Tipo que se plantea cambiar (ej: GrossService, Repo0).")] string typeName,
        [Description("Profundidad máxima del análisis (1-10, defecto 6).")] int maxDepth = 6,
        [Description("Incluir tipos de test dentro de los niveles (siempre se listan aparte). Defecto false.")] bool includeTests = false)
        => graph.Impact(typeName, maxDepth, includeTests);

    [McpServerTool(Title = "Quién usa este tipo", ReadOnly = true, Idempotent = true), Description("""
        Árbol de tipos que dependen de un tipo dado, N niveles hacia arriba.
        "¿Qué partes del sistema usan este servicio?".

        Cada caller muestra la RELACIÓN concreta entre corchetes:
          [ctor-param] inyección por constructor   [call] invocación real
          [inherits]/[implements] herencia          [new] instanciación
          [field]/[property] miembro tipado         [sends]/[handled-by] MediatR

        Si el tipo es una interfaz con binding DI, añade la implementación resuelta.
        Tests, mocks, fakes, stubs y builders se filtran.

        depth: 1 = callers directos · 3 = red razonable (defecto) · 5+ = árbol profundo.
        """)]
    public string FindCallers(
        [Description("Nombre del tipo (ej: IGrossService). Sin namespace.")] string typeName,
        [Description("Niveles hacia arriba (defecto 3).")] int depth = 3)
        => graph.FindCallers(typeName, depth);

    [McpServerTool(Title = "De qué depende este tipo", ReadOnly = true, Idempotent = true), Description("""
        Muestra de qué tipos depende el tipo indicado (referencias salientes),
        agrupadas por destino con su relación y líneas.

        Formato:
          'GrossService' uses (12 types):
            → IGrossRepository [ctor-param] @L23 (used by 3)
            → GrossCalculator [new,call] @L40,55 (used by 1)
            → ILogger [ctor-param] @L21 (used by 47) (external)

        "(used by N)" indica popularidad; "(external)" = tipo no definido en la solución
        (BCL/NuGet). Útil para medir acoplamiento antes de refactorizar.
        """)]
    public string GetUsages(
        [Description("Nombre del tipo (ej: GrossService).")] string typeName)
        => graph.GetUsages(typeName);

    [McpServerTool(Title = "Buscar tipos por nombre", ReadOnly = true, Idempotent = true), Description("""
        Busca tipos por nombre parcial (insensible a mayúsculas). Solo tipos
        DEFINIDOS en la solución (los externos/BCL no son nodos).

        Formato:
          GrossService <class> [ENDPOINT] [GrossService.cs] (callers: 3, uses: 12)
          IGrossService <interface> [DI-SERVICE] [IGrossService.cs] (callers: 17, uses: 0)

        Indicadores: [ENDPOINT] tiene métodos HTTP · [DI-SERVICE] tiene binding DI ·
        <kind> = class/interface/record/struct.

        Úsalo SIEMPRE antes de las demás herramientas para confirmar el nombre exacto.
        """)]
    public string Search(
        [Description("Texto parcial a buscar.")] string pattern)
        => graph.Search(pattern);

    [McpServerTool(Title = "Explorar contexto cercano", ReadOnly = true, Idempotent = true), Description("""
        Explora el contexto cercano de un tipo o patrón en ambas direcciones,
        sin forzar una traza exacta hasta endpoint.

        Agrupa: endpoints directos, resolución DI, callers cercanos,
        dependencias cercanas y endpoints HTTP cercanos. Útil como descubrimiento
        general cuando search() da demasiado o trace_to_endpoints() es muy específico.
        """)]
    public string ExploreContext(
        [Description("Nombre exacto o texto parcial.")] string typeOrPattern,
        [Description("Profundidad bidireccional (defecto 2).")] int depth = 2,
        [Description("Máximo por bloque (defecto 8).")] int limitPerGroup = 8)
        => graph.ExploreContext(typeOrPattern, depth, limitPerGroup);

    [McpServerTool(Title = "Código fuente de un tipo o miembro", ReadOnly = true, Idempotent = true), Description("""
        Devuelve CÓDIGO FUENTE directamente desde el grafo, sin necesidad de leer
        ficheros enteros. Es la herramienta clave para ahorrar tokens: en vez de
        abrir un fichero de 400 líneas, recupera solo lo que necesitas.

        Tres modos:
          - get_source(typeName)               → esquema del tipo: summary + firmas de
                                                 todos sus miembros con sus líneas.
          - get_source(typeName, member)       → el cuerpo de ESE miembro, con números
                                                 de línea, truncado a maxBodyLines.
          - get_source(typeName, maxLines: N)  → el cuerpo COMPLETO del tipo truncado
                                                 a N líneas. Ideal para "enséñame esta
                                                 clase" sin el overhead de understand.

        Si pasas includeBodies=true, en vez de truncar por línea trunca por miembro:
        devuelve los cuerpos completos de los primeros miembros hasta agotar el
        presupuesto de líneas.

        Ejemplo de salida (modo miembro):
          // GrossService.cs:142  GrossService.CalculateGross
            142  public decimal CalculateGross(PayrollContext ctx) {
            143      var bruto = _repo.GetBase(ctx.EmployeeId);
            ...

        Flujo recomendado: find_call_sites/find_callers para localizar →
        get_source para ver solo el método relevante.
        """)]
    public string GetSource(
        [Description("Nombre del tipo (ej: GrossService).")] string typeName,
        [Description("Opcional: nombre del miembro/método. Si se omite y maxBodyLines=0, devuelve esquema.")] string? member = null,
        [Description("Máximo de líneas del cuerpo (defecto 60 para miembro, 200 para tipo completo).")] int maxBodyLines = 60,
        [Description("Si true y no hay member, devuelve cuerpos de los primeros miembros públicos hasta el presupuesto.")] bool includeBodies = false)
        => graph.GetSource(typeName, member, Math.Clamp(maxBodyLines, 5, 600), includeBodies);

    [McpServerTool(Title = "Leer un fichero .cs numerado", ReadOnly = true, Idempotent = true), Description("""
        Lee un fichero .cs del proyecto escaneado, con números de línea y marcas
        de región por tipo definido. Compite con `explore` de GraphEngine para el caso
        "enséñame este fichero". A diferencia de get_source/understand, devuelve el
        fichero entero (no un solo tipo), truncado a maxLines.

        Útil cuando necesitas ver el contexto alrededor de un tipo, imports, o
        múltiples tipos en el mismo fichero. Las líneas se anotan con el tipo
        definido cuando empieza un nuevo tipo.

        Ejemplo:
          // TodoItems.cs (120 líneas)
          // ── MyApp.CreateTodoItemCommand ──
            13  public class CreateTodoItemCommand : IRequest<int>
            14  {
            ...
          // ── MyApp.CreateTodoItemCommandHandler ──
            30  public class CreateTodoItemCommandHandler ...
        """)]
    public string ReadFile(
        [Description("Ruta del fichero .cs (relativa al proyecto o absoluta).")] string filePath,
        [Description("Máximo de líneas a devolver (10-800, defecto 200).")] int maxLines = 200)
        => graph.ReadFile(filePath, Math.Clamp(maxLines, 10, 800));

    [McpServerTool(Title = "Comprender un tipo (código + contexto)", ReadOnly = true, Idempotent = true), Description("""
        COMPRENDER un tipo de un vistazo, en UNA sola llamada. Pensada para "¿cómo
        funciona X?" / "enséñame la clase X completa y su rol en el sistema".

        Devuelve, sin volcar ficheros vecinos:
          - cabecera: kind, summary, binding DI, endpoints que lo alcanzan
          - contexto del grafo: 'used by' (quién lo usa) y 'uses' (de qué depende),
            con la relación de cada arista — el porqué, no solo el qué
          - el CÓDIGO FUENTE COMPLETO del tipo (todos sus miembros), acotado a
            bodyBudget líneas; si se trunca, lista las firmas de los miembros restantes.

        Frente a leer el fichero entero: aquí obtienes UN tipo (no los demás del
        fichero ni sus vecinos) MÁS el mapa de relaciones explícito. Es la forma
        compacta de entender una clase y su entorno gastando los mínimos tokens.

        Para solo un método concreto, usa get_source(tipo, miembro).
        """)]
    public string Understand(
        [Description("Nombre del tipo a comprender (ej: GrossService, IndexingPipeline).")] string typeName,
        [Description("Máximo de líneas de cuerpo a incluir (20-800, defecto 200).")] int bodyBudget = 200)
        => graph.Understand(typeName, bodyBudget);

    [McpServerTool(Title = "Árbol de llamadas salientes", ReadOnly = true, Idempotent = true), Description("""
        FLUJO de ejecución: destila la secuencia de llamadas SALIENTES de un método,
        recursiva hasta 'depth' niveles, con fichero:línea. Responde "¿cómo funciona /
        qué orquesta esto?" mostrando el árbol de llamadas SIN el código fuente.

        Es comprensión de flujo a una fracción de los tokens: cruza varios ficheros que
        tendrías que leer enteros para reconstruir mentalmente la misma cadena.

        Ejemplo:
          flow("OrderService","Place", depth:2)
            → _validator.Validate(order)        :58  [impl: OrderValidator]
            → _pricing.Calculate(order)         :74  [impl: PricingService]
                → _taxRepo.GetRate(...)         :138
            → _repository.Save(order)           :86  [impl: OrderRepository]

        Sin 'member': muestra las llamadas de cada método público (1 nivel).
        Para ver el CUERPO de un método usa get_source; para tipo+contexto, understand.
        """)]
    public string Flow(
        [Description("Tipo de origen (ej: OrderService).")] string typeName,
        [Description("Método cuyo flujo trazar. Si se omite, resume todos los públicos a 1 nivel.")] string? member = null,
        [Description("Profundidad de recursión (1-5, defecto 2).")] int depth = 2)
        => graph.Flow(typeName, member, depth);

    [McpServerTool(Title = "Dónde se invoca de verdad", ReadOnly = true, Idempotent = true), Description("""
        Muestra DÓNDE SE INVOCA REALMENTE un tipo o método, a nivel de método y
        con fichero:línea. Resuelve la diferencia entre "dependencia inyectada" y
        "llamada efectiva": solo lista invocaciones reales (_servicio.Metodo()).

        Formato:
          GrossService.CalculateGross()  ←  PayrollHandler.Handle  @ PayrollHandler.cs:142
          GrossService.GetBase()         ←  IrpfService.Compute     @ IrpfService.cs:88

        Pasa 'member' para filtrar por un método concreto. Esta es la respuesta
        operativa accionable que antes obligaba a leer varios ficheros.
        """)]
    public string FindCallSites(
        [Description("Nombre del tipo cuyo uso real buscas (ej: GrossService).")] string typeName,
        [Description("Opcional: método concreto a filtrar.")] string? member = null,
        [Description("Máximo de resultados (defecto 50).")] int limit = 50)
        => graph.FindCallSites(typeName, member, limit);

    [McpServerTool(Title = "Resolver inyección de dependencias", ReadOnly = true, Idempotent = true), Description("""
        Resuelve la inyección de dependencias: dado un servicio o una implementación,
        muestra el binding registrado (AddScoped/AddSingleton/AddTransient).

        Evita tener que leer Program.cs / Startup.cs / módulos DI a mano:
          resolve_di("IGrossService")  → IGrossService → GrossService [scoped]
          resolve_di("GrossService")   → GrossService ← IGrossService [scoped]

        Cubre genéricos AddScoped<I,C>() y la forma AddScoped(typeof(I), typeof(C)).
        """)]
    public string ResolveDi(
        [Description("Nombre del servicio o implementación (ej: IGrossService).")] string typeName)
        => graph.ResolveDi(typeName);

    [McpServerTool(Title = "Búsqueda semántica de tipos", ReadOnly = true, Idempotent = true), Description("""
        Búsqueda semántica (sin LLM, BM25 en memoria) sobre los tipos públicos:
        combina nombre, summary XML, nombres de miembros y dependencias.
        Encuentra tipos por INTENCIÓN aunque no sepas el nombre exacto.

        Ej: search_semantic("cálculo de retención irpf nómina") → tipos relevantes
        ordenados por score, con su summary.
        """)]
    public string SearchSemantic(
        [Description("Texto de búsqueda por intención.")] string query,
        [Description("Número de resultados (1-30, defecto 10).")] int topK = 10)
        => graph.SearchSemantic(query, topK);

    [McpServerTool(Title = "Tipos más centrales del sistema", ReadOnly = true, Idempotent = true), Description("""
        Lista los tipos MÁS CENTRALES del sistema por PageRank: los nodos núcleo
        por los que pasa la arquitectura. Punto de partida ideal para entender un
        codebase desconocido SIN leer ficheros a ciegas.

        Formato:
           1. IGrossService [IGrossService.cs] (callers: 17)
           2. PayrollController [ENDPOINT] [PayrollController.cs] (callers: 0)
           ...

        Por defecto solo tipos de la solución; incluye 'includeExternal' para ver
        también la infraestructura transversal (ILogger, IMediator…).
        """)]
    public string Hubs(
        [Description("Número de tipos a devolver (1-50, defecto 15).")] int topK = 15,
        [Description("Incluir tipos externos/BCL (infraestructura). Defecto false.")] bool includeExternal = false)
        => graph.Hubs(topK, includeExternal);

    [McpServerTool(Title = "Buscar literales de cadena", ReadOnly = true, Idempotent = true), Description("""
        ¿Dónde está este string/texto en el código C#? Busca en los LITERALES DE
        CADENA indexados (plain, verbatim y const) y devuelve file:line + el tipo
        que lo contiene — sin leer ficheros, con la salida acotada a 'limit'.

        Es el grep de SharpGraph: sustituye a grep/ripgrep para buscar strings,
        mensajes, claves de configuración o rutas DENTRO de los .cs del proyecto.

        Ejemplo:
          search_literals("Todo Lists")
            TodoListsController.cs:42  "Todo Lists"  [TodoListsController]

        caseSensitive para distinguir mayúsculas (defecto: insensible).
        Nota: solo literales de .cs; para buscar en otros ficheros (docs, configs
        no JSON indexados), grep sigue siendo la herramienta.
        """)]
    public string SearchLiterals(
        [Description("Texto a buscar dentro de los string literals.")] string pattern,
        [Description("Máximo de resultados (1-100, defecto 30).")] int limit = 30,
        [Description("Sensible a mayúsculas (defecto false).")] bool caseSensitive = false)
        => graph.SearchLiterals(pattern, limit, caseSensitive);

    [McpServerTool(Title = "Diagrama Mermaid bidireccional", ReadOnly = true, Idempotent = true), Description("""
        DIAGRAMA de contexto en Mermaid desde un tipo ancla, en UNA llamada: la cadena
        de llamadores hacia arriba (hasta los endpoints HTTP) y el árbol de
        dependencias hacia abajo, con la relación de cada flecha. Devuelve un bloque
        ```mermaid``` pegable tal cual en Markdown (GitHub, Obsidian, mermaid.live lo
        renderizan nativamente) — la forma barata de VER lo que el grafo describe.

        "Enséñame gráficamente todo lo que toco desde aquí":
          mermaid_context("CreateOrderCommandHandler")
            → POST /api/orders --> OrdersController -.->|sends| CreateOrderCommand
              -.->|handled-by| Handler (ancla, resaltada) -->|ctor-param| IOrderRepository
              -.->|di-bound| OrderRepository

        Los parámetros nombran SEMÁNTICA, no orientación visual:
          callersDepth — niveles hacia los llamadores (0 desactiva; máx 6, defecto 3).
          depsDepth    — niveles hacia las dependencias (0 desactiva; máx 6, defecto 3).
        direction controla la orientación del dibujo: "TD" (defecto) o "LR".

        Recorta ruido: tests/mocks fuera, ILogger y demás BCL fuera (includeExternal
        para verlos), ParamType/ReturnType excluidos, y tope duro de maxNodes
        (defecto 40) con aviso de truncado dentro del propio bloque.

        Para documentación: pega el bloque tal cual en docs/**/*.md. Para la cadena
        paso a paso usa mermaid_sequence; para el mapa general de la app,
        mermaid_overview.
        """)]
    public string MermaidContext(
        [Description("Nombre del tipo ancla (ej: CreateOrderCommandHandler).")] string typeName,
        [Description("Niveles hacia los llamadores (0-6, defecto 3).")] int callersDepth = 3,
        [Description("Niveles hacia las dependencias (0-6, defecto 3).")] int depsDepth = 3,
        [Description("Tope de nodos del diagrama (5-100, defecto 40).")] int maxNodes = 40,
        [Description("Orientación del diagrama: TD (defecto) o LR.")] string direction = "TD",
        [Description("Incluir tipos externos/BCL (ILogger, IMediator…). Defecto false.")] bool includeExternal = false)
        => graph.MermaidContext(typeName, callersDepth, depsDepth, maxNodes, direction, includeExternal);

    [McpServerTool(Title = "Diagrama de secuencia ancla → endpoint", ReadOnly = true, Idempotent = true), Description("""
        SEQUENCIADIAGRAM Mermaid de lo que pasa desde un tipo hasta los endpoints HTTP
        que lo invocan: cada cadena estructural (Controller -sends-> Command
        -handled-by-> Handler -...-> ancla) dibujada en orden de ejecución, seguida de
        las llamadas salientes del ancla a nivel de método. Devuelve un bloque
        ```mermaid``` pegable en Markdown.

        "Enséñame el RECORRIDO completo de esta petición":
          mermaid_sequence("CreateOrderCommandHandler")
            → participant POST /api/orders ... → Controller->>Command: sends
              → Command-->>Handler: handled-by → Handler->>IOrderRepository: Create()

        Solo aristas reales del grafo (sin heurísticos). Cadena corta primero; hasta
        maxPaths cadenas. Si el ancla no llega a ningún endpoint, dibuja igualmente
        sus llamadas salientes. Para el grafo completo (no secuencial) usa
        mermaid_context; para rutas con heurísticos, trace_to_endpoints.
        """)]
    public string MermaidSequence(
        [Description("Nombre del tipo ancla (ej: CreateOrderCommandHandler).")] string typeName,
        [Description("Método del ancla cuyas llamadas salientes mostrar; si se omite, los públicos.")] string? member = null,
        [Description("Máximo de cadenas a endpoint (1-8, defecto 3).")] int maxPaths = 3,
        [Description("Profundidad máxima de cada cadena (1-12, defecto 8).")] int maxDepth = 8)
        => graph.MermaidSequence(typeName, member, maxPaths, maxDepth);

    [McpServerTool(Title = "Mapa de arquitectura Mermaid", ReadOnly = true, Idempotent = true), Description("""
        DIAGRAMA de arquitectura en Mermaid: BFS multi-fuente desde todos los endpoints
        HTTP (o los de un área) hacia sus dependencias, agrupado en subgraphs por
        namespace. Devuelve un bloque ```mermaid``` pegable — el de cabecera para
        docs/architecture/overview.md.

        mermaid_overview()              → mapa completo de la app
        mermaid_overview("Billing")     → solo endpoints de Billing (tipo o namespace)
        mermaid_overview(depsDepth: 3)  → mapa más profundo

        Sin endpoints indexados (librerías) usa el top-5 por PageRank como semillas y
        lo avisa en el propio bloque. Solo tipos de la solución (BCL fuera) y tope
        duro de maxNodes (defecto 60) con aviso de truncado. Para el detalle desde un
        tipo concreto usa mermaid_context.
        """)]
    public string MermaidOverview(
        [Description("Filtra endpoints por substring de tipo o namespace (ej: \"Billing\").")] string? area = null,
        [Description("Profundidad de dependencias (1-5, defecto 2).")] int depsDepth = 2,
        [Description("Tope de nodos del diagrama (10-150, defecto 60).")] int maxNodes = 60)
        => graph.MermaidOverview(area, depsDepth, maxNodes);

    [McpServerTool(Title = "Catálogo de endpoints HTTP", ReadOnly = true, Idempotent = true), Description("""
        Lista TODOS los endpoints HTTP indexados (controllers + minimal APIs) en JSON:
        controlador, verbo, ruta, método y file:line. Es el catálogo para elegir el
        endpoint exacto que luego dibujas con endpoint_flow.

        Formato:
          { "count": 23, "endpoints": [ { "controller": "…OrdersController",
            "verb": "POST", "route": "/api/orders", "method": "Create",
            "file": "…/OrdersController.cs", "line": 58 } ] }

        También alimenta el árbol de la extensión SharpGraph Flow (VS Code).
        """)]
    public string ListEndpoints() => graph.ListEndpoints();

    [McpServerTool(Title = "Diagrama descendente desde un endpoint", ReadOnly = true, Idempotent = true), Description("""
        SUBGRAFO DESCENDENTE desde un endpoint HTTP en JSON estructurado:
        controller → command/query (MediatR) → handler → servicios → implementaciones
        DI. Es el "qué toca este endpoint de arriba abajo" para humanos (extensión
        SharpGraph Flow) y para máquinas.

        Acepta tres formatos de entrada:
          endpoint_flow("POST /api/orders")   verbo + ruta
          endpoint_flow("/api/orders")        ruta (exacta o substring, máx 5)
          endpoint_flow("OrdersController")   controlador (todas sus rutas)

        Cada NODO lleva: id (N0..Nn), kind (endpoint/controller/command/query/handler/
        interface/implementation/validator/class/infra), name, fqn, file y line —
        abre el fichero en esa línea con click. Las dependencias externas
        (ILogger, IMapper…) entran marcadas con infra=true: el cliente decide si
        las muestra. Cada ARISTA lleva relation (call/sends/handled-by/di-bound…),
        la línea de la primera referencia y back=true cuando su destino YA estaba
        dibujado (dependencia compartida o ciclo): no se re-expande, no hay explosión.

        Incluye además el bloque ```mermaid``` equivalente (clave "mermaid") para
        pegar en docs. Para texto narrativo usa flow(); para arriba-hacia-endpoints,
        trace_to_endpoints.
        """)]
    public string EndpointFlow(
        [Description("Endpoint: \"POST /api/orders\", \"/api/orders\" o nombre del controlador.")] string endpoint,
        [Description("Profundidad máxima del BFS descendente (1-12, defecto 8). Nivel 1 = mediator (command/query), nivel 2 = dependencias de primer nivel, nivel 3 = dependencias de las dependencias…")] int maxDepth = 8,
        [Description("Tope de nodos del subgrafo (5-200, defecto 80).")] int maxNodes = 80,
        [Description("Incluir contratos de entrada/salida (DTOs: parámetros y retornos de métodos como nodos hoja kind=dto). Defecto false.")] bool includeDtos = false)
        => graph.EndpointFlow(endpoint, maxDepth, maxNodes, includeDtos);

    [McpServerTool(Title = "Estadísticas del grafo", ReadOnly = true, Idempotent = true), Description("""
        Estadísticas del grafo: tipos definidos, aristas, endpoints HTTP, call-sites
        (invocaciones reales), bindings DI, ficheros y ruta actual.
        0 tipos = grafo vacío → llama a scan().
        Si hay una versión nueva de SharpGraph publicada, la última línea del
        resultado lo avisa: comunícaselo al usuario.
        """)]
    public string Stats()
    {
        // El refresco de versión va en background (no paga la latencia de la tool)
        // y el aviso se lee SOLO de la caché: stats() nunca bloquea por red.
        SharpGraph.Update.UpdateChecker.RefreshInBackground();
        return graph.Stats() + SharpGraph.Update.UpdateChecker.CachedNotice();
    }
}
