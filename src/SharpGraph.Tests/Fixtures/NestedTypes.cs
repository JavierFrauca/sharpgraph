// Fixture: clases anidadas con referencias cruzadas y colisión de nombre simple.
// Verifica: FQN correcto (Outer.Handler vs Outer.OtherHandler), no se fusionan.
namespace MyApp.Features;

public class OrderWorkflow
{
    public class Handler
    {
        public void Run(InnerService svc) => svc.Do();
    }

    public class OtherHandler
    {
        public void Run(InnerService svc) => svc.Do();
    }
}

public class InnerService
{
    public void Do() { }
}
