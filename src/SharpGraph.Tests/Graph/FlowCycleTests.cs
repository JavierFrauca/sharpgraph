using SharpGraph.Graph;

namespace SharpGraph.Tests.Graph;

/// <summary>
/// Tests específicos de la herramienta <c>flow</c>: terminación, límites y ciclos.
/// Cubre el bug del rebind DI en grafos cíclicos (<c>CodeGraph.RenderFlow</c>).
/// </summary>
public class FlowCycleTests
{
    /// <summary>
    /// Ciclo real mediado por DI: A.M -> IB.M (=> B.M) -> IA.M (=> A.M).
    /// Con bindings IA->A y IB->B registrados, flow() debe seguir el rebind DI,
    /// detectar el ciclo y TERMINAR sin explotar el budget de 80 nodos.
    /// </summary>
    [Fact]
    public void Flow_Terminates_On_Di_Cycle_With_Real_Bindings()
    {
        var graph = GraphTestHarness.Build("DiCycle");

        var result = graph.Flow("A", "M", depth: 5);

        Assert.Contains("Flow", result);

        // Cota de explosión: el output no debe superar las ~50 líneas.
        // En el peor caso (ciclo visitado hasta depth 5 sin dedup), podría haber
        // bastantes; un valor sano está muy por debajo de 80.
        var lineCount = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount < 80,
            $"flow() generó {lineCount} líneas en un grafo cíclico de 2 nodos; posible recursión sin control.");
    }

    /// <summary>
    /// Sin bindings DI registrados, flow() no sigue el rebind y simplemente lista
    /// las llamadas salientes directas. Debe terminar igualmente.
    /// </summary>
    [Fact]
    public void Flow_Terminates_Without_Di_Bindings()
    {
        var graph = GraphTestHarness.Build("FlowWithCycle");

        var result = graph.Flow("A", "M", depth: 5);
        Assert.Contains("Flow", result);

        var lineCount = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount < 50,
            $"flow() generó {lineCount} líneas sin bindings DI; posible problema.");
    }

    /// <summary>
    /// Sin método indicado, flow() devuelve las llamadas salientes por método público.
    /// </summary>
    [Fact]
    public void Flow_Without_Member_Shows_Outgoing_Calls_Per_Method()
    {
        var graph = GraphTestHarness.Build("MediatRController");

        var result = graph.Flow("CreateUserCommandHandler", null, depth: 1);
        Assert.Contains("CreateUserCommandHandler", result);
    }

    /// <summary>
    /// En el ciclo DI, el output no debe mostrar la misma arista (A.M o B.M con misma
    /// línea) repetida muchas veces. Verificamos una cota razonable de duplicados.
    /// </summary>
    [Fact]
    public void Flow_On_Di_Cycle_Does_Not_Explode_With_Duplicate_Edges()
    {
        var graph = GraphTestHarness.Build("DiCycle");

        var result = graph.Flow("A", "M", depth: 5);

        // Cada arista en el grafo es única por (callee, member, line). En un ciclo de
        // 2 nodos, lo sano es ver "→ B.M()" y "→ A.M()" un puñado de veces como mucho.
        var bMentions = CountOccurrences(result, "→ B.M()");
        var aMentions = CountOccurrences(result, "→ A.M()");
        Assert.True(bMentions <= 10, $"B.M() aparece {bMentions} veces; posible duplicación por ciclo.");
        Assert.True(aMentions <= 10, $"A.M() aparece {aMentions} veces; posible duplicación por ciclo.");
    }

    /// <summary>
    /// Contrato del ciclo DI: flow(A.M, depth>=3) sigue el rebind IA->A / IB->B,
    /// detecta el ciclo (visited por "{type}.{member}") y TERMINA en cuanto lo cierra.
    /// Output esperado (3 líneas: B->A->B y corte al detectar el ciclo).
    ///
    /// Este test documenta el comportamiento sano actual. Si un futuro refactor de
    /// RenderFlow o del rebind DI cambia la deduplicación, este test debe actualizarse
    /// EXPLICANDO por qué el nuevo comportamiento es correcto.
    /// </summary>
    [Fact]
    public void Flow_On_Di_Cycle_Follows_Rebind_And_Stops_At_Cycle_Contract()
    {
        var graph = GraphTestHarness.Build("DiCycle");

        var result = graph.Flow("A", "M", depth: 3);

        // 1) sigue el binding IB -> B (aparece la nota [impl: B])
        Assert.Contains("[impl: B]", result);
        // 2) desde B, sigue IA -> A (aparece [impl: A])
        Assert.Contains("[impl: A]", result);
        // 3) el ciclo se corta: A.M no aparece más de 1 vez como arista recursiva
        //    (la arista original A.M→IB.M se cuenta al inicio; las demás son visitas)
        Assert.True(CountOccurrences(result, "→ A.M()") <= 1,
            "El ciclo A.M no se está cortando correctamente.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
