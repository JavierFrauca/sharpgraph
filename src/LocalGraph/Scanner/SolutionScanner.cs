using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LocalGraph.Graph;
using Microsoft.CodeAnalysis.CSharp;

namespace LocalGraph.Scanner;

public sealed class SolutionScanner(CodeGraph graph)
{
    /// <summary>Escaneo completo: reemplaza el grafo entero.</summary>
    public async Task ScanAsync(string path)
    {
        var files = DiscoverFiles(path).ToList();
        await Console.Error.WriteLineAsync($"Scanning {files.Count} .cs files...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var fragments = ParseFiles(files);
        graph.MergeFragments(fragments);

        sw.Stop();
        await Console.Error.WriteLineAsync($"Done in {sw.ElapsedMilliseconds}ms. {graph.Stats()}");
    }

    /// <summary>
    /// Escaneo incremental: solo re-parsea ficheros nuevos o con hash distinto,
    /// y elimina del grafo los ficheros que ya no existen.
    /// </summary>
    public async Task ScanIncrementalAsync(string path)
    {
        var files = DiscoverFiles(path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = graph.KnownFiles();

        foreach (var gone in known.Where(k => !files.Contains(k)))
            graph.RemoveFile(gone);

        var toParse = new List<string>();
        foreach (var f in files)
        {
            var hash = HashFile(f);
            if (graph.TryGetFileHash(f, out var existing) && existing == hash) continue;
            toParse.Add(f);
        }

        if (toParse.Count == 0)
        {
            await Console.Error.WriteLineAsync("Incremental scan: nothing changed.");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fragments = ParseFiles(toParse);
        graph.MergeFragments(fragments);
        sw.Stop();
        await Console.Error.WriteLineAsync($"Incremental: {toParse.Count} files in {sw.ElapsedMilliseconds}ms. {graph.Stats()}");
    }

    /// <summary>Re-parsea un único fichero (usado por el file watcher).</summary>
    public void RescanFile(string filePath)
    {
        if (!File.Exists(filePath)) { graph.RemoveFile(filePath); return; }
        var fragment = ParseFile(filePath);
        if (fragment is not null) graph.MergeFragment(fragment);
    }

    private static List<FileFragment> ParseFiles(IReadOnlyCollection<string> files)
    {
        var bag = new ConcurrentBag<FileFragment>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            var frag = ParseFile(f);
            if (frag is not null) bag.Add(frag);
        });
        return bag.ToList();
    }

    private static FileFragment? ParseFile(string filePath)
    {
        try
        {
            var code = File.ReadAllText(filePath);
            var hash = HashText(code);
            var tree = CSharpSyntaxTree.ParseText(code);
            var fragment = new FileFragment { FilePath = filePath, Hash = hash };
            var visitor = new TypeReferenceVisitor(fragment);
            visitor.Visit(tree.GetRoot());
            return fragment;
        }
        catch
        {
            return null; // ficheros no parseables se ignoran en silencio
        }
    }

    public static IEnumerable<string> DiscoverFiles(string path)
    {
        var root = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f));
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (parts.Any(p => p is "obj" or "bin" or ".git" or "node_modules" or ".vs"))
            return true;
        // ficheros generados
        var name = Path.GetFileName(path);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        try { return HashText(File.ReadAllText(path)); }
        catch { return Guid.NewGuid().ToString(); }
    }

    private static string HashText(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
