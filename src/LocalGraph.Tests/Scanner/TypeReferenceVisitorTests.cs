using LocalGraph.Graph;
using LocalGraph.Scanner;

namespace LocalGraph.Tests.Scanner;

/// <summary>
/// Verifica que <see cref="TypeReferenceVisitor"/> extrae correctamente cada patrón
/// (MediatR, Minimal API, DI, anidados, genéricos, routing, ambigüedad, falsos positivos).
/// Estos tests son la red de seguridad principal: cualquier refactor que rompa la
/// detección de un patrón debe hacerlos fallar aquí.
/// </summary>
public class TypeReferenceVisitorTests
{
    // -------- MediatR --------

    [Fact]
    public void MediatR_Detects_Send_Edge_From_Controller_To_Command()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using MediatR;
namespace MyApp;
public class UserController {
    private readonly IMediator _mediator;
    public UserController(IMediator m) => _mediator = m;
    public void Create() => _mediator.Send(new CreateUserCommand());
}
public class CreateUserCommand : IRequest<int> { }
")!;

        Assert.True(frag.HasRelation("UserController", "CreateUserCommand", EdgeRelation.Sends),
            "Expected Sends edge UserController -> CreateUserCommand");
    }

    [Fact]
    public void MediatR_Detects_HandledBy_Edge_From_Command_To_Handler()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using MediatR;
namespace MyApp;
public class CreateUserCommand : IRequest<int> { }
public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, int> {
    public int Handle(CreateUserCommand c) => 0;
}
")!;

        Assert.True(frag.HasRelation("CreateUserCommand", "CreateUserCommandHandler", EdgeRelation.HandledBy),
            "Expected HandledBy edge Command -> Handler");
    }

    [Fact]
    public void MediatR_Does_Not_Treat_IMediator_As_Noise_Node()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using MediatR;
namespace MyApp;
public class UserController {
    public UserController(IMediator m) { }
}
")!;
        // ctor-param: la inyección del propio IMediator debe quedar como arista CtorParam
        Assert.True(frag.HasRelation("UserController", "IMediator", EdgeRelation.CtorParam));
    }

    // -------- Minimal API --------

    [Fact]
    public void MinimalApi_Registers_Endpoint_And_Call_To_Service()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using Microsoft.AspNetCore.Builder;
namespace MyApp;
public static class Endpoints {
    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet(""/users"", (UserService svc, int id) => svc.Get(id));
    }
}
public class UserService { public object Get(int id) => null; }
")!;

        Assert.Contains(frag.Endpoints, e => e.Verb == "GET" && e.Route == "/users");
        // el handler llama a svc.Get(), donde svc es de tipo UserService
        Assert.Contains(frag.CallSites, c => c.CalleeType == "UserService" && c.CalleeMember == "Get");
    }

    // -------- DI --------

    [Theory]
    [InlineData("AddScoped", "scoped")]
    [InlineData("AddSingleton", "singleton")]
    [InlineData("AddTransient", "transient")]
    public void DI_Generic_Two_Arg_Form_Is_Captured(string method, string expectedLifetime)
    {
        var frag = GraphTestHarness.ParseSnippet($$"""
            using Microsoft.Extensions.DependencyInjection;
            namespace MyApp;
            public static class Cfg {
                public static void R(IServiceCollection s) => s.{{method}}<IFoo, Foo>();
            }
            public interface IFoo { }
            public class Foo : IFoo { }
            """)!;

        var binding = Assert.Single(frag.DiBindings);
        Assert.Equal("IFoo", binding.ServiceType);
        Assert.Equal("Foo", binding.ImplementationType);
        Assert.Equal(expectedLifetime, binding.Lifetime);
    }

    [Fact]
    public void DI_TypeOf_Form_Is_Captured()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using Microsoft.Extensions.DependencyInjection;
namespace MyApp;
public static class Cfg {
    public static void R(IServiceCollection s) => s.AddTransient(typeof(IFoo), typeof(Foo));
}
public interface IFoo { }
public class Foo : IFoo { }
")!;

        var binding = Assert.Single(frag.DiBindings);
        Assert.Equal("IFoo", binding.ServiceType);
        Assert.Equal("Foo", binding.ImplementationType);
        Assert.Equal("transient", binding.Lifetime);
    }

    [Fact]
    public void DI_Keyed_Form_Is_Captured()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using Microsoft.Extensions.DependencyInjection;
namespace MyApp;
public static class Cfg {
    public static void R(IServiceCollection s) => s.AddKeyedSingleton<ICache, MemoryCache>();
}
public interface ICache { }
public class MemoryCache : ICache { }
")!;

        var binding = Assert.Single(frag.DiBindings);
        Assert.Equal("ICache", binding.ServiceType);
        Assert.Equal("MemoryCache", binding.ImplementationType);
        Assert.Equal("singleton", binding.Lifetime);
    }

    // -------- Tipos anidados --------

    [Fact]
    public void Nested_Types_Get_Qualified_FQN()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
namespace MyApp;
public class OrderWorkflow {
    public class Handler {
        public void Run(InnerService svc) => svc.Do();
    }
    public class OtherHandler { }
}
public class InnerService { public void Do() { } }
")!;

        var handlerNode = Assert.Single(frag.Nodes, n => n.Name.EndsWith(".Handler"));
        Assert.Equal("MyApp.OrderWorkflow.Handler", handlerNode.Name);

        var otherHandlerNode = Assert.Single(frag.Nodes, n => n.Name.EndsWith(".OtherHandler"));
        Assert.Equal("MyApp.OrderWorkflow.OtherHandler", otherHandlerNode.Name);

        // Los dos Handler no se fusionan: hay exactamente dos con sufijo .Handler / .OtherHandler
        Assert.Equal(2, frag.Nodes.Count(n => n.Name.StartsWith("MyApp.OrderWorkflow.")));
    }

    // -------- Genéricos contenedor --------

    [Fact]
    public void Generic_Container_Is_Filtered_But_Type_Argument_Is_Kept()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using System.Collections.Generic;
using System.Threading.Tasks;
namespace MyApp;
public class Repo {
    public Task<List<Foo>> GetAllAsync() => null;
}
public class Foo { }
")!;

        // Task y List NO deben aparecer como nodos destino
        Assert.DoesNotContain(frag.Edges, e => e.To == "Task");
        Assert.DoesNotContain(frag.Edges, e => e.To == "List");
        // Foo sí, como ReturnType
        Assert.True(frag.HasRelation("Repo", "Foo", EdgeRelation.ReturnType));
    }

    [Fact]
    public void IEnumerable_Type_Argument_Is_Kept()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using System.Collections.Generic;
namespace MyApp;
public class Repo {
    public IEnumerable<Bar> GetBars() => null;
}
public class Bar { }
")!;

        Assert.True(frag.HasRelation("Repo", "Bar", EdgeRelation.ReturnType));
    }

    // -------- Routing ASP.NET --------

    [Fact]
    public void Controller_Route_Combines_Prefix_And_Action_With_ControllerToken()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using Microsoft.AspNetCore.Mvc;
namespace MyApp.Controllers;
[Route(""api/[controller]"")]
public class PayrollController : ControllerBase {
    [HttpGet(""{id}"")]
    public object Get(int id) => null;
}
")!;

        var ep = Assert.Single(frag.Endpoints);
        Assert.Equal("GET", ep.Verb);
        // NOTA: ASP.NET Core lowercases [controller] por convención al hacer routing real,
        // pero LocalGraph NO lo hace (sustituye el token tal cual). El test documenta el
        // comportamiento actual; si se arregla el lowercase, actualizar la expectativa.
        Assert.Equal("/api/Payroll/{id}", ep.Route);
    }

    [Fact]
    public void Controller_Post_Route_Is_Combined()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
using Microsoft.AspNetCore.Mvc;
namespace MyApp.Controllers;
[Route(""api/[controller]"")]
public class PayrollController : ControllerBase {
    [HttpPost(""calculate"")]
    public object Calculate() => null;
}
")!;

        var ep = Assert.Single(frag.Endpoints);
        Assert.Equal("POST", ep.Verb);
        // Ver nota en Controller_Route_Combines_Prefix_And_Action_With_ControllerToken.
        Assert.Equal("/api/Payroll/calculate", ep.Route);
    }

    // -------- Ambigüedad de nombres --------

    [Fact]
    public void Ambiguous_Names_In_Different_Namespaces_Do_Not_Fuse()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
namespace Sales { public class Order { } }
namespace Purchasing { public class Order { } }
")!;

        var orderNodes = frag.Nodes.Where(n => n.Name.EndsWith(".Order")).ToList();
        Assert.Equal(2, orderNodes.Count);
        Assert.Contains(orderNodes, n => n.Name == "Sales.Order");
        Assert.Contains(orderNodes, n => n.Name == "Purchasing.Order");
    }

    // -------- Falsos positivos de Send --------

    [Fact]
    public void Send_On_NonBus_Receiver_Is_Not_Treated_As_MediatR()
    {
        var frag = GraphTestHarness.ParseSnippet(@"
namespace MyApp;
public class SmtpClient {
    public void Send(EmailMessage msg) { }
}
public class Caller {
    private readonly SmtpClient _smtp;
    public Caller(SmtpClient smtp) => _smtp = smtp;
    public void Run(EmailMessage msg) => _smtp.Send(msg);
}
public class EmailMessage { }
")!;

        // NO debe haber arista Sends desde Caller hacia EmailMessage (Send sobre SmtpClient)
        Assert.DoesNotContain(frag.Edges,
            e => e.Relation == EdgeRelation.Sends && e.To == "EmailMessage");
    }

    [Fact]
    public void Send_On_IMediator_Receiver_Is_Still_Treated_As_MediatR()
    {
        // Test de no-regresión del fix anterior: el caso bueno (receptor IMediator)
        // debe seguir generando la arista Sends.
        var frag = GraphTestHarness.ParseSnippet(@"
using MediatR;
namespace MyApp;
public class UserController {
    private readonly IMediator _mediator;
    public UserController(IMediator m) => _mediator = m;
    public void Create() => _mediator.Send(new CreateUserCommand());
}
public class CreateUserCommand { }
")!;

        Assert.True(frag.HasRelation("UserController", "CreateUserCommand", EdgeRelation.Sends),
            "Send sobre IMediator debe seguir generando arista Sends");
    }
}
