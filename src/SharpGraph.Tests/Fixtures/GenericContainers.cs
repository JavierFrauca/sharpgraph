// Fixture: genéricos contenedor (Task, List, IEnumerable) que deben filtrarse como nodo
// pero descender a los argumentos de tipo (Foo, Bar).
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyApp;

public class Repo
{
    public Task<List<Foo>> GetAllAsync() => null;
    public IEnumerable<Bar> GetBars() => null;
    public void Consume(Foo f, Bar b) { }
}

public class Foo { }
public class Bar { }
