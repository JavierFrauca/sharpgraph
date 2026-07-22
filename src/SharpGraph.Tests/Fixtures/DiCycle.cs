// Fixture: ciclo A.M -> B.M -> A.M con DI registrada (IA->A, IB->B).
// Caso que activa el rebind DI en flow(): A depende de IB, que se resuelve a B,
// que depende de IA, que se resuelve a A. Verifica terminación y no duplicación.
using Microsoft.Extensions.DependencyInjection;

namespace MyApp;

public interface IA { void M(); }
public interface IB { void M(); }

public class A : IA
{
    private readonly IB _b;
    public A(IB b) => _b = b;
    public void M() => _b.M();
}

public class B : IB
{
    private readonly IA _a;
    public B(IA a) => _a = a;
    public void M() => _a.M();
}

public static class CompositionRoot
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IA, A>();
        services.AddScoped<IB, B>();
    }
}
