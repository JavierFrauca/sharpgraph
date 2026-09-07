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
        Title = "SharpGraph — grafo de código y documentación C#",
        Description = "Indexa proyectos C# en un grafo de dependencias y su documentación (.md/.txt/appsettings) en un índice BM25. Responde quién llama a quién, qué implementa cada interfaz (DI), desde qué endpoint HTTP se llega y cómo funciona un flujo — y devuelve código fuente puntual y secciones de documentación, todo en texto compacto para gastar los mínimos tokens.",
        Version = "2.1.0",
        WebsiteUrl = "https://github.com/JavierFrauca/sharpgraph",
    };
    options.ServerInstructions = """
        SharpGraph: mapa de dependencias de proyectos C# + índice de su documentación.
        Responde preguntas estructurales (quién llama a quién, DI, endpoints, flujo)
        y recupera código puntual SIN leer ficheros enteros: la tesis es gastar los
        mínimos tokens.

        == CUÁNDO USAR / CUÁNDO NO ==
        SÍ: dependencias y callers de un tipo · desde qué endpoint se llega · qué
        implementa una interfaz (DI) · dónde se invoca de verdad un método · cómo
        funciona un flujo · entender un tipo con contexto en 1 llamada · buscar
        tipos por intención · buscar en docs/ADRs/appsettings · el código de un
        solo método en vez del fichero entero.
        NO: texto literal en código (mejor grep) · proyectos que no son C# (solo se
        indexan .cs) · editar o ejecutar (solo lectura) · PDF/DOCX y ficheros de
        docs >1 MB (no se indexan).

        == PRIMERA VEZ ==
        stats() → si 0 tipos, scan(path). En Claude Code, configure_auto_scan() una
        vez: el hook CwdChanged escaneará solo al cambiar de proyecto. El grafo es
        persistente (caché en disco) e incremental.

        == FLUJO HABITUAL ==
        1. search("NombreParcial") → nombre exacto del tipo.
        2. ¿Quién depende de X?           → find_callers(X, depth)
        3. ¿Desde qué endpoint?           → trace_to_endpoints(X)
        4. ¿De qué depende X?             → get_usages(X)
        5. ¿DÓNDE SE LLAMA X de verdad?   → find_call_sites(X[, member])
        6. ¿Qué implementa la interfaz?   → resolve_di(IX)
        7. Ver el código de un método     → get_source(X, member)
        8. COMPRENDER un tipo (código+contexto en 1 llamada) → understand(X)
        9. ¿CÓMO FUNCIONA? (árbol de llamadas sin código) → flow(X, member)
        10. Buscar tipos por intención    → search_semantic("...")
        11. Buscar en docs/ADRs           → search_docs("...")

        == CLAVE PARA AHORRAR TOKENS ==
        find_call_sites para localizar la invocación + get_source(tipo, miembro)
        para ver SOLO ese método. Distingue "inyectado" (find_callers / [ctor-param])
        de "llamado de verdad" (find_call_sites / [call]). Los tipos marcados
        [docs:N] aparecen en documentación: understand(X) lista qué docs los
        mencionan.

        == LÍMITES ==
        - Indexado por nombre simple de tipo; los ambiguos se muestran como FQN.
        - Tipos externos/BCL solo como destino de aristas, no como nodos.
        - Tests, mocks, fakes, stubs y builders se filtran automáticamente.
        - El watcher mantiene el grafo y los docs al día al guardar; no re-escanear.
        """;
})
.WithStdioServerTransport()
.WithTools<GraphTools>();

await builder.Build().RunAsync();
return 0;
