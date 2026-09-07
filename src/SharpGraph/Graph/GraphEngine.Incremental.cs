namespace SharpGraph.Graph;

/// <summary>
/// Fusión incremental de fragmentos (ruta rápida + fallback).
///
/// <see cref="GraphEngine.RebuildLocked"/> reconstruye TODOS los índices desde TODOS
/// los fragmentos: correcto, pero O(graffo completo) — con una solución grande son
/// segundos bajo <c>_lock</c>, y el watcher lo ejecutaba una vez por fichero guardado,
/// congelando cualquier query en curso (el cuelgue de understand/search).
///
/// La ruta rápida: si el fichero cambiado declara EXACTAMENTE los mismos tipos
/// (mismo FQN + namespace) y expone las mismas firmas de retorno, la tabla de símbolos
/// global no cambia y, por tanto, las contribuciones YA indexadas del resto de ficheros
/// siguen siendo válidas. Basta restar las contribuciones del fragmento viejo y sumar
/// las del nuevo: O(tamaño del fichero), milisegundos.
///
/// Cualquier cambio estructural (alta/baja/renombre de tipo, firma nueva o con otro
/// tipo de retorno, fichero nuevo) cae al fallback: UN rebuild completo por lote.
/// </summary>
public sealed partial class GraphEngine
{
    /// <summary>
    /// ¿El último MergeFragment(s) tomó la ruta incremental? Informativo: lo usa el
    /// watcher en su log y los tests para verificar qué camino se ejercitó.
    /// </summary>
    public bool LastMergeIncremental { get; private set; }

    /// <summary>
    /// ¿Puede el lote fusionarse por delta? Requiere que cada fichero tenga un
    /// fragmento previo con la misma aportación a la tabla de símbolos y a la tabla
    /// de firmas de retorno. Con un solo fichero estructural en el lote, todo el lote
    /// va a rebuild (una sola vez): mezclar rutas dejaría estados intermedios
    /// inconsistentes entre ficheros.
    /// </summary>
    private bool CanMergeIncrementallyLocked(List<FileFragment> batch)
    {
        foreach (var f in batch)
            if (!_fragments.TryGetValue(f.FilePath, out var old) || !SameSymbolContribution(old, f))
                return false;
        return true;
    }

    /// <summary>
    /// ¿Los dos fragmentos aportan lo mismo a la tabla de símbolos global y a la tabla
    /// de firmas de retorno? Si es así, las resoluciones (Resolve / StepReturnType)
    /// ya aplicadas al indexar el resto de fragmentos siguen siendo válidas, y el
    /// fragmento viejo puede "restarse" re-resolviéndolo contra el estado actual.
    /// </summary>
    private static bool SameSymbolContribution(FileFragment oldFrag, FileFragment newFrag)
    {
        if (oldFrag.Nodes.Count != newFrag.Nodes.Count) return false;
        var oldNames = new HashSet<string>(oldFrag.Nodes.Select(n => n.Namespace + "|" + n.Name), Cmp);
        foreach (var n in newFrag.Nodes)
            if (!oldNames.Remove(n.Namespace + "|" + n.Name)) return false;
        if (oldNames.Count > 0) return false;

        if (oldFrag.ReturnSignatures.Count != newFrag.ReturnSignatures.Count) return false;
        var oldSigs = new HashSet<MemberReturnSignature>(oldFrag.ReturnSignatures);
        foreach (var s in newFrag.ReturnSignatures)
            if (!oldSigs.Remove(s)) return false;
        return oldSigs.Count == 0;
    }

    /// <summary>Indexa un fragmento ya registrado en _fragments (ruta incremental).</summary>
    private void IndexFragmentLocked(FileFragment frag)
    {
        IndexFragmentDeclarationsLocked(frag);
        IndexFragmentReferencesLocked(frag);
        RebuildDocsForFragmentLocked(frag);
    }

    /// <summary>
    /// Resta del índice todas las contribuciones del fragmento. Las eliminaciones son
    /// por igualdad de valor (records): se re-resuelven aristas/call-sites contra el
    /// estado ACTUAL, que es exactamente el estado bajo el que se indexaron (garantizado
    /// por <see cref="SameSymbolContribution"/>), y se obtienen los mismos valores.
    /// </summary>
    private void UnindexFragmentLocked(FileFragment frag)
    {
        // Los docs primero: su contenido (miembros + hasta 40 aristas salientes del
        // nodo) refleja el estado actual, que aún contiene las aristas del fragmento.
        foreach (var name in DeclaredNames(frag))
            RemoveDocLocked(name);

        // ---- inverso del PASO 2 ----

        // call-sites derivados de receptores encadenados: CallSite + arista Call
        foreach (var pcs in frag.PendingCallSites)
        {
            var calleeType = ResolvePendingReceiver(pcs, frag);
            if (calleeType is null) continue;
            RemoveCallsiteLocked(new CallSite(pcs.CallerType, pcs.CallerMember, calleeType, pcs.CalleeMember, pcs.Ns, pcs.Line));
            if (!string.IsNullOrWhiteSpace(pcs.CallerType) &&
                !pcs.CallerType.Equals(calleeType, StringComparison.OrdinalIgnoreCase))
                RemoveEdgeLocked(new TypeEdge(pcs.CallerType, true, calleeType, true, pcs.Ns, EdgeRelation.Call, pcs.Line, pcs.CallerMember));
        }

        // locales var resueltos: claves deterministas (tipo|miembro|local)
        foreach (var pl in frag.PendingLocals)
            _resolvedLocals.Remove(PendingLocalKey(pl.DeclaringType, pl.DeclaringMember, pl.LocalName));

        foreach (var di in frag.DiBindings)
        {
            var resolved = di with
            {
                ServiceType = Resolve(di.ServiceType, di.Ns, frag),
                ImplementationType = Resolve(di.ImplementationType, di.Ns, frag)
            };
            RemoveFromListLocked(_diByService, resolved.ServiceType, resolved);
            RemoveFromListLocked(_diByImpl, resolved.ImplementationType, resolved);
        }

        foreach (var cs in frag.CallSites)
            RemoveCallsiteLocked(cs with { CalleeType = Resolve(cs.CalleeType, cs.Ns, frag) });

        foreach (var e in frag.Edges)
        {
            var from = e.FromResolved ? e.From : Resolve(e.From, e.Ns, frag);
            var to = e.ToResolved ? e.To : Resolve(e.To, e.Ns, frag);
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ||
                from.Equals(to, StringComparison.OrdinalIgnoreCase))
                continue;
            RemoveEdgeLocked(e with { From = from, To = to, FromResolved = true, ToResolved = true });
        }

        // ---- inverso del PASO 1 ----
        foreach (var sig in frag.ReturnSignatures)
            RemoveFromListLocked(_returnsByMember, SignatureKey(sig.TypeName, sig.MemberName), sig);
        foreach (var m in frag.Members)
            RemoveFromListLocked(_members, m.TypeName, m);
        foreach (var ep in frag.Endpoints)
            RemoveFromListLocked(_endpoints, ep.TypeName, ep);

        // nodos: solo desaparecen si ningún otro fichero declara el mismo FQN
        // (partial classes declaran el mismo tipo en varios ficheros). Si hay
        // hermano, el nodo se restaura desde él.
        foreach (var n in frag.Nodes)
        {
            if (TryRestoreNodeLocked(n.Name, frag)) continue;
            _nodes.Remove(n.Name);
            if (_files.TryGetValue(n.Name, out var owner) &&
                owner.Equals(frag.FilePath, StringComparison.OrdinalIgnoreCase))
                _files.Remove(n.Name);
        }
    }

    private static IEnumerable<string> DeclaredNames(FileFragment frag)
        => frag.Nodes.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Restaura un nodo declarado también por otro fichero (partial class).</summary>
    private bool TryRestoreNodeLocked(string name, FileFragment except)
    {
        foreach (var other in _fragments.Values)
        {
            if (other.FilePath.Equals(except.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            var nd = other.Nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (nd is null) continue;
            _nodes[name] = nd;
            _files.TryAdd(name, other.FilePath);
            return true;
        }
        return false;
    }

    // Las entradas vacías de _out/_in NO se eliminan al restar: el rebuild completo
    // también las deja (representan nodos sin aristas salientes/entrantes) y
    // ComputeRankLocked recorre _out.Keys — eliminarlas rompería la paridad de rank
    // entre delta y rebuild, y haría crecer/decrecer el conjunto de nodos rankeados.

    private void RemoveEdgeLocked(TypeEdge resolved)
    {
        if (_out.TryGetValue(resolved.From, out var outList)) outList.Remove(resolved);
        if (_in.TryGetValue(resolved.To, out var inSet)) inSet.Remove(resolved.From);
    }

    private void RemoveCallsiteLocked(CallSite resolved)
    {
        if (_callsByCallee.TryGetValue(resolved.CalleeType, out var byCallee)) byCallee.Remove(resolved);
        if (_callsByCaller.TryGetValue(resolved.CallerType, out var byCaller)) byCaller.Remove(resolved);
    }

    private static void RemoveFromListLocked<T>(Dictionary<string, List<T>> index, string key, T item)
    {
        if (index.TryGetValue(key, out var list)) list.Remove(item);
    }

    // ---------- delta BM25 ----------

    /// <summary>Quita el doc BM25 de un tipo y descuenta sus frecuencias globales.</summary>
    private void RemoveDocLocked(string type)
    {
        var idx = _docs.FindIndex(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var doc = _docs[idx];
        _docs.RemoveAt(idx);
        _totalDocLength -= doc.Length;
        foreach (var term in doc.Tf.Keys)
        {
            if (!_docFrequency.TryGetValue(term, out var c)) continue;
            if (c <= 1) _docFrequency.Remove(term);
            else _docFrequency[term] = c - 1;
        }
    }

    /// <summary>Reconstruye el doc BM25 de los tipos públicos declarados por el fragmento.</summary>
    private void RebuildDocsForFragmentLocked(FileFragment frag)
    {
        foreach (var name in DeclaredNames(frag))
            if (_nodes.TryGetValue(name, out var node))
                BuildDocForNodeLocked(node);
    }
}
