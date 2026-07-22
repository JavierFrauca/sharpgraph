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
    options.ServerInfo = new() { Name = "SharpGraph", Version = "2.1.0" };
    options.ServerInstructions = """
        SharpGraph indexa proyectos C# en un grafo de dependencias y permite
        navegarlo SIN leer ficheros de código fuente — y ahora también recuperar
        código fuente puntual para ahorrar tokens.

        == PRIMERA VEZ ==
        Llama a configure_auto_scan() una vez para activar el escaneo automático
        al cambiar de proyecto. El grafo es persistente (caché en disco) e incremental.

        == FLUJO HABITUAL ==
        1. stats() → si 0 tipos, scan(path).
        2. search("NombreParcial") → nombre exacto del tipo.
        3. ¿Quién depende de X?           → find_callers(X, depth)
        4. ¿Desde qué endpoint?           → trace_to_endpoints(X)
        5. ¿De qué depende X?             → get_usages(X)
        6. ¿DÓNDE SE LLAMA X de verdad?   → find_call_sites(X[, member])
        7. ¿Qué implementa la interfaz?   → resolve_di(IX)
        8. Ver el código de un método     → get_source(X, member)
        9. COMPRENDER un tipo (código+contexto en 1 llamada) → understand(X)
        10. ¿CÓMO FUNCIONA? (árbol de llamadas sin código) → flow(X, member)
        11. Buscar por intención          → search_semantic("...")

        == CLAVE PARA AHORRAR TOKENS ==
        En vez de Read de ficheros enteros: usa find_call_sites para localizar la
        invocación y get_source(tipo, miembro) para ver SOLO ese método. Distingue
        siempre "inyectado" (find_callers / [ctor-param]) de "llamado de verdad"
        (find_call_sites / [call]).

        == NOTAS ==
        - El grafo se indexa por nombre simple de tipo (sin namespace).
        - Tests, mocks, fakes, stubs y builders se filtran automáticamente.
        - El watcher actualiza el grafo al guardar ficheros; no hace falta re-escanear.
        """;
})
.WithStdioServerTransport()
.WithTools<GraphTools>();

await builder.Build().RunAsync();
return 0;
