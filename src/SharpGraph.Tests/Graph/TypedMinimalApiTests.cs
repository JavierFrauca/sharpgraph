using SharpGraph.Graph;
using Xunit;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// Minimal APIs TIPADAS por grupos (estilo CleanArchitecture v2):
/// groupBuilder.MapGet(GetTodoLists) — método-grupo sin ruta, o con la ruta como
/// SEGUNDO argumento. Regresión del hallazgo 2026-10-02: este estilo no se
/// detectaba y trace_to_endpoints/impact devolvían "0 endpoints" en el
/// CleanArchitecture moderno.
/// </summary>
public class TypedMinimalApiTests
{
    private static GraphEngine Build() => GraphTestHarness.Build("TypedMinimalApi");

    [Fact]
    public void Endpoints_Are_Detected_On_The_Declaring_Class()
    {
        var graph = Build();

        // la clase Orders debe tener sus 4 endpoints con el método real
        var search = graph.Search("Orders");
        Assert.Contains("[ENDPOINT]", search);
        var callers = graph.FindCallers("IOrderService");
        Assert.Contains("Orders", callers);
    }

    [Fact]
    public void Trace_Reaches_Typed_Minimal_Endpoints()
    {
        var graph = Build();
        var output = graph.TraceToEndpoints("IOrderService");

        // cadena: IOrderService ← ListOrdersHandler ← ListOrdersQuery ← Orders [GET /]
        Assert.Contains("Orders.GetOrders", output);
        Assert.Contains("ListOrdersQuery", output);
        // rutas: relativa "/" cuando no hay literal, y el literal como 2º argumento
        Assert.Contains("GET /", output);
        Assert.Contains("PUT {id}", output);
    }

    [Fact]
    public void Impact_Counts_Typed_Minimal_Endpoints_At_Risk()
    {
        var graph = Build();
        var output = graph.Impact("IOrderService");

        Assert.Contains("4 endpoints HTTP", output);
        Assert.Contains("Endpoints en riesgo", output);
        Assert.Contains("[PUT {id}] Orders.UpdateOrder", output);
    }

    [Fact]
    public void Impact_Relation_Labels_Describe_The_Dependency()
    {
        var graph = Build();
        var output = graph.Impact("IOrderService");

        // el handler aparece PORQUE inyecta el servicio (arista handler→servicio)
        Assert.Contains("ListOrdersHandler [ctor-param]", output);
        // la query aparece porque su handler la gestiona (arista query→handler)
        Assert.Contains("ListOrdersQuery [handled-by]", output);
    }
}
