using SharpGraph.Graph;
using Xunit;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// impact(): radio de impacto transitivo con propagación DI, resumen por
/// niveles, endpoints en riesgo y tests que cubren el área.
/// </summary>
public class ImpactTests
{
    // Cadena: Repo0 (impl) ←DI— IRepo0 ←ctor— Service0 ←call— Controller0 [endpoint]
    // y un test que referencia Service0 (debe listarse aparte).
    private const string Core = """
        using System;
        using MediatR;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.Extensions.DependencyInjection;

        namespace Corp.Billing;

        public interface IRepo0 { int Find(int id); }

        public sealed class Repo0 : IRepo0
        {
            public int Find(int id) => id;
        }

        public sealed class Service0
        {
            private readonly IRepo0 _repo;
            public Service0(IRepo0 repo) => _repo = repo;
            public decimal Compute(int id) => _repo.Find(id) * 1.21m;
        }

        [ApiController]
        [Route("api/billing")]
        public sealed class Controller0 : ControllerBase
        {
            private readonly Service0 _svc;
            public Controller0(Service0 svc) => _svc = svc;

            [HttpPost("compute")]
            public IActionResult Compute(int id) => Ok(_svc.Compute(id));
        }

        public static class Registration
        {
            public static void AddBilling(IServiceCollection services)
                => services.AddScoped<IRepo0, Repo0>();
        }
        """;

    private const string Tests = """
        using Xunit;

        namespace Corp.Billing.Tests;

        public class Service0Tests
        {
            private readonly Corp.Billing.Service0 _svc = null!;

            [Fact]
            public void Compute_ok() { }
        }
        """;

    private static GraphEngine Build() => GraphTestHarness.BuildFromSnippet(Core, Tests);

    [Fact]
    public void Impact_FromImplementation_PropagatesThroughDiInterface()
    {
        var graph = Build();
        var output = graph.Impact("Repo0");

        // nivel 1: la interfaz que implementa/registra — cambiar la impl la afecta
        Assert.Contains("Nivel 1: IRepo0", output);
        // nivel 2: el consumidor estructural de la interfaz
        Assert.Contains("Nivel 2: Service0", output);
    }

    [Fact]
    public void Impact_Lists_Endpoints_At_Risk()
    {
        var graph = Build();
        var output = graph.Impact("Repo0");

        Assert.Contains("endpoints HTTP", output);
        Assert.Contains("POST /api/billing/compute", output);
    }

    [Fact]
    public void Impact_Lists_Covering_Tests_Separately()
    {
        var graph = Build();
        var output = graph.Impact("Repo0", 6, includeTests: false);

        Assert.Contains("Tests que lo ejercitan", output);
        Assert.Contains("Service0Tests", output);
        // con includeTests=false los tests no aparecen dentro de los niveles
        Assert.DoesNotContain("Nivel 1: Service0Tests", output);
    }

    [Fact]
    public void Impact_FromLeafType_ReportsEmpty()
    {
        var graph = Build();
        var output = graph.Impact("Controller0");

        Assert.Contains("no tiene afectados", output);
    }

    [Fact]
    public void Impact_FromInterface_CountsConsumers()
    {
        var graph = Build();
        var output = graph.Impact("IRepo0");

        // nivel 1: la implementación registrada (arista di-bound) y el consumidor
        Assert.Contains("Repo0 [di-bound]", output);
        Assert.Contains("Service0", output);
        Assert.Contains("Nivel 2: Controller0", output);
        Assert.Contains("1 test", output);
    }
}
