using SharpGraph.Cli;
using SharpGraph.Graph;
using SharpGraph.Mcp;
using SharpGraph.Persistence;
using SharpGraph.Scanner;
using SharpGraph.Watcher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var graph = new GraphEngine();
var store = new GraphStore();
var watcher = new ProjectWatcher(graph, store);

// ── Bifurcación CLI vs MCP ──────────────────────────────────────────────────
// Si el primer argumento es un subcomando CLI conocido (scan, stats, callers,
// setup, help, etc.), ejecutamos modo CLI y salimos. Si no, arrancamos modo
// MCP (stdio server para LLMs) como siempre.
if (args.Length > 0 && CliDispatcher.IsCliCommand(args[0]))
{
    Environment.Exit(await CliDispatcher.Run(args, graph, store, watcher));
}

// ── Modo MCP ────────────────────────────────────────────────────────────────
var path = args.FirstOrDefault();
if (path is not null)
{
    if (!File.Exists(path) && !Directory.Exists(path))
        await Console.Error.WriteLineAsync($"Warning: path not found: {path}");
    else
    {
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
    }
}
else
    await Console.Error.WriteLineAsync("SharpGraph ready (no path). Call scan() to index a project.");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(graph);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(watcher);
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "SharpGraph",
        Title = "SharpGraph — grafo de código C#",
        Description = "Indexa proyectos C# en un grafo de dependencias. Responde quién llama a quién, qué implementa cada interfaz (DI), desde qué endpoint HTTP se llega y cómo funciona un flujo — y devuelve código fuente puntual, todo en texto compacto para gastar los mínimos tokens.",
        Version = "2.3.0",
        WebsiteUrl = "https://github.com/JavierFrauca/sharpgraph",
    };
    options.ServerInstructions = """
        SharpGraph: mapa de dependencias de proyectos C#.
        Responde preguntas estructurales (quién llama a quién, DI, endpoints, flujo)
        y recupera código puntual SIN leer ficheros enteros: la tesis es gastar los
        mínimos tokens.

        == CUÁNDO USAR / CUÁNDO NO ==
        SÍ: dependencias y callers de un tipo · desde qué endpoint se llega · qué
        implementa una interfaz (DI) · dónde se invoca de verdad un método · cómo
        funciona un flujo · entender un tipo con contexto en 1 llamada · buscar
        tipos por intención · el código de un solo método en vez del fichero entero ·
        qué rompes si cambias un tipo (impact) · buscar strings/literales en los .cs
        (search_literals, sustituye a grep dentro del código C#) · VER el grafo:
        diagramas Mermaid pegables en Markdown (mermaid_context de un tipo,
        mermaid_sequence hasta el endpoint, mermaid_overview de la app).
        NO: buscar texto en ficheros que NO son .cs (docs, configs): grep ·
        proyectos que no son C# (solo se indexan .cs) · editar o ejecutar (solo
        lectura) · búsqueda en documentación (.md, ADRs, PDF/DOCX): fuera de
        alcance, solo se indexa código C#.

        == PRIMERA VEZ ==
        stats() → si 0 tipos, scan(path). En Claude Code, configure_auto_scan() una
        vez: el hook CwdChanged escaneará solo al cambiar de proyecto. El grafo es
        persistente (caché en disco) e incremental.

        == FLUJO HABITUAL ==
        1. search("NombreParcial") → nombre exacto del tipo.
        2. ¿Quién depende de X?           → find_callers(X, depth)
        3. ¿QUÉ ROMPO SI CAMBIO X?        → impact(X)  (transitivo: tipos+endpoints+tests)
        4. ¿Desde qué endpoint?           → trace_to_endpoints(X)
        5. ¿De qué depende X?             → get_usages(X)
        6. ¿DÓNDE SE LLAMA X de verdad?   → find_call_sites(X[, member])
        7. ¿Qué implementa la interfaz?   → resolve_di(IX)
        8. Ver el código de un método     → get_source(X, member)
        9. COMPRENDER un tipo (código+contexto en 1 llamada) → understand(X)
        10. ¿CÓMO FUNCIONA? (árbol de llamadas sin código) → flow(X, member)
        11. Buscar tipos por intención    → search_semantic("...")
        12. ¿CÓMO SE VE ESTO? (para el humano / para docs) → mermaid_context(X),
            mermaid_sequence(X[, member]) o mermaid_overview([area])

        == CLAVE PARA AHORRAR TOKENS ==
        find_call_sites para localizar la invocación + get_source(tipo, miembro)
        para ver SOLO ese método. Distingue "inyectado" (find_callers / [ctor-param])
        de "llamado de verdad" (find_call_sites / [call]).

        == LÍMITES ==
        - Indexado por nombre simple de tipo; los ambiguos se muestran como FQN.
        - Tipos externos/BCL solo como destino de aristas, no como nodos.
        - Tests, mocks, fakes, stubs y builders se filtran automáticamente.
        - El watcher mantiene el grafo al día al guardar; no re-escanear.
        """;
})
.WithStdioServerTransport()
.WithTools<GraphTools>();

await builder.Build().RunAsync();
return 0;
