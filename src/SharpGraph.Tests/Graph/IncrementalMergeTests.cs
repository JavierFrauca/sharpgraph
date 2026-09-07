using SharpGraph.Graph;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// Equivalencia de la fusión incremental (GraphEngine.Incremental.cs) frente al
/// rebuild completo: tras editar un fichero ya indexado, el grafo resultante debe
/// ser indistinguible del que se obtendría construyéndolo desde cero con las
/// fuentes editadas. Se compara Stats() exacto (recuentos de todos los índices) y
/// la salida normalizada (líneas ordenadas) de las queries ricas.
/// </summary>
public class IncrementalMergeTests
{
    private const string HandlerCs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public void Handle()
    {
        _svc.Run();
    }
}";

    private const string HandlerV2Cs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public void Handle()
    {
        _svc.Run();
        _svc.Other();
    }
}";

    private const string HandlerSinLlamadasCs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public void Handle()
    {
        _svc.Other();
    }
}";

    private const string HandlerConTipoNuevoCs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public void Handle()
    {
        _svc.Run();
    }
}

public class AuditLog
{
    public void Append(string line) { }
}";

    private const string HandlerConOtroRetornoCs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public ICalcService Build()
    {
        return null;
    }

    public void Handle()
    {
        _svc.Run();
    }
}";

    private const string HandlerConOtroRetornoV2Cs = @"
namespace App;

public class CalcHandler
{
    private readonly ICalcService _svc;

    public CalcHandler(ICalcService svc)
    {
        _svc = svc;
    }

    public CalcService Build()
    {
        return null;
    }

    public void Handle()
    {
        _svc.Run();
    }
}";

    private const string ServiceCs = @"
namespace App;

public interface ICalcService
{
    void Run();
    void Other();
}

public class CalcService : ICalcService
{
    public void Run() { }
    public void Other() { }
}";

    private const string ServiceV2Cs = @"
namespace App;

public interface ICalcService
{
    void Run();
    void Other();
}

public class CalcService : ICalcService
{
    public void Run() { }
    public void Other() { }
    public void Reset() { }
}";

    private const string ServiceSinClaseCs = @"
namespace App;

public interface ICalcService
{
    void Run();
    void Other();
}";

    private const string RegistrationCs = @"
namespace App;

public static class Registration
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<ICalcService, CalcService>();
    }
}";

    private const string OrchestratorCs = @"
namespace App;

public class Orchestrator
{
    private readonly ICalcFactory _factory;

    public Orchestrator(ICalcFactory factory)
    {
        _factory = factory;
    }

    public void Execute()
    {
        var svc = _factory.Get();
        svc.Run();
    }
}";

    private const string OrchestratorV2Cs = @"
namespace App;

public class Orchestrator
{
    private readonly ICalcFactory _factory;

    public Orchestrator(ICalcFactory factory)
    {
        _factory = factory;
    }

    public void Execute()
    {
        var svc = _factory.Get();
        svc.Other();
    }
}";

    private const string FactoryCs = @"
namespace App;

public interface ICalcFactory
{
    ICalcService Get();
}";

    private const string PartialACs = @"
namespace App;

public partial class Part
{
    public void A() { }
}";

    private const string PartialBCs = @"
namespace App;

public partial class Part
{
    public void B(ICalcService svc)
    {
        svc.Run();
    }
}";

    private const string PartialBV2Cs = @"
namespace App;

public partial class Part
{
    public void B(ICalcService svc)
    {
        svc.Other();
    }
}";

    // -------- fusión incremental: edición de cuerpo --------

    [Fact]
    public void BodyEdit_TomaRutaIncremental_YEquivaleAlRebuildCompleto()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(HandlerV2Cs, "snippet0.cs")!);

        Assert.True(graph.LastMergeIncremental);
        var fresh = GraphTestHarness.BuildFromSnippet(HandlerV2Cs, ServiceCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
        Assert.Contains("Other", graph.FindCallSites("ICalcService", null, 50));
    }

    [Fact]
    public void RemovedCall_LaAristaYCallSiteSuciosDesaparecen()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(HandlerSinLlamadasCs, "snippet0.cs")!);

        var sites = graph.FindCallSites("ICalcService", null, 50);
        Assert.DoesNotContain("Run()", sites);
        Assert.Contains("Other()", sites);

        var fresh = GraphTestHarness.BuildFromSnippet(HandlerSinLlamadasCs, ServiceCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
    }

    [Fact]
    public void BodyEditBatch_DeVariosFicheros_UnSoloMergeIncremental()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragments(
        [
            GraphTestHarness.ParseSnippet(HandlerV2Cs, "snippet0.cs")!,
            GraphTestHarness.ParseSnippet(ServiceV2Cs, "snippet1.cs")!,
        ]);

        Assert.True(graph.LastMergeIncremental);
        var fresh = GraphTestHarness.BuildFromSnippet(HandlerV2Cs, ServiceV2Cs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
        // el miembro nuevo (Reset) entra en el doc BM25 del tipo
        Assert.Equal(
            Normalize(fresh.SearchSemantic("reset run calc")),
            Normalize(graph.SearchSemantic("reset run calc")));
    }

    // -------- fallback a rebuild completo: cambios estructurales --------

    [Fact]
    public void NewType_CaeARebuildCompleto_YEquivale()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(HandlerConTipoNuevoCs, "snippet0.cs")!);

        Assert.False(graph.LastMergeIncremental);
        var fresh = GraphTestHarness.BuildFromSnippet(HandlerConTipoNuevoCs, ServiceCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
        Assert.Contains("AuditLog", graph.Search("AuditLog"));
    }

    [Fact]
    public void DeletedType_EnOtroFichero_CaeARebuildCompleto_YEquivale()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(ServiceSinClaseCs, "snippet1.cs")!);

        Assert.False(graph.LastMergeIncremental);
        var fresh = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceSinClaseCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
        Assert.DoesNotContain("App.CalcService ", graph.Search("CalcService"));
    }

    [Fact]
    public void ReturnTypeChange_CaeARebuildCompleto_YEquivale()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerConOtroRetornoCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(HandlerConOtroRetornoV2Cs, "snippet0.cs")!);

        Assert.False(graph.LastMergeIncremental);
        var fresh = GraphTestHarness.BuildFromSnippet(HandlerConOtroRetornoV2Cs, ServiceCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
    }

    // -------- receptores encadenados (pending call-sites) en la ruta rápida --------

    [Fact]
    public void PendingChainBodyEdit_ActualizaElCallSiteResuelto()
    {
        var graph = GraphTestHarness.BuildFromSnippet(OrchestratorCs, FactoryCs, ServiceCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(OrchestratorV2Cs, "snippet0.cs")!);

        Assert.True(graph.LastMergeIncremental);
        var sites = graph.FindCallSites("ICalcService", null, 50);
        Assert.Contains("Orchestrator.Execute", sites);
        Assert.Contains("Other()", sites);

        var fresh = GraphTestHarness.BuildFromSnippet(OrchestratorV2Cs, FactoryCs, ServiceCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
    }

    // -------- partial classes: el nodo sobrevive a la edición del hermano --------

    [Fact]
    public void PartialClass_ElNodoSobreviveALaEdicionDelHermano()
    {
        var graph = GraphTestHarness.BuildFromSnippet(PartialACs, PartialBCs, ServiceCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(PartialBV2Cs, "snippet1.cs")!);

        Assert.True(graph.LastMergeIncremental);
        Assert.Contains("App.Part", graph.Search("Part"));

        var fresh = GraphTestHarness.BuildFromSnippet(PartialACs, PartialBV2Cs, ServiceCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
    }

    // -------- RemoveFile --------

    [Fact]
    public void RemoveFile_EquivaleAGrafoConstruidoSinEseFichero()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.RemoveFile("snippet1.cs");

        var fresh = GraphTestHarness.BuildFromSnippet(HandlerCs, RegistrationCs);
        Assert.Equal(fresh.Stats(), graph.Stats());
        Assert.Equal(Queries(fresh), Queries(graph));
    }

    // -------- índice semántico (BM25) --------

    [Fact]
    public void SemanticSearch_LosScoresCoincidenTrasLaEdicionIncremental()
    {
        var graph = GraphTestHarness.BuildFromSnippet(HandlerCs, ServiceCs, RegistrationCs);
        graph.MergeFragment(GraphTestHarness.ParseSnippet(HandlerV2Cs, "snippet0.cs")!);

        var fresh = GraphTestHarness.BuildFromSnippet(HandlerV2Cs, ServiceCs, RegistrationCs);
        Assert.Equal(
            Normalize(fresh.SearchSemantic("run calc service")),
            Normalize(graph.SearchSemantic("run calc service")));
    }

    // -------- humo de rendimiento: el delta debe ser milisegundos --------

    [Fact]
    public void MergeFragment_EdicionDeCuerpo_CompletaEnMilisegundos()
    {
        const int n = 150;
        var codes = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            codes.Add($@"
namespace Gen{i % 10};

public class C{i}
{{
    private readonly C{next} _next;

    public C{i}(C{next} next)
    {{
        _next = next;
    }}

    public void Op()
    {{
        _next.Op();
    }}
}}");
        }

        var graph = GraphTestHarness.BuildFromSnippet(codes[0], codes.Skip(1).ToArray());
        var edited = codes[0].Replace("_next.Op();", "_next.Op();\n        _next.Op();");
        var fragment = GraphTestHarness.ParseSnippet(edited, "snippet0.cs");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        graph.MergeFragment(fragment!);
        sw.Stop();

        Assert.True(graph.LastMergeIncremental);
        Assert.True(sw.ElapsedMilliseconds < 500, $"El delta tardó {sw.ElapsedMilliseconds}ms (esperado: milisegundos).");
    }

    // -------- helpers --------

    /// <summary>Salida con líneas recortadas y ordenadas: compara contenido, no orden de iteración.</summary>
    private static string Normalize(string output)
        => string.Join("\n", output
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Order(StringComparer.Ordinal));

    /// <summary>Concatenación normalizada de las queries ricas, para comparar delta vs rebuild.</summary>
    private static string Queries(GraphEngine g)
        => string.Join("\n≈\n", new[]
        {
            g.FindCallers("ICalcService", 2),
            g.GetUsages("CalcHandler"),
            g.FindCallSites("ICalcService", null, 50),
            g.ResolveDi("ICalcService"),
            g.TraceToEndpoints("CalcService", 6),
            g.SearchSemantic("run calc service"),
        }.Select(Normalize));
}
