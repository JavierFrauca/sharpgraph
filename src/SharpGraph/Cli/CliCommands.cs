using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Watcher;

namespace SharpGraph.Cli;

/// <summary>
/// Un método por subcomando CLI. Cada uno parsea argumentos posicionales
/// y flags simples (-d, -m, -l, -n), llama al método equivalente de
/// <see cref="GraphEngine"/> e imprime el resultado a stdout.
/// </summary>
internal static class CliCommands
{
    // ────────────────────────── ESCANEO ──────────────────────────

    public static async Task<int> Scan(string[] args, GraphEngine graph, GraphStore store, ProjectWatcher watcher)
    {
        var path = GetPositional(args, 0) ?? ".";

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine($"Path no encontrado: {path}");
            return 1;
        }

        graph.Clear(path);
        if (store.TryLoad(path, out var cached))
            graph.MergeFragments(cached);

        var scanner = new Scanner.SolutionScanner(graph);
        await scanner.ScanIncrementalAsync(path);
        store.Save(path, graph.Fragments());
        watcher.Watch(path);

        Console.WriteLine(graph.Stats());
        return 0;
    }

    public static int Stats(GraphEngine graph)
    {
        Console.WriteLine(graph.Stats());
        return 0;
    }

    // ────────────────────────── ENDPOINTS ──────────────────────────

    public static int Endpoints(GraphEngine graph)
    {
        Console.WriteLine(graph.ListEndpoints());
        return 0;
    }

    public static int EndpointFlow(string[] args, GraphEngine graph)
    {
        var endpoint = GetPositional(args, 0);
        if (endpoint is null)
        {
            Console.Error.WriteLine("Uso: sharpgraph endpoint-flow \"POST /api/orders\" | /api/orders | OrdersController [-d <depth>] [-n <nodos>] [--dtos]");
            return 1;
        }
        var depth = GetFlagInt(args, "-d", 8);
        var maxNodes = GetFlagInt(args, "-n", 80);
        var dtos = HasFlag(args, "--dtos");
        Console.WriteLine(graph.EndpointFlow(endpoint, depth, maxNodes, dtos));
        return 0;
    }

    // ────────────────────────── NAVEGACIÓN ──────────────────────────

    public static int Search(string[] args, GraphEngine graph)
    {
        var pattern = GetPositional(args, 0);
        if (pattern is null) { Console.Error.WriteLine("Uso: sharpgraph search <patrón>"); return 1; }
        Console.WriteLine(graph.Search(pattern));
        return 0;
    }

    public static int Callers(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph callers <tipo> [-d <depth>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 3);
        Console.WriteLine(graph.FindCallers(type, depth));
        return 0;
    }

    public static int Usages(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph usages <tipo>"); return 1; }
        Console.WriteLine(graph.GetUsages(type));
        return 0;
    }

    public static int Callsites(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph callsites <tipo> [-m <miembro>] [-l <límite>]"); return 1; }
        var member = GetFlag(args, "-m");
        var limit = GetFlagInt(args, "-l", 50);
        Console.WriteLine(graph.FindCallSites(type, member, limit));
        return 0;
    }

    public static int Trace(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph trace <tipo> [-d <depth>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 8);
        Console.WriteLine(graph.TraceToEndpoints(type, depth));
        return 0;
    }

    public static int Impact(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph impact <tipo> [-d <depth>] [--with-tests]"); return 1; }
        var depth = GetFlagInt(args, "-d", 6);
        var withTests = HasFlag(args, "--with-tests");
        Console.WriteLine(graph.Impact(type, depth, withTests));
        return 0;
    }

    public static int Flow(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph flow <tipo> [-m <miembro>] [-d <depth>]"); return 1; }
        var member = GetFlag(args, "-m");
        var depth = GetFlagInt(args, "-d", 2);
        Console.WriteLine(graph.Flow(type, member, depth));
        return 0;
    }

    public static int Hubs(string[] args, GraphEngine graph)
    {
        var topK = GetFlagInt(args, "-n", 15);
        var includeExternal = HasFlag(args, "--include-external");
        Console.WriteLine(graph.Hubs(topK, includeExternal));
        return 0;
    }

    public static int Di(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph di <tipo>"); return 1; }
        Console.WriteLine(graph.ResolveDi(type));
        return 0;
    }

    // ────────────────────────── CÓDIGO ──────────────────────────

    public static int Source(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph source <tipo> [-m <miembro>] [-l <líneas>]"); return 1; }
        var member = GetFlag(args, "-m");
        var lines = GetFlagInt(args, "-l", member is not null ? 60 : 200);
        Console.WriteLine(graph.GetSource(type, member, lines));
        return 0;
    }

    public static int Understand(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph understand <tipo> [-l <budget>]"); return 1; }
        var budget = GetFlagInt(args, "-l", 200);
        Console.WriteLine(graph.Understand(type, budget));
        return 0;
    }

    public static int ReadFile(string[] args, GraphEngine graph)
    {
        var path = GetPositional(args, 0);
        if (path is null) { Console.Error.WriteLine("Uso: sharpgraph read-file <fichero> [-l <líneas>]"); return 1; }
        var lines = GetFlagInt(args, "-l", 200);
        Console.WriteLine(graph.ReadFile(path, lines));
        return 0;
    }

    public static int Semantic(string[] args, GraphEngine graph)
    {
        var query = GetPositional(args, 0);
        if (query is null) { Console.Error.WriteLine("Uso: sharpgraph semantic <query> [-n <topK>]"); return 1; }
        // si hay múltiples posicionales, los unimos (la query puede tener espacios sin comillas)
        var allPositionals = GetAllPositionals(args);
        if (allPositionals.Length > 1) query = string.Join(" ", allPositionals);
        var topK = GetFlagInt(args, "-n", 10);
        Console.WriteLine(graph.SearchSemantic(query, topK));
        return 0;
    }

    public static int Explore(string[] args, GraphEngine graph)
    {
        var pattern = GetPositional(args, 0);
        if (pattern is null) { Console.Error.WriteLine("Uso: sharpgraph explore <patrón> [-d <depth>] [-l <limit>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 2);
        var limit = GetFlagInt(args, "-l", 8);
        Console.WriteLine(graph.ExploreContext(pattern, depth, limit));
        return 0;
    }

    public static int Literals(string[] args, GraphEngine graph)
    {
        var pattern = GetPositional(args, 0);
        if (pattern is null) { Console.Error.WriteLine("Uso: sharpgraph literals <texto> [-l <límite>] [-c]"); return 1; }
        var limit = GetFlagInt(args, "-l", 30);
        var caseSensitive = HasFlag(args, "-c");
        Console.WriteLine(graph.SearchLiterals(pattern, limit, caseSensitive));
        return 0;
    }

    // ────────────────────────── DIAGRAMAS ──────────────────────────

    public static int Mermaid(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph mermaid <tipo> [-u <callers>] [-d <deps>] [-n <nodos>] [--lr] [--external]"); return 1; }
        var up = GetFlagInt(args, "-u", 3);
        var down = GetFlagInt(args, "-d", 3);
        var maxNodes = GetFlagInt(args, "-n", 40);
        var dir = HasFlag(args, "--lr") ? "LR" : "TD";
        var external = HasFlag(args, "--external");
        Console.WriteLine(graph.MermaidContext(type, up, down, maxNodes, dir, external));
        return 0;
    }

    public static int MermaidSeq(string[] args, GraphEngine graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph mermaid-seq <tipo> [-m <miembro>] [-p <caminos>] [-d <depth>]"); return 1; }
        var member = GetFlag(args, "-m");
        var paths = GetFlagInt(args, "-p", 3);
        var depth = GetFlagInt(args, "-d", 8);
        Console.WriteLine(graph.MermaidSequence(type, member, paths, depth));
        return 0;
    }

    public static int MermaidOverview(string[] args, GraphEngine graph)
    {
        var area = GetFlag(args, "-a");
        var depth = GetFlagInt(args, "-d", 2);
        var maxNodes = GetFlagInt(args, "-n", 60);
        Console.WriteLine(graph.MermaidOverview(area, depth, maxNodes));
        return 0;
    }

    // ────────────────────────── MANTENIMIENTO ──────────────────────────

    public static async Task<int> Update(string[] args)
    {
        var checkOnly = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        return await SharpGraph.Update.SelfUpdater.Run(checkOnly);
    }

    // ────────────────────────── HELP ──────────────────────────

    public static int Help(string[] args)
    {
        var command = GetPositional(args, 0);
        if (command is null)
            Console.WriteLine(CliHelp.General());
        else
            Console.WriteLine(CliHelp.ForCommand(command));
        return 0;
    }

    // ────────────────────────── PARSING ──────────────────────────

    /// <summary>Obtiene argumento posicional o muestra uso y devuelve error.</summary>
    private static bool RequirePositional(string[] args, out string value, string usage)
    {
        value = GetPositional(args, 0)!;
        if (value is not null) return true;
        Console.Error.WriteLine(usage);
        return false;
    }

    /// <summary>Obtiene el n-ésimo argumento posicional (ignora flags).</summary>
    private static string? GetPositional(string[] args, int index)
    {
        var positionals = GetAllPositionals(args);
        return index < positionals.Length ? positionals[index] : null;
    }

    /// <summary>Todos los argumentos que no empiezan por - o --.</summary>
    private static string[] GetAllPositionals(string[] args)
        => args.Where(a => !a.StartsWith('-')).ToArray();

    /// <summary>Obtiene el valor de un flag string: -m valor.</summary>
    private static string? GetFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>Obtiene el valor de un flag int: -d 3.</summary>
    private static int GetFlagInt(string[] args, string flag, int defaultValue)
    {
        var val = GetFlag(args, flag);
        return int.TryParse(val, out var n) ? n : defaultValue;
    }

    /// <summary>Comprueba si un flag booleano está presente: --include-external.</summary>
    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
