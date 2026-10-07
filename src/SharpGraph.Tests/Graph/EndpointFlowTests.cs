using System.Text.Json;
using SharpGraph.Graph;
using Xunit;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// endpoint_flow y list_endpoints: el subgrafo descendente desde un endpoint HTTP
/// que consume la extensión SharpGraph Flow. Se valida el JSON estructurado
/// (capas endpoint→controller→command→handler→servicios→impls DI), el marcado de
/// back-edges en dependencias compartidas, el flag infra para ILogger y el
/// presupuesto de nodos.
/// </summary>
public class EndpointFlowTests
{
    // Cadena completa con TODO lo que pinta el diagrama de la extensión:
    // endpoint → controller -sends-> command -handled-by-> handler
    //   → IOrderRepository/IEmailService (DI) → implementaciones → IUnitOfWork COMPARTIDO
    //   (back-edge) + ILogger (infra). Réplica del caso Orders del prototipo.
    private const string App = """
        using MediatR;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;

        namespace Corp.Flow;

        public record CreateOrderCommand(string Sku) : IRequest<int>;

        public sealed record CreateOrderResponse(int Id);

        public record UpdateOrderCommand(int Id, string Sku) : IRequest<int>;

        public interface IUnitOfWork { int Save(); }

        public interface IOrderRepository { int Create(string sku); }

        public interface IEmailService { void Send(string to); }

        public sealed class UnitOfWork : IUnitOfWork
        {
            public int Save() => 1;
        }

        public sealed class OrderRepository : IOrderRepository
        {
            private readonly IUnitOfWork _uow;
            public OrderRepository(IUnitOfWork uow) => _uow = uow;
            public int Create(string sku) => _uow.Save();
        }

        public sealed class EmailService : IEmailService
        {
            private readonly IUnitOfWork _uow;
            public EmailService(IUnitOfWork uow) => _uow = uow;
            public void Send(string to) => _ = _uow.Save();
        }

        public sealed class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, int>
        {
            private readonly IOrderRepository _repo;
            private readonly IEmailService _email;
            private readonly ILogger<CreateOrderCommandHandler> _logger;
            public CreateOrderCommandHandler(IOrderRepository repo, IEmailService email,
                ILogger<CreateOrderCommandHandler> logger)
                => (_repo, _email, _logger) = (repo, email, logger);

            public CreateOrderResponse Handle(CreateOrderCommand cmd)
            {
                _logger.LogInformation("creating {Sku}", cmd.Sku);
                var id = _repo.Create(cmd.Sku);
                _email.Send("client@corp.test");
                return new CreateOrderResponse(id);
            }
        }

        [ApiController]
        [Route("api/orders")]
        public sealed class OrdersController : ControllerBase
        {
            private readonly IMediator _mediator;
            public OrdersController(IMediator mediator) => _mediator = mediator;

            [HttpPost]
            public IActionResult Create(CreateOrderCommand cmd)
            {
                var id = _mediator.Send(cmd);
                return Ok(id);
            }

            [HttpPut("{id}")]
            public IActionResult Update(int id, UpdateOrderCommand cmd)
            {
                _mediator.Send(cmd);
                return NoContent();
            }
        }

        public static class Registration
        {
            public static void AddDi(IServiceCollection services)
            {
                services.AddScoped<IUnitOfWork, UnitOfWork>();
                services.AddScoped<IOrderRepository, OrderRepository>();
                services.AddScoped<IEmailService, EmailService>();
            }
        }
        """;

    private static GraphEngine BuildApp() => GraphTestHarness.BuildFromSnippet(App);

    private static JsonElement Flow(GraphEngine graph, string endpoint, int maxNodes = 80)
        => JsonDocument.Parse(graph.EndpointFlow(endpoint, maxNodes: maxNodes)).RootElement;

    private static List<(int Id, string Kind, string Name, bool Infra)> NodeRows(JsonElement flow)
        => flow.GetProperty("nodes").EnumerateArray()
            .Select(n => (n.GetProperty("id").GetString()!.Substring(1) is { } s ? int.Parse(s) : -1,
                          n.GetProperty("kind").GetString()!,
                          n.GetProperty("name").GetString()!,
                          n.GetProperty("infra").GetBoolean()))
            .ToList();

    private static List<(string From, string To, string Rel, bool Back)> EdgeRows(JsonElement flow)
        => flow.GetProperty("edges").EnumerateArray()
            .Select(e => (e.GetProperty("from").GetString()!,
                          e.GetProperty("to").GetString()!,
                          e.GetProperty("relation").GetString()!,
                          e.GetProperty("back").GetBoolean()))
            .ToList();

    private static string KindOf(JsonElement flow, string name)
        => flow.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("name").GetString() == name)
            .GetProperty("kind").GetString()!;

    // ─────────────────────────── list_endpoints ───────────────────────────

    [Fact]
    public void ListEndpoints_ReturnsCatalogWithFileAndLine()
    {
        var json = JsonDocument.Parse(BuildApp().ListEndpoints()).RootElement;

        Assert.True(json.GetProperty("count").GetInt32() >= 1);
        var ep = json.GetProperty("endpoints").EnumerateArray().First();
        Assert.Equal("POST", ep.GetProperty("verb").GetString());
        Assert.Equal("/api/orders", ep.GetProperty("route").GetString());
        Assert.Equal("OrdersController", ep.GetProperty("controllerName").GetString());
        Assert.Equal("snippet0.cs", ep.GetProperty("file").GetString());
        Assert.True(ep.GetProperty("line").GetInt32() > 0);
    }

    // ─────────────────────────── endpoint_flow ───────────────────────────

    [Fact]
    public void Flow_FromVerbRoute_DescendsAllLayers()
    {
        var flow = Flow(BuildApp(), "POST /api/orders");

        Assert.Null(flow.TryGetProperty("error", out _) ? "error" : null);
        Assert.Equal("POST /api/orders", flow.GetProperty("endpoint").GetString());
        Assert.Equal("N0", flow.GetProperty("root").GetString());
        Assert.Equal("endpoint", flow.GetProperty("nodes").EnumerateArray().First()
            .GetProperty("kind").GetString());

        Assert.Equal("controller", KindOf(flow, "OrdersController"));
        Assert.Equal("command", KindOf(flow, "CreateOrderCommand"));
        Assert.Equal("handler", KindOf(flow, "CreateOrderCommandHandler"));
        Assert.Equal("interface", KindOf(flow, "IOrderRepository"));
        Assert.Equal("implementation", KindOf(flow, "OrderRepository"));

        var rels = EdgeRows(flow).Select(e => e.Rel).ToList();
        Assert.Contains("sends", rels);
        Assert.Contains("handled-by", rels);
        Assert.Contains("di-bound", rels);

        // metadatos de navegación: los nodos de la solución llevan file + línea;
        // los externos (ILogger, ControllerBase…) no tienen fichero local
        var nodes = flow.GetProperty("nodes").EnumerateArray().ToList();
        Assert.All(nodes, n =>
        {
            var file = n.GetProperty("file");
            if (file.ValueKind == JsonValueKind.String)
            {
                Assert.True(file.GetString()!.Length > 0);
                Assert.True(n.GetProperty("line").GetInt32() > 0);
            }
            else
            {
                Assert.Equal(0, n.GetProperty("line").GetInt32());
            }
        });

        // el bloque mermaid viene incluido para export
        Assert.Contains("flowchart TD", flow.GetProperty("mermaid").GetString());
        Assert.Contains("|sends|", flow.GetProperty("mermaid").GetString());
    }

    [Fact]
    public void Flow_MarksBackEdge_ForSharedDependency()
    {
        var flow = Flow(BuildApp(), "POST /api/orders");
        var edges = EdgeRows(flow);
        var nodes = flow.GetProperty("nodes").EnumerateArray().ToList();

        // IUnitOfWork aparece UNA sola vez y la segunda referencia es back-edge
        Assert.Equal(1, NodeRows(flow).Count(n => n.Name == "IUnitOfWork"));
        var uowId = nodes.First(n => n.GetProperty("name").GetString() == "IUnitOfWork")
            .GetProperty("id").GetString();
        Assert.Contains(edges, e => e.Back && e.To == uowId);
    }

    [Fact]
    public void Flow_FlagsLoggerAsInfra_AndMediator()
    {
        var flow = Flow(BuildApp(), "POST /api/orders");

        var logger = flow.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("name").GetString()!.Contains("Logger"));
        Assert.True(logger.GetProperty("infra").GetBoolean());
        Assert.Equal("infra", logger.GetProperty("kind").GetString());

        var mediator = flow.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("name").GetString() == "IMediator");
        Assert.True(mediator.GetProperty("infra").GetBoolean());
    }

    [Fact]
    public void Flow_ResolvesByControllerName_AndByRoute()
    {
        var byName = Flow(BuildApp(), "OrdersController");
        Assert.Equal(2, byName.GetProperty("matchedEndpoints").GetInt32()); // POST + PUT

        var byRoute = Flow(BuildApp(), "/api/orders");
        Assert.Equal("POST /api/orders", byRoute.GetProperty("endpoint").GetString());
    }

    [Fact]
    public void Flow_TruncatesAtNodeBudget()
    {
        var flow = Flow(BuildApp(), "POST /api/orders", maxNodes: 5);

        Assert.True(flow.GetProperty("truncated").GetBoolean());
        Assert.True(flow.GetProperty("omitted").GetInt32() > 0);
        Assert.Equal(5, flow.GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public void Flow_SingleEndpointMode_ExcludesOtherActionsOfSameController()
    {
        // clic en UN endpoint: del controller solo sale SU método de acción —
        // no los commands de las demás acciones del mismo controller
        var flow = JsonDocument.Parse(BuildApp().EndpointFlow("POST /api/orders")).RootElement;

        Assert.Equal("POST /api/orders", flow.GetProperty("endpoint").GetString());
        Assert.DoesNotContain(flow.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("name").GetString() == "UpdateOrderCommand");
        Assert.Contains(flow.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("name").GetString() == "CreateOrderCommand");
    }

    [Fact]
    public void Flow_FromControllerName_IncludesAllItsActions()
    {
        // clic en el CONTROLADOR (nombre): el batiburrido completo — todas sus acciones
        var flow = JsonDocument.Parse(BuildApp().EndpointFlow("OrdersController")).RootElement;

        var names = flow.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("CreateOrderCommand", names);
        Assert.Contains("UpdateOrderCommand", names);
        Assert.Equal(2, flow.GetProperty("matchedEndpoints").GetInt32());
    }

    [Fact]
    public void Flow_UnknownEndpoint_ReturnsJsonError()
    {
        var result = BuildApp().EndpointFlow("POST /nope");

        var json = JsonDocument.Parse(result).RootElement;
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("list_endpoints", error.GetString());
    }

    [Fact]
    public void Flow_WithDtosFlag_IncludesContractsAsLeafNodes()
    {
        var graph = BuildApp();

        // por defecto los contratos (ParamType/ReturnType) están fuera
        var without = JsonDocument.Parse(graph.EndpointFlow("POST /api/orders")).RootElement;
        Assert.DoesNotContain(without.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("kind").GetString() == "dto");

        // con includeDtos el contrato de salida entra como nodo hoja kind=dto
        var with = JsonDocument.Parse(graph.EndpointFlow("POST /api/orders", includeDtos: true)).RootElement;
        var dto = with.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("name").GetString() == "CreateOrderResponse");
        Assert.Equal("dto", dto.GetProperty("kind").GetString());
        Assert.False(dto.GetProperty("infra").GetBoolean());
        // y el comando SIGUE clasificándose como command (no lo pisa el dto)
        Assert.Contains(with.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("kind").GetString() == "command" && n.GetProperty("name").GetString() == "CreateOrderCommand");
    }
}
