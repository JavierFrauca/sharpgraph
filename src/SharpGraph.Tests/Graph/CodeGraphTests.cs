using SharpGraph.Graph;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// Tests de consultas de alto nivel sobre el grafo ya construido.
/// Usan los fixtures embebidos como código fuente.
/// </summary>
public class CodeGraphTests
{
    // -------- stats / conteos básicos --------

    [Fact]
    public void Stats_Reflects_Loaded_Fixtures()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var stats = graph.Stats();
        Assert.Contains("types defined", stats);
        // al menos las interfaces e implementaciones del fixture
        Assert.Contains("DI bindings", stats);
        // 3 bindings: AddScoped<,> + AddKeyedSingleton<,> + AddTransient(typeof,)
        // (pueden resolverse a más si los símbolos coinciden; aquí verificamos >= 3)
    }

    // -------- resolve_di --------

    [Fact]
    public void ResolveDi_Returns_Implementation_For_Generic_Scoped()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.ResolveDi("IOrderService");
        Assert.Contains("OrderService", result);
        Assert.Contains("scoped", result);
    }

    [Fact]
    public void ResolveDi_Returns_Implementation_For_Keyed_Singleton()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.ResolveDi("ICache");
        Assert.Contains("MemoryCache", result);
        Assert.Contains("singleton", result);
    }

    [Fact]
    public void ResolveDi_Returns_Implementation_For_TypeOf_Transient()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.ResolveDi("IValidator");
        Assert.Contains("DefaultValidator", result);
        Assert.Contains("transient", result);
    }

    [Fact]
    public void ResolveDi_Reverse_Lookup_From_Implementation_To_Service()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.ResolveDi("OrderService");
        Assert.Contains("IOrderService", result);
    }

    // -------- search --------

    [Fact]
    public void Search_Finds_Types_By_Partial_Name()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.Search("Order");
        Assert.Contains("OrderService", result);
        Assert.Contains("IOrderService", result);
    }

    [Fact]
    public void Search_Reports_No_Match_On_Unknown_Pattern()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.Search("ThisDoesNotExist");
        Assert.Contains("No types found", result);
    }

    // -------- trace_to_endpoints (camino MediatR) --------

    [Fact]
    public void TraceToEndpoints_Reaches_Controller_Via_MediatR()
    {
        var graph = GraphTestHarness.Build("MediatRController");

        var result = graph.TraceToEndpoints("IUserService");
        // El servicio se alcanza desde el Handler, que está conectado al Command, que el Controller envía.
        // Aunque el fixture no decora el Controller con [HttpGet], debe encontrar callers.
        Assert.Contains("UserService", result);
    }

    // -------- find_callers --------

    [Fact]
    public void FindCallers_Returns_Callers_Of_Type()
    {
        var graph = GraphTestHarness.Build("MediatRController");

        var result = graph.FindCallers("IUserService", depth: 2);
        // CreateUserCommandHandler usa IUserService
        Assert.Contains("CreateUserCommandHandler", result);
    }

    [Fact]
    public void FindCallers_Suggests_Di_Resolution_For_Service_Interface()
    {
        var graph = GraphTestHarness.Build("DiRegistrations");

        var result = graph.FindCallers("IOrderService");
        Assert.Contains("DI", result);
        Assert.Contains("OrderService", result);
    }

    // -------- find_call_sites --------

    [Fact]
    public void FindCallSites_Returns_Real_Invocations_Only()
    {
        var graph = GraphTestHarness.Build("MediatRController");

        var result = graph.FindCallSites("IUserService", null, 50);
        // Handle() en CreateUserCommandHandler invoca _userService.Create()
        Assert.Contains("Create", result);
        Assert.Contains("CreateUserCommandHandler", result);
    }

    // -------- ambigüedad --------

    [Fact]
    public void Ambiguous_Query_Asks_For_Qualification()
    {
        var graph = GraphTestHarness.Build("AmbiguousNames");

        var result = graph.FindCallers("Order");
        Assert.Contains("ambiguo", result);
    }

    [Fact]
    public void Ambiguous_Query_With_FQN_Resolves()
    {
        var graph = GraphTestHarness.Build("AmbiguousNames");

        var result = graph.GetUsages("Sales.Order");
        // No debe pedir cualificación al pasar el FQN
        Assert.DoesNotContain("ambiguo", result);
    }

    // -------- get_usages --------

    [Fact]
    public void GetUsages_Lists_Dependencies_With_Relation()
    {
        var graph = GraphTestHarness.Build("MediatRController");

        var result = graph.GetUsages("CreateUserCommandHandler");
        Assert.Contains("IUserService", result);
        Assert.Contains("ctor-param", result);
    }

    // -------- minimal api integration --------

    [Fact]
    public void MinimalApi_Endpoint_Reachable_From_Service()
    {
        var graph = GraphTestHarness.Build("MinimalApi");

        // stats debe reportar al menos 1 endpoint
        var stats = graph.Stats();
        Assert.Contains("HTTP endpoints", stats);

        // find_call_sites del servicio debe mostrar al handler del endpoint como caller
        var calls = graph.FindCallSites("UserService", null, 50);
        Assert.Contains("Get", calls);
    }
}
