using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Scanner;
using SharpGraph.Watcher;

namespace SharpGraph.Cli;

/// <summary>
/// Punto de entrada de la CLI. Recibe los args, identifica el subcomando,
/// parsea flags simples (-d, -m, -l, -n, --client, etc.) y delega al
/// método correspondiente de <see cref="CliCommands"/>.
/// </summary>
internal static class CliDispatcher
{
    /// <summary>
    /// Comandos CLI reconocidos. Si args[0] está aquí, se ejecuta modo CLI.
    /// Si no, se cae al modo MCP.
    /// </summary>
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "scan", "stats", "search", "callers", "find-callers",
        "usages", "get-usages", "callsites", "find-callsites",
        "trace", "trace-to-endpoints", "impact", "flow", "hubs",
        "di", "resolve-di", "source", "get-source", "understand",
        "read-file", "read-file", "readfile", "semantic",
        "search-semantic", "literals", "search-literals", "explore",
        "explore-context", "mermaid", "mermaid-context", "mermaid-seq",
        "mermaid-sequence", "mermaid-overview", "update", "self-update",
        "setup", "help",
    };

    public static bool IsCliCommand(string arg)
        => Commands.Contains(arg);

    public static async Task<int> Run(string[] args, GraphEngine graph, GraphStore store, ProjectWatcher watcher)
    {
        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        // El grafo necesita un path cargado para casi todo excepto scan/help/setup/update.
        // Si no hay path cargado y el comando lo necesita, intentamos cargar el cwd.
        if (cmd is not ("scan" or "setup" or "help" or "update"))
        {
            EnsureGraphLoaded(graph, store, watcher);
        }

        // Aviso de versión nueva (cacheado 24 h, fail-silent, opt-out con
        // SHARPGRAPH_NO_UPDATE_CHECK). 'update' hace su propio check con force;
        // 'setup' es un menú interactivo donde el ruido estorba.
        if (cmd is not ("update" or "setup"))
        {
            SharpGraph.Update.UpdateChecker.PrintNoticeToConsole();
        }

        return cmd switch
        {
            "scan" => await CliCommands.Scan(rest, graph, store, watcher),
            "stats" => CliCommands.Stats(graph),
            "search" => CliCommands.Search(rest, graph),
            "callers" or "find-callers" => CliCommands.Callers(rest, graph),
            "usages" or "get-usages" => CliCommands.Usages(rest, graph),
            "callsites" or "find-callsites" => CliCommands.Callsites(rest, graph),
            "trace" or "trace-to-endpoints" => CliCommands.Trace(rest, graph),
            "impact" => CliCommands.Impact(rest, graph),
            "flow" => CliCommands.Flow(rest, graph),
            "hubs" => CliCommands.Hubs(rest, graph),
            "di" or "resolve-di" => CliCommands.Di(rest, graph),
            "source" or "get-source" => CliCommands.Source(rest, graph),
            "understand" => CliCommands.Understand(rest, graph),
            "read-file" or "readfile" => CliCommands.ReadFile(rest, graph),
            "semantic" or "search-semantic" => CliCommands.Semantic(rest, graph),
            "literals" or "search-literals" => CliCommands.Literals(rest, graph),
            "explore" or "explore-context" => CliCommands.Explore(rest, graph),
            "mermaid" or "mermaid-context" => CliCommands.Mermaid(rest, graph),
            "mermaid-seq" or "mermaid-sequence" => CliCommands.MermaidSeq(rest, graph),
            "mermaid-overview" => CliCommands.MermaidOverview(rest, graph),
            "update" or "self-update" => await CliCommands.Update(rest),
            "setup" => await SetupWizard.Run(rest),
            "help" => CliCommands.Help(rest),
            _ => PrintUnknown(cmd),
        };
    }

    /// <summary>
    /// Si el grafo está vacío pero hay caché para el directorio actual, la carga
    /// y responde tal cual: los comandos de una sola consulta NO re-hashean el
    /// proyecto (contrato tipo `codegraph status` — su sync también es explícito).
    /// `sharpgraph scan` es el sync explícito; el watcher en caliente cubre el
    /// modo MCP. Sin caché: escaneo completo (primera vez).
    /// </summary>
    private static void EnsureGraphLoaded(GraphEngine graph, GraphStore store, ProjectWatcher watcher)
    {
        if (graph.NodeCount > 0) return;

        var cwd = Directory.GetCurrentDirectory();
        graph.Clear(cwd);

        if (store.TryLoad(cwd, out var cached))
            graph.MergeFragments(cached);

        if (graph.NodeCount > 0) return; // caché cargada: listo para responder

        var files = SolutionScanner.DiscoverFiles(cwd).ToList();
        if (files.Count == 0) return;

        var scanner = new SolutionScanner(graph);
        scanner.ScanAsync(cwd).Wait();
        store.Save(cwd, graph.Fragments());
    }

    private static int PrintUnknown(string cmd)
    {
        Console.Error.WriteLine($"Comando desconocido: '{cmd}'");
        Console.Error.WriteLine("Ejecuta 'sharpgraph help' para ver los comandos disponibles.");
        return 1;
    }
}
