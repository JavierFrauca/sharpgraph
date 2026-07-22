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
public class GraphTools(CodeGraph graph, GraphStore store, ProjectWatcher watcher)
{
    [McpServerTool, Description("""
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
                    ["tool"] = "Scan",
                    ["input"] = new JsonObject { ["path"] = "${cwd}" },
                    ["async"] = true,
                    ["statusMessage"] = "SharpGraph indexing..."
                }
            }
        });

        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return $"Hook configurado en {settingsPath}. Reinicia Claude Code para activarlo.";
    }

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
        Lee un fichero .cs del proyecto escaneado, con números de línea y marcas
        de región por tipo definido. Compite con `explore` de CodeGraph para el caso
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
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

    [McpServerTool, Description("""
        Estadísticas del grafo: tipos definidos, aristas, endpoints HTTP, call-sites
        (invocaciones reales), bindings DI, ficheros indexados y ruta actual.
        0 tipos = grafo vacío → llama a scan().
        """)]
    public string Stats() => graph.Stats();
}
