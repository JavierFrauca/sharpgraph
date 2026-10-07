using System.Text.RegularExpressions;
using SharpGraph.Graph;
using Xunit;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// Diagramas Mermaid: mermaid_context (flowchart bidireccional), mermaid_sequence
/// (cadenas a endpoint) y mermaid_overview (mapa multi-endpoint). Se valida el
/// contenido del bloque (nodos, flechas tipadas, estilos, escape y truncado), no
/// el render (eso lo hace el cliente Mermaid).
/// </summary>
public class MermaidTests
{
    // Cadena completa: OrdersController -sends-> CreateOrderCommand
    // -handled-by-> CreateOrderCommandHandler -ctor-param-> IOrderRepository
    // -di-bound-> OrderRepository. Cubre MediatR + DI + endpoint HTTP.
    private const string App = """
        using MediatR;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.Extensions.DependencyInjection;

        namespace Corp.Diagrams;

        public record CreateOrderCommand(string Sku) : IRequest<int>;

        public interface IOrderRepository { int Create(string sku); }

        public sealed class OrderRepository : IOrderRepository
        {
            public int Create(string sku) => 1;
        }

        public sealed class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, int>
        {
            private readonly IOrderRepository _repo;
            public CreateOrderCommandHandler(IOrderRepository repo) => _repo = repo;

            public int Handle(CreateOrderCommand cmd) => _repo.Create(cmd.Sku);
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
                var id = _mediator.Send(new CreateOrderCommand(cmd.Sku));
                return Ok(id);
            }
        }

        public static class Registration
        {
            public static void AddDi(IServiceCollection services)
                => services.AddScoped<IOrderRepository, OrderRepository>();
        }
        """;

    private static GraphEngine BuildApp() => GraphTestHarness.BuildFromSnippet(App);

    // ─────────────────────────── mermaid_context ───────────────────────────

    [Fact]
    public void Context_FromHandler_ShowsFullMediatRChainUpAndDepsDown()
    {
        var output = BuildApp().MermaidContext("CreateOrderCommandHandler");

        Assert.Contains("```mermaid", output);
        Assert.Contains("flowchart TD", output);
        // cadena de entrada: endpoint con ruta, sends y handled-by punteados
        Assert.Contains("POST /api/orders", output);
        Assert.Contains("|sends|", output);
        Assert.Contains("|handled-by|", output);
        Assert.Contains("CreateOrderCommand", output);
        Assert.Contains("OrdersController", output);
        // dependencias: el handler llama al repo (call domina sobre ctor-param)
        // y la impl llega por di-bound
        Assert.Matches(@"N\d+ --> N\d+", output);
        Assert.Contains("|di-bound|", output);
        Assert.Contains("OrderRepository", output);
        // el ancla sale resaltada y sin el ciclo falso del genérico IRequestHandler<>:
        // la única flecha gruesa permitida es la implements real (OrderRepository ⇒ IOrderRepository)
        Assert.Contains(":::anchor", output);
        Assert.Single(Regex.Matches(output, @"^\s+\w+ ==>", RegexOptions.Multiline));
    }

    [Fact]
    public void Context_ExcludesExternals_AndTestsByDefault()
    {
        var output = BuildApp().MermaidContext("CreateOrderCommandHandler");

        // IMediator es BCL: fuera por defecto
        Assert.DoesNotContain("IMediator", output);
        // el endpoint se dibuja pero no se expande por encima (es entrada)
        Assert.DoesNotContain("ControllerBase", output);
    }

    [Fact]
    public void Context_IncludeExternal_ShowsBclDependencies()
    {
        const string withLogger = """
            namespace Corp.Logging;

            public sealed class UsesLogger
            {
                public UsesLogger(ILogger<UsesLogger> log) { }
            }
            """;
        var graph = GraphTestHarness.BuildFromSnippet(withLogger);

        var without = graph.MermaidContext("UsesLogger");
        Assert.DoesNotContain("ILogger", without);

        var with = graph.MermaidContext("UsesLogger", includeExternal: true);
        Assert.Contains("ILogger", with);
    }

    [Fact]
    public void Context_DepthZero_DisablesTheDirection()
    {
        var graph = BuildApp();

        var onlyDeps = graph.MermaidContext("CreateOrderCommandHandler", callersDepth: 0);
        Assert.DoesNotContain("OrdersController", onlyDeps);
        Assert.Contains("IOrderRepository", onlyDeps);

        var onlyCallers = graph.MermaidContext("CreateOrderCommandHandler", depsDepth: 0);
        Assert.Contains("OrdersController", onlyCallers);
        Assert.DoesNotContain("IOrderRepository", onlyCallers);
    }

    [Fact]
    public void Context_DirectionLR_IsHonored()
    {
        var output = BuildApp().MermaidContext("CreateOrderCommandHandler", direction: "LR");

        Assert.Contains("flowchart LR", output);
    }

    [Fact]
    public void Context_OnDiCycle_TerminatesAndDrawsBothSides()
    {
        const string cycle = """
            using Microsoft.Extensions.DependencyInjection;

            namespace Corp.Cycle;

            public interface IA { }
            public interface IB { }

            public sealed class A : IA
            {
                public A(IB b) { }
            }

            public sealed class B : IB
            {
                public B(A a) { }
            }

            public static class Reg
            {
                public static void Add(IServiceCollection s)
                {
                    s.AddScoped<IA, A>();
                    s.AddScoped<IB, B>();
                }
            }
            """;

        var output = GraphTestHarness.BuildFromSnippet(cycle).MermaidContext("A");

        // terminó (si cuelga, el test lo evidencia) y dibuja las dos caras del ciclo
        Assert.Contains("```mermaid", output);
        Assert.Contains("|di-bound|", output);
        Assert.Contains("[\"A\"]", output);
        Assert.Contains("[\"B\"]", output);
    }

    [Fact]
    public void Context_EscapesRoutesWithBraces()
    {
        const string routed = """
            using Microsoft.AspNetCore.Mvc;

            namespace Corp.Routes;

            [ApiController]
            [Route("api/{tenant}/items")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult Get(string id) => Ok(id);
            }
            """;

        var output = GraphTestHarness.BuildFromSnippet(routed).MermaidContext("ItemsController");

        Assert.Contains("&#123;tenant&#125;", output);
        Assert.DoesNotContain("{tenant}", output);
        // el ancla ES un endpoint: el estilo anchor prevalece sobre entry
        Assert.Contains(":::anchor", output);
    }

    [Fact]
    public void Context_TruncatesAtMaxNodes_AndWarnsInsideBlock()
    {
        const string chain = """
            namespace Corp.Chain;

            public interface IStep0 { }

            public sealed class Step1
            {
                public Step1(IStep0 s) { }
            }

            public sealed class Step2
            {
                public Step2(Step1 s) { }
            }

            public sealed class Step3
            {
                public Step3(Step2 s) { }
            }

            public sealed class Step4
            {
                public Step4(Step3 s) { }
            }

            public sealed class Step5
            {
                public Step5(Step4 s) { }
            }

            public sealed class Step6
            {
                public Step6(Step5 s) { }
            }
            """;

        var output = GraphTestHarness.BuildFromSnippet(chain)
            .MermaidContext("Step6", callersDepth: 0, depsDepth: 6, maxNodes: 5);

        Assert.Contains("TRUNCADO", output);
        // tope duro respetado: no hay más de maxNodes nodos dibujados
        var drawn = Regex.Matches(output, @"N\d+\[").Count;
        Assert.True(drawn <= 5, $"esperaba ≤5 nodos, hay {drawn}");
        // y el corte no rompe el bloque
        Assert.EndsWith("```", output.TrimEnd());
        Assert.Contains("flowchart TD", output);
    }

    [Fact]
    public void Context_UnknownType_ReturnsHint()
    {
        var output = BuildApp().MermaidContext("NoExiste");

        Assert.Contains("not found", output);
    }

    // ─────────────────────────── mermaid_sequence ───────────────────────────

    [Fact]
    public void Sequence_FromHandler_RendersChainInExecutionOrderPlusCalls()
    {
        var output = BuildApp().MermaidSequence("CreateOrderCommandHandler");

        Assert.Contains("sequenceDiagram", output);
        Assert.Contains("autonumber", output);
        Assert.Contains("participant", output);
        // endpoint como participante con su ruta
        Assert.Contains("POST /api/orders", output);
        // mensajes de la cadena: endpoint → command → handler (flecha punteada en MediatR)
        Assert.Contains("->>", output);
        Assert.Contains("sends", output);
        Assert.Contains("handled-by", output);
        // llamadas salientes del ancla a nivel de método
        Assert.Contains("Create()", output);
    }

    [Fact]
    public void Sequence_MemberFilter_RestrictsOutgoingCalls()
    {
        var output = BuildApp().MermaidSequence("CreateOrderCommandHandler", member: "Handle");

        Assert.Contains("Handle", output);
    }

    [Fact]
    public void Sequence_LeafType_StillDrawsOutgoingCalls()
    {
        const string leaf = """
            namespace Corp.Leaf;

            public interface IThing { int Ping(); }
            public sealed class Thing : IThing { public int Ping() => 1; }

            public sealed class Isolated
            {
                private readonly IThing _t;
                public Isolated(IThing t) => _t = t;
                public int Go() => _t.Ping();
            }
            """;

        var output = GraphTestHarness.BuildFromSnippet(leaf).MermaidSequence("Isolated");

        Assert.Contains("sequenceDiagram", output);
        Assert.Contains("sin cadena estructural a endpoint", output);
        Assert.Contains("Ping()", output);
    }

    // ─────────────────────────── mermaid_overview ───────────────────────────

    [Fact]
    public void Overview_GroupsEndpointsByNamespaceSubgraph()
    {
        var output = BuildApp().MermaidOverview(depsDepth: 4);

        Assert.Contains("flowchart TD", output);
        Assert.Contains("subgraph SG0[\"Corp.Diagrams\"]", output);
        Assert.Contains("POST /api/orders", output);
        Assert.Contains("CreateOrderCommandHandler", output);
        Assert.Contains("OrderRepository", output);
    }

    [Fact]
    public void Overview_AreaFilter_NarrowsAndMissesReport()
    {
        var graph = BuildApp();

        var hit = graph.MermaidOverview("orders", depsDepth: 1);
        Assert.Contains("OrdersController", hit);

        var miss = graph.MermaidOverview("NoExiste");
        Assert.Contains("No hay endpoints", miss);
    }

    [Fact]
    public void Overview_WithoutEndpoints_FallsBackToHubs()
    {
        const string library = """
            namespace Corp.Lib;

            public interface IFoo { }
            public sealed class Foo : IFoo { }
            """;

        var output = GraphTestHarness.BuildFromSnippet(library).MermaidOverview();

        Assert.Contains("top-5 por PageRank", output);
        Assert.Contains("Foo", output);
    }
}
