// Fixture: bindings de inyección de dependencias en todas sus formas.
// Verifica: DiBinding para AddScoped<I,C>(), AddKeyedSingleton<I,C>() y AddTransient(typeof(I), typeof(C)).
using Microsoft.Extensions.DependencyInjection;

namespace MyApp;

public static class CompositionRoot
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.AddKeyedSingleton<ICache, MemoryCache>();
        services.AddTransient(typeof(IValidator), typeof(DefaultValidator));
    }
}

public interface IOrderService { }
public class OrderService : IOrderService { }

public interface ICache { }
public class MemoryCache : ICache { }

public interface IValidator { }
public class DefaultValidator : IValidator { }
