using System.Text;

namespace SharpGraph.Graph;

/// <summary>
/// Grafo de dependencias en memoria. Se construye a partir de
/// <see cref="FileFragment"/> (uno por fichero .cs), lo que permite
/// reconstrucción incremental: al cambiar un fichero se reemplaza su
/// fragmento y se reindexan los derivados.
/// </summary>
public sealed partial class GraphEngine
{
    private sealed record Doc(string Type, Dictionary<string, int> Tf, int Length);

    private readonly Lock _lock = new();

    // Comparers globales: evitan repetir `new(StringComparer.OrdinalIgnoreCase)` 18+ veces
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly StringComparer CmpOrd = StringComparer.Ordinal;

    // Fuente de verdad: fragmentos por fichero.
    private readonly Dictionary<string, FileFragment> _fragments = new(Cmp);

    // Índices derivados (reconstruidos desde _fragments).
    private readonly Dictionary<string, NodeDef> _nodes = new(Cmp);
    private readonly Dictionary<string, string> _files = new(Cmp);
    private readonly Dictionary<string, List<TypeEdge>> _out = new(Cmp);
    private readonly Dictionary<string, HashSet<string>> _in = new(Cmp);
    private readonly Dictionary<string, List<EndpointDef>> _endpoints = new(Cmp);
    private readonly Dictionary<string, List<CallSite>> _callsByCallee = new(Cmp);
    // llamadas SALIENTES por tipo emisor (para flow): CallerType -> call-sites
    private readonly Dictionary<string, List<CallSite>> _callsByCaller = new(Cmp);
    private readonly Dictionary<string, List<DiBinding>> _diByService = new(Cmp);
    private readonly Dictionary<string, List<DiBinding>> _diByImpl = new(Cmp);
    private readonly Dictionary<string, List<MemberSpan>> _members = new(Cmp);
    // B — signaturas de retorno (método/propiedad) indexadas por (tipo, miembro) para
    // resolver receptores encadenados. Clave: "TypeName|MemberName" -> ReturnSimpleType.
    private readonly Dictionary<string, List<MemberReturnSignature>> _returnsByMember = new(Cmp);
    // B.3 — Locales var pendientes ya resueltos, indexados por (declaringType|declaringMember|localName).
    // Se rellena ANTES de procesar PendingCallSites para que estos puedan usarlos.
    private readonly Dictionary<string, string> _resolvedLocals = new(Cmp);

    // BM25 sobre tipos públicos.
    private readonly List<Doc> _docs = [];
    private readonly Dictionary<string, int> _docFrequency = new(Cmp);
    private int _totalDocLength;

    // Centralidad (PageRank): cuanto más alto, más "núcleo" arquitectónico es el tipo.
    private readonly Dictionary<string, double> _rank = new(Cmp);

    // Tabla de símbolos para resolución de identidad: nombre simple -> FQNs declarados.
    private readonly Dictionary<string, List<(string Fqn, string Ns)>> _fqnBySimple = new(CmpOrd);
    // Nombres simples declarados por 2+ tipos: requieren cualificación al mostrarse.
    private readonly HashSet<string> _ambiguous = new(CmpOrd);

    public string? CurrentPath { get; private set; }
    public int NodeCount { get { lock (_lock) return _nodes.Count; } }
    public int EdgeCount { get { lock (_lock) return _out.Values.Sum(v => v.Count); } }

    // ----------------------------------------------------------------- writes

    public void Clear(string? newPath = null)
    {
        lock (_lock)
        {
            _fragments.Clear();
            CurrentPath = newPath;
            RebuildLocked();
        }
    }

    public IReadOnlyCollection<string> KnownFiles()
    {
        lock (_lock) return _fragments.Keys.ToList();
    }

    public bool TryGetFileHash(string filePath, out string hash)
    {
        lock (_lock)
        {
            if (_fragments.TryGetValue(filePath, out var f)) { hash = f.Hash; return true; }
            hash = ""; return false;
        }
    }

    /// <summary>Fusiona o reemplaza el fragmento de un fichero y reindexa.</summary>
    public void MergeFragment(FileFragment fragment)
    {
        lock (_lock)
        {
            _fragments[fragment.FilePath] = fragment;
            RebuildLocked();
        }
    }

    public void MergeFragments(IEnumerable<FileFragment> fragments)
    {
        lock (_lock)
        {
            foreach (var f in fragments) _fragments[f.FilePath] = f;
            RebuildLocked();
        }
    }

    public void RemoveFile(string filePath)
    {
        lock (_lock)
        {
            if (_fragments.Remove(filePath)) RebuildLocked();
        }
    }

    public IReadOnlyCollection<FileFragment> Fragments()
    {
        lock (_lock) return _fragments.Values.ToList();
    }

    private void RebuildLocked()
    {
        _nodes.Clear(); _files.Clear(); _out.Clear(); _in.Clear();
        _endpoints.Clear(); _callsByCallee.Clear(); _callsByCaller.Clear(); _diByService.Clear();
        _diByImpl.Clear(); _members.Clear(); _returnsByMember.Clear(); _resolvedLocals.Clear();
        _docs.Clear(); _docFrequency.Clear(); _totalDocLength = 0;
        _rank.Clear(); _fqnBySimple.Clear(); _ambiguous.Clear();

        // PASO 1: tipos declarados + tabla de símbolos (necesaria para resolver referencias).
        foreach (var frag in _fragments.Values)
        {
            foreach (var n in frag.Nodes)
            {
                if (!_nodes.TryGetValue(n.Name, out var existing) || (existing.Summary is null && n.Summary is not null))
                    _nodes[n.Name] = n;
                _files.TryAdd(n.Name, frag.FilePath);

                var simple = LastSegment(n.Name);
                if (!_fqnBySimple.TryGetValue(simple, out var list)) _fqnBySimple[simple] = list = [];
                if (!list.Any(x => x.Fqn.Equals(n.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add((n.Name, n.Namespace));
            }

            foreach (var ep in frag.Endpoints)
            {
                if (!_endpoints.TryGetValue(ep.TypeName, out var list)) _endpoints[ep.TypeName] = list = [];
                list.Add(ep);
            }

            foreach (var m in frag.Members)
            {
                if (!_members.TryGetValue(m.TypeName, out var list)) _members[m.TypeName] = list = [];
                list.Add(m);
            }

            // B — indexa signaturas de retorno: (tipo, miembro) -> tipo de retorno simple.
            // Resuelve el tipo declarado a FQN para que el lookup posterior funcione
            // con el mismo nombre con el que se indexan los nodos.
            foreach (var sig in frag.ReturnSignatures)
            {
                if (!_returnsByMember.TryGetValue(SignatureKey(sig.TypeName, sig.MemberName), out var list))
                    _returnsByMember[SignatureKey(sig.TypeName, sig.MemberName)] = list = [];
                list.Add(sig);
            }
        }

        foreach (var (simple, fqns) in _fqnBySimple)
            if (fqns.Count > 1) _ambiguous.Add(simple);

        // PASO 2: aristas, call-sites y DI con los extremos resueltos a FQN.
        foreach (var frag in _fragments.Values)
        {
            foreach (var e in frag.Edges)
            {
                var from = e.FromResolved ? e.From : Resolve(e.From, e.Ns, frag);
                var to = e.ToResolved ? e.To : Resolve(e.To, e.Ns, frag);
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ||
                    from.Equals(to, StringComparison.OrdinalIgnoreCase))
                    continue;

                var resolved = e with { From = from, To = to, FromResolved = true, ToResolved = true };
                if (!_out.TryGetValue(from, out var outList)) _out[from] = outList = [];
                outList.Add(resolved);
                if (!_in.TryGetValue(to, out var inSet)) _in[to] = inSet = new(Cmp);
                inSet.Add(from);
                _in.TryAdd(from, new(Cmp));
                _out.TryAdd(to, []);
            }

            foreach (var cs in frag.CallSites)
            {
                var callee = Resolve(cs.CalleeType, cs.Ns, frag);
                var resolved = cs with { CalleeType = callee };
                if (!_callsByCallee.TryGetValue(callee, out var list)) _callsByCallee[callee] = list = [];
                list.Add(resolved);
                if (!_callsByCaller.TryGetValue(cs.CallerType, out var cl)) _callsByCaller[cs.CallerType] = cl = [];
                cl.Add(resolved);
            }

            foreach (var di in frag.DiBindings)
            {
                var svc = Resolve(di.ServiceType, di.Ns, frag);
                var impl = Resolve(di.ImplementationType, di.Ns, frag);
                var resolved = di with { ServiceType = svc, ImplementationType = impl };
                if (!_diByService.TryGetValue(svc, out var s)) _diByService[svc] = s = [];
                s.Add(resolved);
                if (!_diByImpl.TryGetValue(impl, out var i)) _diByImpl[impl] = i = [];
                i.Add(resolved);
            }

            // B.3 — resuelve primero los PendingLocals (var x = <expr>) para que los
            // PendingCallSites que los usen como receptor puedan resolverlos.
            foreach (var pl in frag.PendingLocals)
            {
                var localType = ResolvePendingInitializer(pl.Initializer, frag);
                if (localType is null) continue;
                _resolvedLocals[PendingLocalKey(pl.DeclaringType, pl.DeclaringMember, pl.LocalName)] = localType;
            }

            // B — resuelve call-sites pendientes cuyo receptor era complejo (factory,
            // chaining, member-access profundo). Camina los pasos desde el local
            // inicial hasta inferir el tipo final del receptor y, si lo logra,
            // registra el call-site y la arista Call como cualquier otro.
            foreach (var pcs in frag.PendingCallSites)
            {
                var calleeType = ResolvePendingReceiver(pcs, frag);
                if (calleeType is null) continue;
                var resolved = new CallSite(pcs.CallerType, pcs.CallerMember, calleeType, pcs.CalleeMember, pcs.Ns, pcs.Line);
                if (!_callsByCallee.TryGetValue(calleeType, out var list)) _callsByCallee[calleeType] = list = [];
                list.Add(resolved);
                if (!_callsByCaller.TryGetValue(pcs.CallerType, out var cl)) _callsByCaller[pcs.CallerType] = cl = [];
                cl.Add(resolved);

                // arista Call (From=caller FQN o <top-level>, To=callee resuelto)
                if (!string.IsNullOrWhiteSpace(pcs.CallerType) &&
                    !pcs.CallerType.Equals(calleeType, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_out.TryGetValue(pcs.CallerType, out var outList)) _out[pcs.CallerType] = outList = [];
                    outList.Add(new TypeEdge(pcs.CallerType, true, calleeType, true, pcs.Ns, EdgeRelation.Call, pcs.Line, pcs.CallerMember));
                    if (!_in.TryGetValue(calleeType, out var inSet)) _in[calleeType] = inSet = new(Cmp);
                    inSet.Add(pcs.CallerType);
                }
            }
        }

        BuildDocsLocked();
        ComputeRankLocked();
    }

    // ---------- resolución de identidad de símbolo ----------

    private static string LastSegment(string name)
    {
        var idx = name.LastIndexOf('.');
        return idx < 0 ? name : name[(idx + 1)..];
    }

    /// <summary>
    /// Resuelve un nombre simple a su FQN usando namespace de contexto + usings del fichero.
    /// Si no es un tipo de la solución (BCL/NuGet) o no se puede desambiguar, devuelve el nombre tal cual.
    /// </summary>
    private string Resolve(string raw, string ns, FileFragment? frag)
    {
        var simple = LastSegment(raw);
        if (frag is not null && frag.Aliases.TryGetValue(simple, out var aliasTarget))
            simple = LastSegment(aliasTarget);

        if (!_fqnBySimple.TryGetValue(simple, out var candidates) || candidates.Count == 0)
            return simple;
        if (candidates.Count == 1)
            return candidates[0].Fqn;

        // 1) mismo namespace exacto
        foreach (var c in candidates)
            if (c.Ns.Equals(ns, StringComparison.Ordinal)) return c.Fqn;
        // 2) namespace importado por un using
        if (frag is not null)
            foreach (var c in candidates)
                if (frag.Usings.Contains(c.Ns, StringComparer.Ordinal)) return c.Fqn;
        // 3) prefijo de namespace compartido más largo con el contexto
        var best = candidates[0];
        var bestShared = -1;
        foreach (var c in candidates)
        {
            var shared = SharedPrefixLength(c.Ns, ns);
            if (shared > bestShared) { bestShared = shared; best = c; }
        }
        return best.Fqn;
    }

    private static int SharedPrefixLength(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Min(pa.Length, pb.Length);
        var i = 0;
        while (i < n && pa[i].Equals(pb[i], StringComparison.Ordinal)) i++;
        return i;
    }

    /// <summary>Clave para indexar/consultar signaturas de retorno por (tipo, miembro).</summary>
    private static string SignatureKey(string typeName, string memberName)
        => typeName + "|" + memberName;

    /// <summary>
    /// Camina los pasos de un PendingCallSite para inferir el tipo del receptor
    /// final. Empieza resolviendo el primer paso (Local → tipo conocido del
    /// fragmento) y avanza consultando las signaturas de retorno indexadas
    /// (MethodReturn / PropertyAccess). Devuelve el FQN del tipo final, o null
    /// si no se puede inferir.
    /// </summary>
    private string? ResolvePendingReceiver(PendingCallSite pcs, FileFragment frag)
    {
        if (pcs.Receiver.Count == 0) return null;
        var first = pcs.Receiver[0];
        if (first.Kind != PendingReceiverStepKind.Local) return null;

        string? currentType;
        // El Local step lleva el nombre simple del tipo del receptor (campo/param/var),
        // resuelto por el visitor con LookupLocal. Si lo trae, lo usamos.
        if (!string.IsNullOrWhiteSpace(first.TypeSimpleName))
        {
            currentType = Resolve(first.TypeSimpleName, pcs.Ns, frag);
        }
        else
        {
            // B.3 — Local sin tipo (var x = await ...; x.M()): el tipo se resolvió antes
            // en este mismo rebuild a partir del PendingLocal. Si no está, no podemos avanzar.
            var key = PendingLocalKey(pcs.CallerType, pcs.CallerMember, first.Name);
            if (!_resolvedLocals.TryGetValue(key, out var resolved)) return null;
            currentType = resolved;
        }

        for (var i = 1; i < pcs.Receiver.Count; i++)
        {
            var step = pcs.Receiver[i];
            currentType = StepReturnType(currentType, step.Name, step.Kind);
            if (currentType is null) return null;
        }
        return currentType;
    }

    /// <summary>Resuelve el tipo de un inicializador de var serializado como pasos.</summary>
    private string? ResolvePendingInitializer(IReadOnlyList<PendingReceiverStep> init, FileFragment frag)
    {
        if (init.Count == 0) return null;
        var first = init[0];
        if (first.Kind != PendingReceiverStepKind.Local) return null;
        if (string.IsNullOrWhiteSpace(first.TypeSimpleName)) return null;
        var currentType = Resolve(first.TypeSimpleName, first.Ns ?? "", frag);
        for (var i = 1; i < init.Count; i++)
        {
            var step = init[i];
            currentType = StepReturnType(currentType, step.Name, step.Kind);
            if (currentType is null) return null;
        }
        return currentType;
    }

    private static string PendingLocalKey(string declaringType, string declaringMember, string localName)
        => $"{declaringType}|{declaringMember}|{localName}";

    /// <summary>
    /// Dado el tipo actual y un paso (método o propiedad), devuelve el tipo de
    /// retorno/propiedad declarado, consultando las signaturas indexadas.
    /// La signatura se indexa por el FQN del tipo declarante (p.ej. "MyApp.IFactory"),
    /// pero currentType puede llegar como nombre simple si no estaba en la tabla
    /// global (BCL/NuGet). Probamos FQN, simple, y por sufijo para cubrir ambos.
    /// </summary>
    private string? StepReturnType(string? currentType, string memberName, PendingReceiverStepKind kind)
    {
        if (string.IsNullOrWhiteSpace(currentType)) return null;

        var candidates = new List<MemberReturnSignature>();
        // 1) FQN exacto
        if (_returnsByMember.TryGetValue(SignatureKey(currentType, memberName), out var exact))
            candidates.AddRange(exact);
        // 2) nombre simple (si currentType no estaba en la tabla global)
        if (currentType.Contains('.'))
        {
            var simple = LastSegment(currentType);
            if (_returnsByMember.TryGetValue(SignatureKey(simple, memberName), out var bySimple))
                candidates.AddRange(bySimple);
        }
        // 3) sufijo: la signatura se declaró como "MyApp.IFactory" pero currentType
        //    llegó como "IFactory" (caso típico). Buscamos claves que terminen en ".IFactory|Get".
        if (candidates.Count == 0)
        {
            var suffix = "|" + LastSegment(currentType) + "|" + memberName;
            foreach (var key in _returnsByMember.Keys)
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = key.Split('|');
                    if (parts.Length == 2 && parts[1] == memberName)
                        candidates.AddRange(_returnsByMember[key]);
                }
        }
        if (candidates.Count == 0) return null;
        // Si hay varias signaturas (overloads), tomamos la primera; sin modelo semántico
        // no podemos distinguir por tipos de argumento. Best-effort.
        var sig = candidates[0];
        return Resolve(sig.ReturnSimpleType, sig.Ns, null);
    }

    /// <summary>Nombre para mostrar: simple salvo que colisione, entonces el FQN.</summary>
    private string Display(string key)
    {
        if (!_nodes.ContainsKey(key)) return key; // externo o sintético: tal cual
        var simple = LastSegment(key);
        return _ambiguous.Contains(simple) ? key : simple;
    }

    /// <summary>
    /// Resuelve la entrada del usuario (nombre simple o FQN) a una clave del grafo.
    /// Devuelve null y rellena <paramref name="ambiguity"/> si el nombre es ambiguo.
    /// </summary>
    private string? ResolveInput(string input, out string? ambiguity)
    {
        ambiguity = null;
        if (_nodes.ContainsKey(input) || _out.ContainsKey(input) || _in.ContainsKey(input))
            return input;

        if (_fqnBySimple.TryGetValue(input, out var candidates) && candidates.Count > 0)
        {
            if (candidates.Count == 1) return candidates[0].Fqn;
            ambiguity = $"'{input}' es ambiguo entre {candidates.Count} tipos: " +
                        string.Join(", ", candidates.Select(c => c.Fqn)) +
                        ". Especifica el nombre cualificado.";
            return null;
        }
        return null;
    }

    // ----------------------------------------------------------------- helpers

    private EdgeRelation DominantRelation(string from, string to)
    {
        if (!_out.TryGetValue(from, out var edges)) return EdgeRelation.ParamType;
        EdgeRelation best = EdgeRelation.ParamType;
        var bestRank = -1;
        foreach (var e in edges)
        {
            if (!e.To.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
            var rank = RelationRank(e.Relation);
            if (rank > bestRank) { bestRank = rank; best = e.Relation; }
        }
        return best;
    }

    private static int RelationRank(EdgeRelation r) => r switch
    {
        EdgeRelation.Call => 7,
        EdgeRelation.Sends => 6,
        EdgeRelation.HandledBy => 6,
        EdgeRelation.New => 5,
        EdgeRelation.CtorParam => 4,
        EdgeRelation.FieldType => 3,
        EdgeRelation.PropertyType => 3,
        EdgeRelation.Inherits => 2,
        EdgeRelation.Implements => 2,
        _ => 1
    };

    private static int CountDistinctTargets(List<TypeEdge> edges)
        => edges.Select(e => e.To).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private List<string> ResolveSeeds(string typeOrPattern)
    {
        if (_nodes.ContainsKey(typeOrPattern)) return [typeOrPattern];
        return _nodes.Keys
            .Where(k => k.Contains(typeOrPattern, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => GetMatchRank(k, typeOrPattern))
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Take(3).ToList();
    }

    /// <summary>
    /// Quita caminos idénticos y, cuando un endpoint ya tiene un camino exacto
    /// (direct/nearby/exact-mediatr), descarta los heurísticos hacia ese mismo endpoint.
    /// </summary>
    private static List<string> DedupePaths(List<string> raw)
    {
        var distinct = raw.Distinct(StringComparer.Ordinal).ToList();

        static string EndpointKey(string line)
        {
            // salta la etiqueta inicial "[label] " para comparar solo el endpoint+cadena destino
            var afterLabel = line.StartsWith('[') ? line.IndexOf("] ", StringComparison.Ordinal) is var i && i >= 0 ? line[(i + 2)..] : line : line;
            var arrow = afterLabel.IndexOf(" ← ", StringComparison.Ordinal);
            return arrow < 0 ? afterLabel : afterLabel[..arrow];
        }
        static bool IsHeuristic(string line) => line.StartsWith("[heuristic]", StringComparison.Ordinal);

        var endpointsWithExact = distinct
            .Where(l => !IsHeuristic(l))
            .Select(EndpointKey)
            .ToHashSet(StringComparer.Ordinal);

        return distinct
            .Where(l => !IsHeuristic(l) || !endpointsWithExact.Contains(EndpointKey(l)))
            .ToList();
    }

    private static string ClassifyPath(List<string> path)
    {
        if (path.Any(p => p.StartsWith('↔'))) return "heuristic";
        if (path.Any(p => p.StartsWith('⇒'))) return "exact-mediatr";
        return path.Count <= 3 ? "direct" : "nearby";
    }

    private static int GetMatchRank(string candidate, string pattern)
    {
        if (candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return 0;
        var simple = candidate.Contains('.') ? candidate.Split('.').Last() : candidate;
        if (simple.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static bool IsTestType(string name)
    {
        var simple = name.Contains('.') ? name.Split('.').Last() : name;
        return simple.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
            || simple.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || simple.Contains("Mock", StringComparison.OrdinalIgnoreCase)
            || simple.Contains("Fake", StringComparison.OrdinalIgnoreCase)
            || simple.Contains("Stub", StringComparison.OrdinalIgnoreCase)
            || simple.Contains("Builder", StringComparison.OrdinalIgnoreCase);
    }
}
