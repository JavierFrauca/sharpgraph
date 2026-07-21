// Fixture: ciclo A.M -> B.M -> A.M mediado por DI.
// Verifica: flow() termina (no se cuelga), respeta el budget de 80 nodos y no duplica entradas.
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
    public B(IA a) => _a = _a = a;

    public void M() => _a.M();
}

public static class DiConfig
{
    public static void Register(object services)
    {
        // registra A y B mediados por interfaces, lo que habilita el rebind en flow
    }
}
