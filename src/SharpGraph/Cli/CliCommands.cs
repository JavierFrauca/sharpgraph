using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Watcher;

namespace SharpGraph.Cli;

/// <summary>
/// Un método por subcomando CLI. Cada uno parsea argumentos posicionales
/// y flags simples (-d, -m, -l, -n), llama al método equivalente de
/// <see cref="CodeGraph"/> e imprime el resultado a stdout.
/// </summary>
internal static class CliCommands
{
    // ────────────────────────── ESCANEO ──────────────────────────

    public static async Task<int> Scan(string[] args, CodeGraph graph, GraphStore store, ProjectWatcher watcher)
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

    public static int Stats(CodeGraph graph)
    {
        Console.WriteLine(graph.Stats());
        return 0;
    }

    // ────────────────────────── NAVEGACIÓN ──────────────────────────

    public static int Search(string[] args, CodeGraph graph)
    {
        var pattern = GetPositional(args, 0);
        if (pattern is null) { Console.Error.WriteLine("Uso: sharpgraph search <patrón>"); return 1; }
        Console.WriteLine(graph.Search(pattern));
        return 0;
    }

    public static int Callers(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph callers <tipo> [-d <depth>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 3);
        Console.WriteLine(graph.FindCallers(type, depth));
        return 0;
    }

    public static int Usages(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph usages <tipo>"); return 1; }
        Console.WriteLine(graph.GetUsages(type));
        return 0;
    }

    public static int Callsites(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph callsites <tipo> [-m <miembro>] [-l <límite>]"); return 1; }
        var member = GetFlag(args, "-m");
        var limit = GetFlagInt(args, "-l", 50);
        Console.WriteLine(graph.FindCallSites(type, member, limit));
        return 0;
    }

    public static int Trace(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph trace <tipo> [-d <depth>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 8);
        Console.WriteLine(graph.TraceToEndpoints(type, depth));
        return 0;
    }

    public static int Flow(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph flow <tipo> [-m <miembro>] [-d <depth>]"); return 1; }
        var member = GetFlag(args, "-m");
        var depth = GetFlagInt(args, "-d", 2);
        Console.WriteLine(graph.Flow(type, member, depth));
        return 0;
    }

    public static int Hubs(string[] args, CodeGraph graph)
    {
        var topK = GetFlagInt(args, "-n", 15);
        var includeExternal = HasFlag(args, "--include-external");
        Console.WriteLine(graph.Hubs(topK, includeExternal));
        return 0;
    }

    public static int Di(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph di <tipo>"); return 1; }
        Console.WriteLine(graph.ResolveDi(type));
        return 0;
    }

    // ────────────────────────── CÓDIGO ──────────────────────────

    public static int Source(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph source <tipo> [-m <miembro>] [-l <líneas>]"); return 1; }
        var member = GetFlag(args, "-m");
        var lines = GetFlagInt(args, "-l", member is not null ? 60 : 200);
        Console.WriteLine(graph.GetSource(type, member, lines));
        return 0;
    }

    public static int Understand(string[] args, CodeGraph graph)
    {
        var type = GetPositional(args, 0);
        if (type is null) { Console.Error.WriteLine("Uso: sharpgraph understand <tipo> [-l <budget>]"); return 1; }
        var budget = GetFlagInt(args, "-l", 200);
        Console.WriteLine(graph.Understand(type, budget));
        return 0;
    }

    public static int ReadFile(string[] args, CodeGraph graph)
    {
        var path = GetPositional(args, 0);
        if (path is null) { Console.Error.WriteLine("Uso: sharpgraph read-file <fichero> [-l <líneas>]"); return 1; }
        var lines = GetFlagInt(args, "-l", 200);
        Console.WriteLine(graph.ReadFile(path, lines));
        return 0;
    }

    public static int Semantic(string[] args, CodeGraph graph)
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

    public static int Explore(string[] args, CodeGraph graph)
    {
        var pattern = GetPositional(args, 0);
        if (pattern is null) { Console.Error.WriteLine("Uso: sharpgraph explore <patrón> [-d <depth>] [-l <limit>]"); return 1; }
        var depth = GetFlagInt(args, "-d", 2);
        var limit = GetFlagInt(args, "-l", 8);
        Console.WriteLine(graph.ExploreContext(pattern, depth, limit));
        return 0;
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
