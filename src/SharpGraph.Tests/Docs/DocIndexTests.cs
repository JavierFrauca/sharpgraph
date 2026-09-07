using SharpGraph.Docs;

namespace SharpGraph.Tests.Docs;

/// <summary>
/// Tests del índice de documentación: parseo (título/secciones), búsqueda BM25
/// con ponderación de secciones, menciones docs↔código, inclusiones/exclusiones
/// y actualización incremental (RescanFiles).
/// </summary>
public class DocIndexTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sharpgraph-doctests-" + Guid.NewGuid().ToString("N"));

    private string WriteDoc(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp: best-effort */ }
    }

    // -------- búsqueda --------

    [Fact]
    public void Search_Finds_Markdown_And_Shows_Relative_Path_Title_And_Section()
    {
        WriteDoc("docs/adr/007-retenciones.md", """
            # Cálculo de retenciones

            Introducción general.

            ## Cálculo del tramo IRPF

            Detalle del cálculo.

            ```text
            # esto no es una sección (está en un fence)
            ```
            """);
        var index = new DocIndex();
        index.Rebuild(_dir, []);

        var result = index.Search("tramo irpf");

        Assert.Contains("docs/adr/007-retenciones.md", result);
        Assert.Contains("Cálculo de retenciones", result);
        Assert.Contains("§ Cálculo del tramo IRPF", result);
        Assert.DoesNotContain("§ esto no es una sección", result);
    }

    [Fact]
    public void Search_Ranks_Heading_Match_Over_Body_Only_Match()
    {
        WriteDoc("a.md", """
            # Doc A

            ## Casos de nomina en produccion

            Blah blah blah blah blah.
            """);
        WriteDoc("b.md", """
            # Doc B

            Texto sobre nomina y pagas.
            """);
        var index = new DocIndex();
        index.Rebuild(_dir, []);

        var result = index.Search("nomina");

        // la sección ponderada x3 debe posicionar a.md antes que la mención de cuerpo de b.md
        Assert.True(result.IndexOf("a.md") < result.IndexOf("b.md"),
            $"esperado a.md antes que b.md:\n{result}");
    }

    [Fact]
    public void Search_On_Empty_Index_Advises_Scan()
    {
        var index = new DocIndex();

        Assert.Contains("scan()", index.Search("cualquier cosa"));
    }

    [Fact]
    public void Txt_Is_Indexed_With_Filename_As_Title()
    {
        WriteDoc("NOTES.txt", "notas del proyecto sobre pagas");
        var index = new DocIndex();
        index.Rebuild(_dir, []);

        var result = index.Search("pagas");

        Assert.Contains("NOTES.txt", result);
        Assert.Contains("«NOTES»", result);
    }

    // -------- json de configuración --------

    [Fact]
    public void Only_Config_Json_Files_Are_Indexed()
    {
        WriteDoc("appsettings.json", """{ "ConnectionStrings": { "PayrollDb": "Server=sql" } }""");
        WriteDoc("package.json", """{ "name": "demo", "dependencies": {} }""");
        var index = new DocIndex();
        index.Rebuild(_dir, []);

        Assert.Equal(1, index.Count);
        Assert.Contains("appsettings.json", index.Search("connection"));
    }

    // -------- menciones docs↔código --------

    [Fact]
    public void Mentions_Connect_Docs_To_PascalCase_Types_Only()
    {
        WriteDoc("docs/adr/001.md", "# ADR 1\n\nEl GrossService centraliza el cálculo.");
        WriteDoc("docs/adr/002.md", "# ADR 2\n\nEl usuario (user) de la app.");
        var index = new DocIndex();
        index.Rebuild(_dir, ["GrossService", "User", "Db"]);

        var mentions = index.MentionsOf("GrossService");
        var single = Assert.Single(mentions);
        Assert.Equal("docs/adr/001.md", single.Path);
        Assert.Equal("ADR 1", single.Title);

        Assert.Empty(index.MentionsOf("User")); // sin mayúscula interna: no mencionable
        Assert.Empty(index.MentionsOf("Db"));   // < 4 caracteres
    }

    [Fact]
    public void Graph_Search_And_Understand_Show_Doc_Mentions()
    {
        var graph = GraphTestHarness.BuildFromSnippet(
            "public class GrossService { public decimal Calc() => 1m; }");
        WriteDoc("docs/adr/010.md", "# ADR servicio\n\nEl GrossService calcula el bruto.");

        graph.ScanDocs(_dir);

        var understand = graph.Understand("GrossService", 200);
        Assert.Contains("docs:", understand);
        Assert.Contains("docs/adr/010.md", understand);

        var search = graph.Search("GrossService");
        Assert.Contains("[docs:1]", search);
    }

    // -------- exclusiones e incremental --------

    [Fact]
    public void Excluded_Directories_Are_Skipped()
    {
        WriteDoc("obj/generated.md", "# build output");
        WriteDoc("docs/real.md", "# Doc real");
        var index = new DocIndex();
        index.Rebuild(_dir, []);

        Assert.Equal(1, index.Count);
        Assert.DoesNotContain("generated", index.Search("build output"));
    }

    [Fact]
    public void RescanFiles_Picks_Up_Edits_And_Deletions()
    {
        var path = WriteDoc("docs/x.md", "# Alpha\n\nContenido alpha.");
        var index = new DocIndex();
        index.Rebuild(_dir, []);
        Assert.Contains("Alpha", index.Search("alpha"));

        File.WriteAllText(path, "# Beta\n\nContenido beta.");
        index.RescanFiles([path], []);
        Assert.StartsWith("Sin resultados", index.Search("alpha"));
        Assert.Contains("Beta", index.Search("beta"));

        File.Delete(path);
        index.RescanFiles([path], []);
        Assert.Equal(0, index.Count);
    }
}
