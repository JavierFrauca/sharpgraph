using System.Collections.Concurrent;
using System.Reflection;
using LocalGraph.Graph;
using LocalGraph.Scanner;
using Microsoft.CodeAnalysis.CSharp;

namespace LocalGraph.Tests;

/// <summary>
/// Construye un <see cref="CodeGraph"/> a partir de fixtures sintéticos embebidos
/// (carpeta <c>Fixtures/</c>). Cada fixture activa un patrón concreto y se parsea
/// con el mismo <c>TypeReferenceVisitor</c> que usa el scanner en producción, de
/// modo que los tests ejercitan el camino real sin tocar el disco.
/// </summary>
internal static class GraphTestHarness
{
    private const string FixturePrefix = "LocalGraph.Tests.Fixtures.";

    /// <summary>Construye un grafo con uno o varios fixtures por nombre (sin extensión).</summary>
    public static CodeGraph Build(params string[] fixtureNames)
    {
        var graph = new CodeGraph();
        var assembly = Assembly.GetExecutingAssembly();
        var fragments = new ConcurrentBag<FileFragment>();

        Parallel.ForEach(fixtureNames, name =>
        {
            var resource = FixturePrefix + name + ".cs";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException($"Fixture embebido no encontrado: {resource}");
            using var reader = new StreamReader(stream);
            var code = reader.ReadToEnd();

            var frag = ParseFixture(name + ".cs", code);
            if (frag is not null) fragments.Add(frag);
        });

        graph.MergeFragments(fragments);
        return graph;
    }

    /// <summary>Parses un snippet C# suelto y devuelve su fragmento (sin pasar por disco).</summary>
    public static FileFragment? ParseSnippet(string code, string fileName = "snippet.cs")
        => ParseFixture(fileName, code);

    private static FileFragment? ParseFixture(string fileName, string code)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(code));
        var fragment = new FileFragment { FilePath = fileName, Hash = Convert.ToHexString(hash) };
        var tree = CSharpSyntaxTree.ParseText(code);
        var visitor = new TypeReferenceVisitor(fragment);
        visitor.Visit(tree.GetRoot());
        return fragment;
    }

    /// <summary>Lista los nombres de aristas (Relation) entre dos tipos, sin distinguir FQN/calificado.</summary>
    public static List<EdgeRelation> Relations(this FileFragment f, string fromSimple, string toSimple)
        => f.Edges
            .Where(e => LastSegment(e.From) == fromSimple && LastSegment(e.To) == toSimple)
            .Select(e => e.Relation)
            .ToList();

    /// <summary>¿El fragmento contiene una arista con esa relación hacia ese destino simple?</summary>
    public static bool HasRelation(this FileFragment f, string fromSimple, string toSimple, EdgeRelation relation)
        => f.Edges.Any(e => LastSegment(e.From) == fromSimple
                         && LastSegment(e.To) == toSimple
                         && e.Relation == relation);

    private static string LastSegment(string name)
    {
        var idx = name.LastIndexOf('.');
        return idx < 0 ? name : name[(idx + 1)..];
    }
}
