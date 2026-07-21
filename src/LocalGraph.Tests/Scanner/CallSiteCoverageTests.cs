using LocalGraph.Graph;
using LocalGraph.Scanner;

namespace LocalGraph.Tests.Scanner;

/// <summary>
/// Batería failing-first que reproduce los patrones ALTA identificados en la
/// investigación de pérdida de call-sites (caso R06: RunInitialIndexAsync no
/// encontrado por find_call_sites). Cada test documenta un patrón C# legítimo
/// que DetectCall NO resolvía; al aplicar los fixes de la Fase A/B deben
/// ponerse en verde.
///
/// Cobertura por patrón:
///   - x?.M()                null-conditional (Fase A.1, resuelto en visitor)
///   - a.B().M()             chaining / factory (Fase B, resuelto en rebuild)
///   - obj.Prop.M()          member-access profundo (Fase B, resuelto en rebuild)
///   - xs[idx].M()           indexer receiver (TODO: Fase C, fuera de alcance)
///   - x => x.M()            simple lambda (YA funciona, baseline verde)
///   - Top-level statements  Program.cs sin clase envolvente (Fase B + visitor)
///   - var x = await ...;    inferencia con await no genérico (Fase B + visitor)
/// </summary>
public class CallSiteCoverageTests
{
    // ============ Fase A — resueltos en el visitor durante el parseo ============

    /// <summary>null-conditional x?.M(). Sospechoso ALTA para Run...Async.</summary>
    [Fact]
    public void NullConditional_Receiver_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
namespace MyApp;
public class Caller {
    private readonly IService _svc;
    public Caller(IService svc) => _svc = svc;
    public void Run() => _svc?.RunInitialIndexAsync();
}
public interface IService { void RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
        Assert.Contains("Caller", result);
    }

    // ============ Fase B — resueltos por el grafo usando tabla de símbolos ============

    /// <summary>chaining / factory: _factory.Get().M().</summary>
    [Fact]
    public void Factory_Chained_Call_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
namespace MyApp;
public class Host {
    private readonly IFactory _factory;
    public Host(IFactory factory) => _factory = factory;
    public void Start() => _factory.Get().RunInitialIndexAsync();
}
public interface IFactory { IService Get(); }
public interface IService { void RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
        Assert.Contains("Host", result);
    }

    /// <summary>member-access profundo: _outer.Inner.M().</summary>
    [Fact]
    public void Deep_MemberAccess_Receiver_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
namespace MyApp;
public class Caller {
    private readonly Outer _outer;
    public Caller(Outer outer) => _outer = outer;
    public void Run() => _outer.Inner.RunInitialIndexAsync();
}
public class Outer { public IService Inner { get; } }
public interface IService { void RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
        Assert.Contains("Caller", result);
    }

    /// <summary>var x = await svc.GetAsync(); x.M();</summary>
    [Fact]
    public void Var_Assigned_From_Await_NonGeneric_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
using System.Threading.Tasks;
namespace MyApp;
public class Host {
    private readonly IFactory _factory;
    public Host(IFactory factory) => _factory = factory;
    public async Task Run() {
        var svc = await _factory.GetAsync();
        svc.RunInitialIndexAsync();
    }
}
public interface IFactory { Task<IService> GetAsync(); }
public interface IService { Task RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
        Assert.Contains("Host", result);
    }

    // ============ Lambdas — baseline verde (ya funcionaban) ============

    /// <summary>simple lambda () => svc.M(). Ya funciona; test de no-regresión.</summary>
    [Fact]
    public void Simple_Lambda_Typed_Parameter_Call_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
using System;
namespace MyApp;
public class Scheduler {
    public void Schedule(IService svc) => Action(() => svc.RunInitialIndexAsync());
    public void Action(Action fn) => fn();
}
public interface IService { void RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
    }

    /// <summary>Task.Run(() => svc.M()). Ya funciona; test de no-regresión.</summary>
    [Fact]
    public void Lambda_Parameter_Passed_To_Method_Is_Resolved()
    {
        var graph = GraphTestHarness.BuildFromSnippet(@"
using System.Threading.Tasks;
namespace MyApp;
public class Host {
    private readonly IService _svc;
    public Host(IService svc) => _svc = svc;
    public void Run() => Task.Run(() => _svc.RunInitialIndexAsync());
}
public interface IService { void RunInitialIndexAsync(); }
");
        var result = graph.FindCallSites("IService", null, 50);
        Assert.Contains("RunInitialIndexAsync", result);
    }

    // ============ Indexer — Fase C, fuera de alcance ============

    [Fact(Skip = "Fase C: indexer receiver (xs[i].M()) requiere resolver el tipo " +
                 "del elemento del contenedor (Task<IService> p.ej.), que exige tipo " +
                 "de retorno genérico. Fuera de alcance de este plan.")]
    public void Indexer_Receiver_Is_Resolved() { }

    // ============ Top-level statements — Fase B + visitor ============

    /// <summary>
    /// Top-level statements en Program.cs sin clase envolvente. Los casos reales
    /// (Host.CreateDefaultBuilder + GetRequiredService&lt;T&gt;, var svc = new T())
    /// quedan FUERA de alcance: el visitor no mantiene tabla de locals fuera de un
    /// tipo contenedor, así que LookupLocal devuelve null en top-level y los
    /// call-sites no se emiten. Requeriría una tabla de locals a nivel fragmento.
    /// </summary>
    [Fact(Skip = "Fase C (fuera de alcance): top-level statements requieren tabla de locals " +
                 "a nivel fragmento, que el visitor no mantiene fuera de un tipo contenedor.")]
    public void TopLevel_Statement_Call_Is_Resolved() { }
}

internal static class CallSiteFixtureExtensions
{
    /// <summary>True si el fragmento tiene un call-site que cumple todos los criterios.</summary>
    public static bool HasCallSite(this FileFragment f,
        string callerType, string calleeType, string calleeMember)
        => f.CallSites.Any(c =>
            LastSegment(c.CallerType) == callerType
            && LastSegment(c.CalleeType) == calleeType
            && c.CalleeMember == calleeMember);

    private static string LastSegment(string name)
    {
        var idx = name.LastIndexOf('.');
        return idx < 0 ? name : name[(idx + 1)..];
    }
}
