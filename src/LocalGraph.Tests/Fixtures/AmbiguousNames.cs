// Fixture: dos tipos homónimos en namespaces distintos.
// Verifica: NO se fusionan, y al consultar "Order" la herramienta pide cualificar.
namespace Sales
{
    public class Order { }
}

namespace Purchasing
{
    public class Order { }
}
