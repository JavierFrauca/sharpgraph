using SharpGraph.Graph;
using Xunit;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// search_literals: el grep nativo del grafo. Cubre extracción (plain, verbatim,
/// const; ruido filtrado), búsqueda insensible/sensible, y la ruta delta
/// (editar un fichero sustituye sus literales).
/// </summary>
public class LiteralsTests
{
    private const string Code = """
        using Microsoft.AspNetCore.Mvc;

        namespace Corp.App.Pages;

        public sealed class TodoLists
        {
            private const string PageTitle = "Todo Lists";
            private readonly string _path = @"C:\\data\\todo.json";

            public IActionResult Render(string user)
            {
                var msg = $"Hola {user}";          // interpolated: NO indexado (v1)
                var empty = "";
                var single = "x";
                System.Console.WriteLine("todo lists pendientes: " + msg);
                return Ok(PageTitle);
            }
        }
        """;

    private static GraphEngine Build() => GraphTestHarness.BuildFromSnippet(Code);

    [Fact]
    public void Finds_Literals_With_File_Line_And_Type()
    {
        var graph = Build();
        var output = graph.SearchLiterals("todo lists");

        Assert.Contains("'todo lists' × 2:", output);
        // insensible por defecto: encuentra el const y el WriteLine
        Assert.Contains("Todo Lists", output);
        Assert.Contains("todo lists pendientes", output);
        Assert.Contains("[TodoLists]", output);
    }

    [Fact]
    public void Verbatim_And_Const_Literals_Are_Indexed_Trivia_Is_Not()
    {
        var graph = Build();

        Assert.Contains("C:\\\\data\\\\todo.json", graph.SearchLiterals("data"));
        // "" y "x" se filtran por triviales; el interpolated no se indexa (v1)
        Assert.Contains("No literals matching", graph.SearchLiterals("Hola"));
    }

    [Fact]
    public void CaseSensitive_Flag_Distinguishes()
    {
        var graph = Build();

        var insensitive = graph.SearchLiterals("TODO LISTS");
        var sensitive = graph.SearchLiterals("TODO LISTS", caseSensitive: true);

        Assert.Contains("Todo Lists", insensitive);
        Assert.Contains("No literals matching", sensitive);
    }

    [Fact]
    public void No_Match_Hint_Is_Friendly()
    {
        var graph = Build();
        var output = graph.SearchLiterals("no-existe-nada-de-esto");
        Assert.Contains("No literals matching", output);
        Assert.Contains("grep", output);
    }

    [Fact]
    public void Delta_Edit_Replaces_File_Literals()
    {
        var graph = Build();
        Assert.Contains("todo lists pendientes", graph.SearchLiterals("pendientes"));

        // misma declaración de tipos, distinto cuerpo → ruta delta
        var edited = Code.Replace("todo lists pendientes", "tareas pendientes");
        var fragment = GraphTestHarness.ParseSnippet(edited, "snippet0.cs");
        Assert.NotNull(fragment);
        graph.MergeFragments([fragment!]);

        Assert.True(graph.LastMergeIncremental, "debería tomar la ruta delta (mismos tipos)");
        Assert.DoesNotContain("todo lists pendientes", graph.SearchLiterals("pendientes"));
        Assert.Contains("tareas pendientes", graph.SearchLiterals("pendientes"));
    }
}
