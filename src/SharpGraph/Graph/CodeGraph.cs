using System.Text;

namespace SharpGraph.Graph;

/// <summary>
/// Grafo de dependencias en memoria. Se construye a partir de
/// <see cref="FileFragment"/> (uno por fichero .cs), lo que permite
/// reconstrucción incremental: al cambiar un fichero se reemplaza su
/// fragmento y se reindexan los derivados.
/// </summary>
public sealed class CodeGraph
{
    private sealed record Doc(string Type, Dictionary<string, int> Tf, int Length);

    private readonly Lock _lock = new();

    // Fuente de verdad: fragmentos por fichero.
    private readonly Dictionary<string, FileFragment> _fragments = new(StringComparer.OrdinalIgnoreCase);

    // Índices derivados (reconstruidos desde _fragments).
    private readonly Dictionary<string, NodeDef> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TypeEdge>> _out = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _in = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<EndpointDef>> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CallSite>> _callsByCallee = new(StringComparer.OrdinalIgnoreCase);
    // llamadas SALIENTES por tipo emisor (para flow): CallerType -> call-sites
    private readonly Dictionary<string, List<CallSite>> _callsByCaller = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DiBinding>> _diByService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DiBinding>> _diByImpl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MemberSpan>> _members = new(StringComparer.OrdinalIgnoreCase);
    // B — signaturas de retorno (método/propiedad) indexadas por (tipo, miembro) para
    // resolver receptores encadenados. Clave: "TypeName|MemberName" -> ReturnSimpleType.
    private readonly Dictionary<string, List<MemberReturnSignature>> _returnsByMember = new(StringComparer.OrdinalIgnoreCase);
    // B.3 — Locales var pendientes ya resueltos, indexados por (declaringType|declaringMember|localName).
    // Se rellena ANTES de procesar PendingCallSites para que estos puedan usarlos.
    private readonly Dictionary<string, string> _resolvedLocals = new(StringComparer.OrdinalIgnoreCase);

    // BM25 sobre tipos públicos.
    private readonly List<Doc> _docs = [];
    private readonly Dictionary<string, int> _docFrequency = new(StringComparer.OrdinalIgnoreCase);
    private int _totalDocLength;

    // Centralidad (PageRank): cuanto más alto, más "núcleo" arquitectónico es el tipo.
    private readonly Dictionary<string, double> _rank = new(StringComparer.OrdinalIgnoreCase);

    // Tabla de símbolos para resolución de identidad: nombre simple -> FQNs declarados.
    private readonly Dictionary<string, List<(string Fqn, string Ns)>> _fqnBySimple = new(StringComparer.Ordinal);
    // Nombres simples declarados por 2+ tipos: requieren cualificación al mostrarse.
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

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
    public void MergeFragment(FileFragment fragment, bool rebuild = true)
    {
        lock (_lock)
        {
            _fragments[fragment.FilePath] = fragment;
            if (rebuild) RebuildLocked();
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
                if (!_in.TryGetValue(to, out var inSet)) _in[to] = inSet = new(StringComparer.OrdinalIgnoreCase);
                inSet.Add(from);
                _in.TryAdd(from, new(StringComparer.OrdinalIgnoreCase));
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
                    if (!_in.TryGetValue(calleeType, out var inSet)) _in[calleeType] = inSet = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>PageRank (power iteration) sobre las aristas del grafo.</summary>
    private void ComputeRankLocked()
    {
        var nodes = _out.Keys.ToList();
        var n = nodes.Count;
        if (n == 0) return;

        const double d = 0.85;
        const int iterations = 20;

        var outTargets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            outTargets[node] = _out.TryGetValue(node, out var e)
                ? e.Select(z => z.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

        var rank = nodes.ToDictionary(x => x, _ => 1.0 / n, StringComparer.OrdinalIgnoreCase);

        for (var it = 0; it < iterations; it++)
        {
            var next = nodes.ToDictionary(x => x, _ => (1 - d) / n, StringComparer.OrdinalIgnoreCase);
            var dangling = 0d;
            foreach (var node in nodes)
                if (outTargets[node].Count == 0) dangling += rank[node];
            var danglingShare = d * dangling / n;

            foreach (var node in nodes)
            {
                var targets = outTargets[node];
                if (targets.Count > 0)
                {
                    var share = d * rank[node] / targets.Count;
                    foreach (var to in targets)
                        if (next.TryGetValue(to, out var cur)) next[to] = cur + share;
                }
            }
            foreach (var node in nodes) next[node] += danglingShare;
            rank = next;
        }

        foreach (var (k, v) in rank) _rank[k] = v;
    }

    private double Rank(string name) => _rank.GetValueOrDefault(name, 0d);

    public string Hubs(int topK, bool includeExternal)
    {
        lock (_lock)
        {
            topK = Math.Clamp(topK, 1, 50);
            if (_rank.Count == 0) return "Grafo vacío. Ejecuta scan().";

            var ranked = _rank
                .Where(kv => includeExternal || _nodes.ContainsKey(kv.Key))
                .Where(kv => !IsTestType(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Tipos más centrales (PageRank, top {ranked.Count}) — por aquí empieza a entender el sistema:");
            var i = 1;
            foreach (var kv in ranked)
            {
                var defined = _nodes.ContainsKey(kv.Key);
                var file = _files.TryGetValue(kv.Key, out var f) ? $" [{Path.GetFileName(f)}]" : "";
                var callers = _in.TryGetValue(kv.Key, out var inv) ? inv.Count : 0;
                var ep = _endpoints.ContainsKey(kv.Key) ? " [ENDPOINT]" : "";
                var ext = defined ? "" : " (external)";
                sb.AppendLine($"  {i,2}. {Display(kv.Key)}{ep}{file} (callers: {callers}){ext}");
                i++;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Mejora 5: leer un fichero .cs entero del proyecto escaneado, numerado,
    /// truncado a maxLines. Compite con CodeGraph `explore` en el caso "muéstrame
    /// este fichero". Solo se aceptan ficheros que el scanner haya indexado
    /// previamente (están en el grafo).
    /// </summary>
    public string ReadFile(string filePath, int maxLines)
    {
        lock (_lock)
        {
            maxLines = Math.Clamp(maxLines, 10, 800);
            // normalizar: acepta path relativo al proyecto o absoluto
            var candidate = filePath;
            if (!File.Exists(candidate) && CurrentPath is not null)
            {
                candidate = Path.Combine(CurrentPath, filePath);
                // si CurrentPath es un fichero (.sln/.csproj), usar su directorio
                if (!File.Exists(candidate) && File.Exists(CurrentPath))
                    candidate = Path.Combine(Path.GetDirectoryName(CurrentPath)!, filePath);
            }
            if (!File.Exists(candidate))
                return $"File not found in project: {filePath}. Current project: {CurrentPath ?? "(none)"}";

            // aceptar solo .cs (por seguridad)
            if (!candidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return $"Only .cs files are supported: {candidate}";

            string[] lines;
            try { lines = File.ReadAllLines(candidate); }
            catch (Exception ex) { return $"Could not read {candidate}: {ex.Message}"; }

            var sb = new StringBuilder();
            sb.AppendLine($"// {Path.GetFileName(candidate)} ({lines.Length} líneas)");
            var end = Math.Min(maxLines, lines.Length);
            for (var i = 0; i < end; i++)
            {
                var lineNum = i + 1;
                // anotar los tipos definidos que empiezan en esta línea
                var typeHere = _nodes.Values.Where(n => _files.TryGetValue(n.Name, out var f) &&
                    f.Equals(candidate, StringComparison.OrdinalIgnoreCase) && n.StartLine == lineNum).ToList();
                if (typeHere.Count > 0)
                    sb.AppendLine($"// ── {string.Join(", ", typeHere.Select(t => t.Name))} ──");
                sb.AppendLine($"{lineNum,5}  {lines[i]}");
            }
            if (lines.Length > maxLines)
                sb.AppendLine($"  … +{lines.Length - maxLines} líneas");
            return sb.ToString();
        }
    }

    private void BuildDocsLocked()
    {
        foreach (var node in _nodes.Values)
        {
            if (!node.IsPublic) continue;
            var sb = new StringBuilder();
            sb.Append(node.Name).Append(' ');
            if (node.Summary is not null) sb.Append(node.Summary).Append(' ');
            if (_members.TryGetValue(node.Name, out var members))
                foreach (var m in members.Where(m => m.IsPublic))
                    sb.Append(m.MemberName).Append(' ');
            if (_out.TryGetValue(node.Name, out var edges))
                foreach (var e in edges.Take(40))
                    sb.Append(e.To).Append(' ');

            var tf = BuildTermFrequencies(sb.ToString());
            var len = tf.Values.Sum();
            if (len == 0) continue;
            _docs.Add(new Doc(node.Name, tf, len));
            _totalDocLength += len;
            foreach (var term in tf.Keys)
                _docFrequency[term] = _docFrequency.GetValueOrDefault(term, 0) + 1;
        }
    }

    // ----------------------------------------------------------------- queries

    public string Stats()
    {
        lock (_lock)
        {
            var defined = _nodes.Count;
            var endpointCount = _endpoints.Values.Sum(v => v.Count);
            var callCount = _callsByCallee.Values.Sum(v => v.Count);
            var diCount = _diByService.Values.Sum(v => v.Count);
            var path = CurrentPath is null ? "" : $"\nPath: {CurrentPath}";
            return $"Graph: {defined} types defined, {EdgeCountLocked()} edges, {endpointCount} HTTP endpoints, " +
                   $"{callCount} call-sites, {diCount} DI bindings, {_fragments.Count} files indexed{path}";
        }
    }

    private int EdgeCountLocked() => _out.Values.Sum(v => v.Count);

    public string Search(string pattern)
    {
        lock (_lock)
        {
            var matches = _nodes.Keys
                .Where(k => k.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => GetMatchRank(k, pattern))
                .ThenByDescending(Rank)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            if (matches.Count == 0)
                return $"No types found matching '{pattern}'. (only types defined in the solution are indexed)";

            var sb = new StringBuilder();
            sb.AppendLine($"Types matching '{pattern}' ({matches.Count}):");
            foreach (var m in matches)
            {
                var file = _files.TryGetValue(m, out var f) ? $" [{Path.GetFileName(f)}]" : "";
                var isEndpoint = _endpoints.ContainsKey(m) ? " [ENDPOINT]" : "";
                var di = _diByService.ContainsKey(m) ? " [DI-SERVICE]" : "";
                var callers = _in.TryGetValue(m, out var inv) ? inv.Count : 0;
                var uses = _out.TryGetValue(m, out var outv) ? CountDistinctTargets(outv) : 0;
                var kind = _nodes.TryGetValue(m, out var nd) ? nd.Kind.ToString().ToLowerInvariant() : "?";
                // muestra siempre el FQN en search para que el usuario pueda desambiguar
                sb.AppendLine($"  {m} <{kind}>{isEndpoint}{di}{file} (callers: {callers}, uses: {uses})");
            }
            return sb.ToString();
        }
    }

    public string GetUsages(string typeName)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null || !_out.TryGetValue(key, out var edges) || edges.Count == 0)
                return $"'{typeName}' has no recorded usages, or type not found. Try search().";
            typeName = key;

            // agrupar por destino, fusionando relaciones y líneas
            var grouped = edges
                .GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    To = g.Key,
                    Relations = g.Select(e => e.Relation).Distinct().OrderBy(r => r).ToList(),
                    Lines = g.Select(e => e.Line).Where(l => l > 0).Distinct().OrderBy(l => l).Take(3).ToList(),
                })
                .OrderBy(x => x.To, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"'{Display(typeName)}' uses ({grouped.Count} types):");
            foreach (var g in grouped)
            {
                var calledBy = _in.TryGetValue(g.To, out var inv) ? inv.Count : 0;
                var rels = string.Join(",", g.Relations.Select(r => r.Label()));
                var lines = g.Lines.Count > 0 ? $" @L{string.Join(",", g.Lines)}" : "";
                var defined = _nodes.ContainsKey(g.To) ? "" : " (external)";
                sb.AppendLine($"  → {Display(g.To)} [{rels}]{lines} (used by {calledBy}){defined}");
            }
            return sb.ToString();
        }
    }

    public string FindCallers(string typeName, int depth = 3)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null) return $"Type '{typeName}' not found in graph. Try search() to find it.";
            typeName = key;

            depth = Math.Clamp(depth, 1, 6);
            var sb = new StringBuilder();
            sb.AppendLine($"Callers of '{Display(typeName)}' (depth {depth}):");
            sb.AppendLine();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeName };
            PrintCallers(typeName, sb, visited, depth, 0);

            if (_diByService.TryGetValue(typeName, out var di))
                sb.AppendLine($"  [DI] resolves to: {string.Join(", ", di.Select(d => $"{Display(d.ImplementationType)} ({d.Lifetime})").Distinct())}");
            return sb.ToString();
        }
    }

    private void PrintCallers(string typeName, StringBuilder sb, HashSet<string> visited, int maxDepth, int currentDepth)
    {
        if (!_in.TryGetValue(typeName, out var callers) || callers.Count == 0) return;

        foreach (var caller in callers.Where(c => !IsTestType(c)).OrderByDescending(Rank).ThenBy(c => c, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            var indent = new string(' ', (currentDepth + 1) * 2);
            var rel = DominantRelation(caller, typeName);
            var endpointTag = _endpoints.TryGetValue(caller, out var eps) && eps.Count > 0
                ? $" [ENDPOINT: {string.Join(", ", eps.Select(e => $"{e.Verb} {e.Route}"))}]"
                : "";
            sb.AppendLine($"{indent}← {Display(caller)} [{rel.Label()}]{endpointTag}");

            if (currentDepth + 1 < maxDepth && visited.Add(caller))
            {
                PrintCallers(caller, sb, visited, maxDepth, currentDepth + 1);
                visited.Remove(caller);
            }
        }
    }

    public string TraceToEndpoints(string typeName, int maxDepth = 8)
    {
        lock (_lock)
        {
            maxDepth = Math.Clamp(maxDepth, 1, 12);
            var resolved = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (resolved is null) return $"Type '{typeName}' not found. Try search().";
            typeName = resolved;

            var rawResults = new List<string>();
            var visits = new int[1];
            var path = new List<string> { Display(typeName) };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeName };
            Dfs(typeName, path, visited, rawResults, maxDepth, visits, canPivot: true);
            var results = DedupePaths(rawResults);

            var sb = new StringBuilder();
            if (results.Count == 0)
            {
                sb.AppendLine($"No endpoint paths found for '{Display(typeName)}'.");
                if (_in.TryGetValue(typeName, out var direct) && direct.Count > 0)
                    sb.AppendLine($"Direct callers: {string.Join(", ", direct.Take(10).Select(Display))}");
                if (_diByService.TryGetValue(typeName, out var di))
                    sb.AppendLine($"DI implementation: {string.Join(", ", di.Select(d => Display(d.ImplementationType)).Distinct())}");
                return sb.ToString();
            }

            sb.AppendLine($"Paths to HTTP endpoints for '{Display(typeName)}' ({results.Count} found):");
            sb.AppendLine();
            foreach (var r in results) sb.AppendLine("  " + r);
            return sb.ToString();
        }
    }

    private void Dfs(string current, List<string> path, HashSet<string> visited,
        List<string> results, int maxDepth, int[] visits, bool canPivot)
    {
        if (path.Count > maxDepth) return;
        if (results.Count >= 15) return;
        if (++visits[0] > 20_000) return;

        if (_endpoints.TryGetValue(current, out var eps) && eps.Count > 0)
        {
            var chain = string.Join(" ← ", path);
            var label = ClassifyPath(path);
            foreach (var ep in eps)
                results.Add($"[{label}] [{ep.Verb} {ep.Route}] {Display(current)}.{ep.MethodName} ← {chain}");
            return;
        }

        if (_in.TryGetValue(current, out var callers))
        {
            foreach (var caller in callers)
            {
                if (visited.Contains(caller) || IsTestType(caller)) continue;
                var rel = DominantRelation(caller, current);
                visited.Add(caller);
                path.Add(rel is EdgeRelation.Sends or EdgeRelation.HandledBy ? $"⇒{Display(caller)}" : Display(caller));
                Dfs(caller, path, visited, results, maxDepth, visits, canPivot);
                path.RemoveAt(path.Count - 1);
                visited.Remove(caller);
            }
        }

        // Fallback heurístico (solo si no hubo modelado explícito de MediatR):
        // pivota por un Command/Query compartido entre handler y controller.
        if (canPivot && _out.TryGetValue(current, out var uses))
        {
            foreach (var e in uses)
            {
                var dep = e.To;
                if (visited.Contains(dep)) continue;
                if (e.Relation is EdgeRelation.Sends or EdgeRelation.HandledBy) continue; // ya cubierto exacto
                if (!_in.TryGetValue(dep, out var depCallers) || depCallers.Count < 2) continue;

                visited.Add(dep);
                path.Add($"↔{Display(dep)}");
                Dfs(dep, path, visited, results, maxDepth, visits, canPivot: false);
                path.RemoveAt(path.Count - 1);
                visited.Remove(dep);
            }
        }
    }

    // ---- NUEVO: recuperación de código fuente (elimina el paso Read) ----

    public string GetSource(string typeName, string? member, int maxBodyLines, bool includeBodies = false)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is not null) typeName = key;
            if (!_files.TryGetValue(typeName, out var file))
                return $"Type '{typeName}' not found among defined types. Try search().";
            if (!File.Exists(file))
                return $"Source file for '{typeName}' no longer exists at {file}. Re-scan needed.";

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception ex) { return $"Could not read {file}: {ex.Message}"; }

            var members = _members.TryGetValue(typeName, out var ms) ? ms : [];
            var node = _nodes.GetValueOrDefault(typeName);

            if (member is not null)
            {
                var hit = members
                    .Where(m => m.MemberName.Equals(member, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.EndLine - m.StartLine)
                    .FirstOrDefault();
                if (hit is null)
                    return $"Member '{member}' not found in '{typeName}'. Members: {string.Join(", ", members.Select(m => m.MemberName).Distinct())}";
                return RenderSlice(typeName, file, hit, lines, maxBodyLines);
            }

            // Mejora 1: Sin miembro y maxBodyLines > 0 → cuerpo del tipo truncado.
            // Compite con "enséñame esta clase" sin el overhead de understand.
            if (maxBodyLines > 0 && node is not null)
            {
                var start = Math.Clamp(node.StartLine, 1, Math.Max(1, lines.Length));
                var end = Math.Clamp(node.EndLine, start, Math.Max(1, lines.Length));
                return RenderTypeBody(typeName, members, file, lines, node, start, end, maxBodyLines, includeBodies);
            }

            // Sin miembro y sin maxBodyLines → esquema (legacy, mantiene compat).
            var sb = new StringBuilder();
            sb.AppendLine($"// {Path.GetFileName(file)}  ({typeName})");
            if (node?.Summary is not null) sb.AppendLine($"/// {node.Summary}");
            sb.AppendLine($"<{node?.Kind.ToString().ToLowerInvariant() ?? "type"}> {typeName}  (L{node?.StartLine}-{node?.EndLine})");
            sb.AppendLine("members:");
            foreach (var m in members.OrderBy(m => m.StartLine))
                sb.AppendLine($"  L{m.StartLine,-5} {m.Signature}");
            sb.AppendLine();
            sb.AppendLine("Tip: get_source(typeName, member) devuelve el cuerpo de un miembro concreto.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Mejora 1/3: Renderiza el cuerpo completo de un tipo, truncado por línea
    /// (maxBodyLines) o con cuerpos de miembros (includeBodies=true).
    /// </summary>
    private static string RenderTypeBody(string typeName, List<MemberSpan> members,
        string file, string[] lines, NodeDef node, int start, int end,
        int maxBodyLines, bool includeBodies)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {Path.GetFileName(file)}:{node.StartLine}  {typeName} (L{start}-{end})");

        if (includeBodies && members.Count > 0)
        {
            // Mejora 3: devolver los cuerpos de los primeros N miembros públicos,
            // sin cortarlos a la mitad. El presupuesto se reparte entre miembros.
            var publicMembers = members.Where(m => m.IsPublic)
                .OrderBy(m => m.StartLine).ToList();
            var remaining = maxBodyLines;
            var shown = 0;
            foreach (var m in publicMembers)
            {
                if (remaining <= 0)
                {
                    sb.AppendLine($"  … +{publicMembers.Count - shown} miembros más (pasa member para verlos):");
                    foreach (var rest in publicMembers.Skip(shown).Take(8))
                        sb.AppendLine($"    L{rest.StartLine,-5} {rest.Signature}");
                    break;
                }
                var bodyLen = m.EndLine - m.StartLine + 1;
                var showEnd = bodyLen <= remaining ? m.EndLine : m.StartLine + remaining - 1;
                for (var i = m.StartLine; i <= showEnd && i <= lines.Length; i++)
                    sb.AppendLine($"{i,5}  {lines[i - 1]}");
                if (bodyLen > remaining)
                {
                    sb.AppendLine($"  … (truncado a {remaining} de {bodyLen} líneas)");
                    remaining = 0;
                }
                else
                    remaining -= bodyLen;
                shown++;
            }
        }
        else
        {
            // Truncado simple por línea
            var truncated = end - start + 1 > maxBodyLines;
            var shownEnd = truncated ? start + maxBodyLines - 1 : end;
            for (var i = start; i <= shownEnd && i <= lines.Length; i++)
                sb.AppendLine($"{i,5}  {lines[i - 1]}");
            if (truncated)
            {
                var memberList = members.Where(m => m.StartLine > shownEnd).OrderBy(m => m.StartLine).ToList();
                sb.AppendLine($"  … +{end - shownEnd} líneas. Miembros restantes:");
                foreach (var m in memberList)
                    sb.AppendLine($"    L{m.StartLine,-5} {m.Signature}");
            }
        }
        return sb.ToString();
    }

    private static string RenderSlice(string typeName, string file, MemberSpan m, string[] lines, int maxBodyLines)
    {
        var start = Math.Clamp(m.StartLine, 1, lines.Length);
        var end = Math.Clamp(m.EndLine, start, lines.Length);
        var truncated = false;
        if (end - start + 1 > maxBodyLines)
        {
            end = start + maxBodyLines - 1;
            truncated = true;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"// {Path.GetFileName(file)}:{m.StartLine}  {typeName}.{m.MemberName}");
        for (var i = start; i <= end; i++)
            sb.AppendLine($"{i,5}  {lines[i - 1]}");
        if (truncated) sb.AppendLine($"  … ({m.EndLine - m.StartLine + 1} líneas en total, truncado a {maxBodyLines})");
        return sb.ToString();
    }

    // ---- NUEVO: comprensión en una sola llamada (compite con explore) ----
    // Devuelve el cuerpo COMPLETO del tipo + contexto curado del grafo (DI, deps,
    // callers, endpoints). A diferencia de explore, no vuelca ficheros vecinos:
    // el contexto se da como metadatos compactos, y el código como un único tipo.
    public string Understand(string typeName, int bodyBudget)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null || !_nodes.TryGetValue(key, out var node))
                return $"Type '{typeName}' not found among defined types. Try search().";
            typeName = key;
            bodyBudget = Math.Clamp(bodyBudget, 20, 800);

            var sb = new StringBuilder();
            var file = _files.GetValueOrDefault(typeName);
            var members = (_members.GetValueOrDefault(typeName) ?? []).OrderBy(m => m.StartLine).ToList();
            var bodyLines = node.EndLine - node.StartLine + 1;

            // Mejora 2: framing condicional. Clases pequeñas (<50 líneas) llevan
            // solo una cabecera compacta de 1 línea; las grandes llevan el bloque
            // completo (DI, used-by, uses).
            if (bodyLines < 50)
            {
                // Header compacto: tipo + resumen + adyacencia en 1 línea
                var summary = node.Summary is not null ? $" | {node.Summary}" : "";
                var graphCtx = UnderstandCompactContext(typeName);
                sb.AppendLine($"// {Path.GetFileName(file ?? "?")}:{node.StartLine}  {node.Kind.ToString().ToLowerInvariant()} " +
                              $"{Display(typeName)} (L{node.StartLine}-{node.EndLine}){summary}");
                if (!string.IsNullOrWhiteSpace(graphCtx))
                    sb.AppendLine($"// graph: {graphCtx}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"== Understand: {Display(typeName)} ==  [{(file is null ? "?" : Path.GetFileName(file))}]");
                sb.AppendLine($"kind: {node.Kind.ToString().ToLowerInvariant()}" + (node.Summary is not null ? $"  |  {node.Summary}" : ""));

                // DI
                if (_diByService.TryGetValue(typeName, out var asSvc))
                    sb.AppendLine($"DI: {Display(typeName)} → {string.Join(", ", asSvc.Select(d => $"{Display(d.ImplementationType)} [{d.Lifetime}]").Distinct())}");
                if (_diByImpl.TryGetValue(typeName, out var asImpl))
                    sb.AppendLine($"DI: registrado como {string.Join(", ", asImpl.Select(d => $"{Display(d.ServiceType)} [{d.Lifetime}]").Distinct())}");

                // endpoints directos + cercanos (compacto)
                var eps = FormatEndpoints(typeName);
                var nearbyEps = FindNearbyEndpoints(typeName, 4, 4);
                if (eps.Count > 0) sb.AppendLine($"endpoints: {string.Join(" | ", eps)}");
                else if (nearbyEps.Count > 0) sb.AppendLine($"endpoints (vía cadena): {string.Join(" | ", nearbyEps.Take(3))}");

                // Mejora 6: compresión de contexto. "used by" y "uses" en 1 línea compacta.
                sb.AppendLine($"graph: {UnderstandCompactContext(typeName)}");
                sb.AppendLine();
            }

            // cuerpo del tipo
            if (file is not null && File.Exists(file))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { lines = []; }
                var start = Math.Clamp(node.StartLine, 1, Math.Max(1, lines.Length));
                var end = Math.Clamp(node.EndLine, start, Math.Max(1, lines.Length));
                var shownEnd = end;

                // Mejora 4: chunking por miembro. No cortar miembros a la mitad.
                // La línea de corte se alinea al final del último miembro que quepa.
                if (end - start + 1 > bodyBudget)
                {
                    var rawCut = start + bodyBudget - 1;
                    // buscar el miembro más cercano cuyo EndLine <= rawCut
                    var bestEnd = start - 1;
                    foreach (var m in members)
                    {
                        if (m.EndLine <= rawCut && m.EndLine > bestEnd)
                            bestEnd = m.EndLine;
                    }
                    if (bestEnd >= start)
                        shownEnd = bestEnd;
                    else
                        shownEnd = rawCut; // fallback: ningún miembro completo cabe
                }

                if (shownEnd > end) shownEnd = end;
                for (var i = start; i <= shownEnd && i <= lines.Length; i++)
                    sb.AppendLine($"{i,5}  {lines[i - 1]}");
                if (shownEnd < end)
                {
                    sb.AppendLine($"  … +{end - shownEnd} líneas. Miembros restantes:");
                    foreach (var m in members.Where(m => m.StartLine > shownEnd).OrderBy(m => m.StartLine))
                        sb.AppendLine($"    L{m.StartLine,-5} {m.Signature}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Mejora 6: contexto comprimido del grafo en 1 línea.
    /// ← callers (con relación) | → deps (con relación) [+ DI]
    /// </summary>
    private string UnderstandCompactContext(string typeName)
    {
        var parts = new List<string>();

        // used by (callers)
        var callers = _in.GetValueOrDefault(typeName);
        if (callers is not null && callers.Count > 0)
        {
            var topCallers = callers.Where(n => !IsTestType(n))
                .OrderByDescending(Rank).Take(5).Select(n =>
                    $"{Display(n)}[{DominantRelation(n, typeName).Label()}]");
            var extra = callers.Count(n => !IsTestType(n)) - 5;
            var callersStr = string.Join(", ", topCallers) + (extra > 0 ? $" +{extra}" : "");
            parts.Add($"← {callersStr}");
        }
        else
            parts.Add("← none");

        // uses (deps)
        if (_out.TryGetValue(typeName, out var edges) && edges.Count > 0)
        {
            var topDeps = edges.GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
                .Select(g => (To: g.Key, Rel: g.Select(e => e.Relation).OrderByDescending(RelationRank).First()))
                .Take(5).Select(x => $"{Display(x.To)}[{x.Rel.Label()}]");
            var depsStr = string.Join(", ", topDeps);
            parts.Add($"→ {depsStr}");
        }
        else
            parts.Add("→ none");

        // DI hint
        if (_diByService.TryGetValue(typeName, out var asSvc))
            parts.Add($"DI⇒{string.Join(",", asSvc.Select(d => Display(d.ImplementationType)).Distinct())}");
        if (_diByImpl.TryGetValue(typeName, out var asImpl))
            parts.Add($"DI⇐{string.Join(",", asImpl.Select(d => Display(d.ServiceType)).Distinct())}");

        return string.Join(" | ", parts);
    }

    // ---- NUEVO: flow — destila la secuencia de llamadas (comprensión de flujo) ----
    // Devuelve el árbol de llamadas SALIENTES de un método, recursivo a profundidad N,
    // con fichero:línea. Es el "cómo funciona / qué orquesta" a una fracción de los
    // tokens del código fuente, cruzando ficheros que tendrías que leer enteros.
    public string Flow(string typeName, string? member, int depth)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null) return $"Type '{typeName}' not found. Try search().";
            typeName = key;
            depth = Math.Clamp(depth, 1, 5);

            var sb = new StringBuilder();
            var budget = new[] { 80 }; // tope de nodos para evitar explosión

            if (member is not null)
            {
                sb.AppendLine($"Flow de {Display(typeName)}.{member} (depth {depth}):");
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var n = RenderFlow(typeName, member, sb, 0, depth, visited, budget);
                if (n == 0)
                    sb.AppendLine("  (sin llamadas resueltas; puede ser hoja o usar dispatch dinámico. Prueba get_source.)");
            }
            else
            {
                // sin método: vista de un nivel por cada método público con llamadas
                sb.AppendLine($"Flow de {Display(typeName)} (llamadas salientes por método):");
                var members = (_members.GetValueOrDefault(typeName) ?? [])
                    .Where(m => m.IsPublic).OrderBy(m => m.StartLine).ToList();
                var any = false;
                foreach (var m in members)
                {
                    var calls = CallsOf(typeName, m.MemberName);
                    if (calls.Count == 0) continue;
                    any = true;
                    sb.AppendLine($"  {m.MemberName}()");
                    foreach (var c in calls)
                        sb.AppendLine($"    → {Display(c.CalleeType)}.{c.CalleeMember}()  :{c.Line}");
                }
                if (!any) sb.AppendLine("  (sin llamadas salientes resueltas en métodos públicos.)");
            }
            return sb.ToString();
        }
    }

    private List<CallSite> CallsOf(string type, string member)
        => _callsByCaller.TryGetValue(type, out var all)
            ? all.Where(c => c.CallerMember.Equals(member, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(c => c.Line).ToList()
            : [];

    private int RenderFlow(string type, string member, StringBuilder sb, int level, int maxDepth,
        HashSet<string> visited, int[] budget)
    {
        if (budget[0] <= 0) return 0;
        var calls = CallsOf(type, member);
        var count = 0;
        foreach (var c in calls)
        {
            if (budget[0]-- <= 0) { sb.AppendLine(new string(' ', (level + 1) * 2) + "… (truncado)"); break; }
            var indent = new string(' ', (level + 1) * 2);

            // seguir el binding DI: si el callee es una interfaz sin llamadas propias
            // pero tiene implementación registrada, recursamos en la implementación.
            var recurseType = c.CalleeType;
            if (CallsOf(recurseType, c.CalleeMember).Count == 0 && _diByService.TryGetValue(recurseType, out var di))
            {
                var impl = di.Select(d => d.ImplementationType).FirstOrDefault(i => CallsOf(i, c.CalleeMember).Count > 0)
                           ?? di.Select(d => d.ImplementationType).FirstOrDefault();
                if (impl is not null) recurseType = impl;
            }
            var implNote = !recurseType.Equals(c.CalleeType, StringComparison.OrdinalIgnoreCase)
                ? $"  [impl: {Display(recurseType)}]" : "";
            sb.AppendLine($"{indent}→ {Display(c.CalleeType)}.{c.CalleeMember}()  :{c.Line}{implNote}");
            count++;

            // Deduplicación de ciclos: la clave es "{recurseType}.{member}".
            // IMPORTANTE: se usa recurseType (el tipo con el que realmente recursamos,
            // tras aplicar el rebind DI) y NO c.CalleeType. Esto es lo que corta los
            // ciclos A.M -> IB.M (=> B.M) -> IA.M (=> A.M): la segunda visita a A.M
            // encuentra "A.M" ya en visited y no desciende. El Remove al final permite
            // que el MISMO tipo+miembro pueda aparecer por otra rama distinta (paths
            // independientes que convergen), que es legítimo.
            // Test de regresión: FlowCycleTests.Flow_On_Di_Cycle_Follows_Rebind_And_Stops_At_Cycle_Contract.
            var sig = $"{recurseType}.{c.CalleeMember}";
            if (level + 1 < maxDepth && _nodes.ContainsKey(recurseType) && visited.Add(sig))
            {
                RenderFlow(recurseType, c.CalleeMember, sb, level + 1, maxDepth, visited, budget);
                visited.Remove(sig);
            }
        }
        return count;
    }

    private string CompactNeighbors(string typeName, HashSet<string>? names, bool reverse, int limit = 8)
    {
        if (names is null || names.Count == 0) return "(ninguno)";
        var items = names.Where(n => !IsTestType(n))
            .OrderByDescending(Rank).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).Take(limit)
            .Select(n => $"{Display(n)} [{(reverse ? DominantRelation(n, typeName) : DominantRelation(typeName, n)).Label()}]");
        var extra = names.Count(n => !IsTestType(n)) - limit;
        return string.Join(", ", items) + (extra > 0 ? $", +{extra}" : "");
    }

    private string CompactDeps(string typeName, int limit = 8)
    {
        if (!_out.TryGetValue(typeName, out var edges) || edges.Count == 0) return "(ninguno)";
        var groups = edges.GroupBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .Select(g => (To: g.Key, Rel: g.Select(e => e.Relation).OrderByDescending(RelationRank).First()))
            .OrderBy(x => x.To, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = groups.Take(limit).Select(x => $"{Display(x.To)} [{x.Rel.Label()}]");
        var extra = groups.Count - limit;
        return string.Join(", ", shown) + (extra > 0 ? $", +{extra}" : "");
    }

    // ---- NUEVO: dónde se invoca realmente un tipo (a nivel de método) ----

    public string FindCallSites(string typeName, string? member, int limit)
    {
        lock (_lock)
        {
            limit = Math.Clamp(limit, 1, 100);
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is not null) typeName = key;
            if (!_callsByCallee.TryGetValue(typeName, out var calls) || calls.Count == 0)
            {
                var hint = _in.TryGetValue(typeName, out var inv) && inv.Count > 0
                    ? $" Pero {inv.Count} tipos lo referencian estructuralmente (find_callers)."
                    : "";
                return $"No se registraron invocaciones de métodos de '{Display(typeName)}'.{hint}";
            }

            var filtered = calls
                .Where(c => member is null || c.CalleeMember.Equals(member, StringComparison.OrdinalIgnoreCase))
                .Where(c => !IsTestType(c.CallerType))
                .OrderBy(c => c.CalleeMember, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CallerType, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filtered.Count == 0)
                return $"No call-sites for '{Display(typeName)}.{member}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Call-sites of '{Display(typeName)}{(member is null ? "" : "." + member)}' ({filtered.Count}):");
            foreach (var c in filtered.Take(limit))
            {
                var file = _files.TryGetValue(c.CallerType, out var f) ? Path.GetFileName(f) : "?";
                sb.AppendLine($"  {Display(c.CalleeType)}.{c.CalleeMember}()  ←  {Display(c.CallerType)}.{c.CallerMember}  @ {file}:{c.Line}");
            }
            if (filtered.Count > limit) sb.AppendLine($"  … +{filtered.Count - limit} más");
            return sb.ToString();
        }
    }

    // ---- NUEVO: resolución de inyección de dependencias ----

    public string ResolveDi(string typeName)
    {
        lock (_lock)
        {
            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is not null) typeName = key;

            var sb = new StringBuilder();
            var found = false;
            if (_diByService.TryGetValue(typeName, out var asService))
            {
                found = true;
                sb.AppendLine($"'{Display(typeName)}' (servicio) se resuelve a:");
                foreach (var d in asService.DistinctBy(d => (d.ImplementationType, d.Lifetime)))
                    sb.AppendLine($"  → {Display(d.ImplementationType)}  [{d.Lifetime}] (L{d.Line})");
            }
            if (_diByImpl.TryGetValue(typeName, out var asImpl))
            {
                found = true;
                sb.AppendLine($"'{Display(typeName)}' (implementación) está registrada para:");
                foreach (var d in asImpl.DistinctBy(d => (d.ServiceType, d.Lifetime)))
                    sb.AppendLine($"  ← {Display(d.ServiceType)}  [{d.Lifetime}] (L{d.Line})");
            }
            return found ? sb.ToString() : $"No se encontró binding DI para '{typeName}'.";
        }
    }

    public string SearchSemantic(string query, int topK = 10)
    {
        lock (_lock)
        {
            topK = Math.Clamp(topK, 1, 30);
            if (_docs.Count == 0) return "No hay tipos públicos indexados. Ejecuta scan().";

            var queryTerms = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (queryTerms.Count == 0) return "La query no contiene términos útiles.";

            var n = _docs.Count;
            var avgLen = _totalDocLength == 0 ? 1d : (double)_totalDocLength / n;
            const double k1 = 1.5, b = 0.75;

            var scored = new List<(Doc Doc, double Score)>();
            foreach (var doc in _docs)
            {
                var score = 0d;
                foreach (var term in queryTerms)
                {
                    if (!doc.Tf.TryGetValue(term, out var tf) || tf == 0) continue;
                    var df = _docFrequency.GetValueOrDefault(term, 0);
                    if (df == 0) continue;
                    var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    var denom = tf + k1 * (1 - b + b * (doc.Length / avgLen));
                    score += idf * (tf * (k1 + 1)) / denom;
                }
                if (score > 0) scored.Add((doc, score));
            }

            if (scored.Count == 0) return $"No se encontraron tipos similares para '{query}'.";

            var top = scored.OrderByDescending(x => x.Score).Take(topK).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Tipos semánticamente cercanos a '{query}' ({top.Count}):");
            foreach (var item in top)
            {
                var node = _nodes.GetValueOrDefault(item.Doc.Type);
                var file = _files.TryGetValue(item.Doc.Type, out var f) ? $" [{Path.GetFileName(f)}]" : "";
                sb.AppendLine($"  score={item.Score:F2} | {Display(item.Doc.Type)}{file}");
                if (node?.Summary is not null) sb.AppendLine($"    {node.Summary}");
            }
            return sb.ToString();
        }
    }

    public string ExploreContext(string typeOrPattern, int depth = 2, int limitPerGroup = 8)
    {
        lock (_lock)
        {
            depth = Math.Clamp(depth, 1, 4);
            limitPerGroup = Math.Clamp(limitPerGroup, 3, 15);
            if (string.IsNullOrWhiteSpace(typeOrPattern)) return "Provide a type name or partial pattern.";

            var seeds = ResolveSeeds(typeOrPattern);
            if (seeds.Count == 0) return $"No types found matching '{typeOrPattern}'.";

            var sb = new StringBuilder();
            if (seeds.Count > 1)
            {
                sb.AppendLine($"Exploring top {seeds.Count} matches for '{typeOrPattern}': {string.Join(", ", seeds.Select(Display))}");
                sb.AppendLine();
            }
            foreach (var seed in seeds) { AppendContext(sb, seed, depth, limitPerGroup); sb.AppendLine(); }
            return sb.ToString().TrimEnd();
        }
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

    private void AppendContext(StringBuilder sb, string seed, int depth, int limitPerGroup)
    {
        var file = _files.TryGetValue(seed, out var fp) ? $" [{Path.GetFileName(fp)}]" : "";
        var callersCount = _in.TryGetValue(seed, out var callers) ? callers.Count : 0;
        var usesCount = _out.TryGetValue(seed, out var outv) ? CountDistinctTargets(outv) : 0;

        sb.AppendLine($"Context around '{Display(seed)}'{file}");
        sb.AppendLine($"  Summary: callers={callersCount}, uses={usesCount}, endpoints={GetEndpointCount(seed)}, call-sites-in={(_callsByCallee.TryGetValue(seed, out var c) ? c.Count : 0)}");

        var node = _nodes.GetValueOrDefault(seed);
        if (node?.Summary is not null) sb.AppendLine($"  Doc: {node.Summary}");

        var directEndpoints = FormatEndpoints(seed);
        if (directEndpoints.Count > 0) AppendGroup(sb, "Direct HTTP endpoints", directEndpoints, limitPerGroup);

        if (_diByService.TryGetValue(seed, out var di))
            AppendGroup(sb, "DI resolves to", di.Select(d => $"{Display(d.ImplementationType)} ({d.Lifetime})").Distinct().ToList(), limitPerGroup);

        var nearbyCallers = Traverse(seed, _in, depth, limitPerGroup, skipTests: true);
        AppendGroup(sb, "Nearby callers", nearbyCallers, limitPerGroup);

        var nearbyDeps = TraverseOut(seed, depth, limitPerGroup);
        AppendGroup(sb, "Nearby dependencies", nearbyDeps, limitPerGroup);

        var nearbyEndpoints = FindNearbyEndpoints(seed, depth, limitPerGroup);
        AppendGroup(sb, "Nearby HTTP endpoints", nearbyEndpoints, limitPerGroup);
    }

    private List<string> Traverse(string start, Dictionary<string, HashSet<string>> adjacency, int maxDepth, int limit, bool skipTests)
    {
        var queue = new Queue<(string Name, int Dist)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var results = new List<string>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0 && results.Count < limit)
        {
            var (name, dist) = queue.Dequeue();
            if (dist >= maxDepth || !adjacency.TryGetValue(name, out var next)) continue;
            foreach (var cand in next.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Add(cand) || (skipTests && IsTestType(cand))) continue;
                results.Add(FormatNeighbor(cand, dist + 1));
                if (results.Count >= limit) break;
                queue.Enqueue((cand, dist + 1));
            }
        }
        return results;
    }

    private List<string> TraverseOut(string start, int maxDepth, int limit)
    {
        var queue = new Queue<(string Name, int Dist)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var results = new List<string>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0 && results.Count < limit)
        {
            var (name, dist) = queue.Dequeue();
            if (dist >= maxDepth || !_out.TryGetValue(name, out var edges)) continue;
            foreach (var to in edges.Select(e => e.To).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Add(to)) continue;
                results.Add(FormatNeighbor(to, dist + 1));
                if (results.Count >= limit) break;
                queue.Enqueue((to, dist + 1));
            }
        }
        return results;
    }

    private List<string> FindNearbyEndpoints(string seed, int maxDepth, int limit)
    {
        var queue = new Queue<(string Name, int Dist)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seed };
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        queue.Enqueue((seed, 0));
        while (queue.Count > 0 && results.Count < limit)
        {
            var (name, dist) = queue.Dequeue();
            if (dist >= maxDepth || !_in.TryGetValue(name, out var callers)) continue;
            foreach (var caller in callers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (IsTestType(caller) || !visited.Add(caller)) continue;
                parents[caller] = name;
                queue.Enqueue((caller, dist + 1));
                if (!_endpoints.TryGetValue(caller, out var eps) || eps.Count == 0) continue;
                var chain = BuildChain(caller, seed, parents);
                foreach (var ep in eps)
                {
                    results.Add($"[{ep.Verb} {ep.Route}] {Display(caller)}.{ep.MethodName} ← {chain}");
                    if (results.Count >= limit) break;
                }
                if (results.Count >= limit) break;
            }
        }
        return results;
    }

    private string BuildChain(string from, string seed, Dictionary<string, string> parents)
    {
        var chain = new List<string>();
        var current = from;
        while (true)
        {
            chain.Add(Display(current));
            if (current.Equals(seed, StringComparison.OrdinalIgnoreCase)) break;
            if (!parents.TryGetValue(current, out current!)) break;
        }
        return string.Join(" ← ", chain);
    }

    private List<string> FormatEndpoints(string typeName)
        => _endpoints.TryGetValue(typeName, out var eps)
            ? eps.Select(e => $"[{e.Verb} {e.Route}] {Display(typeName)}.{e.MethodName}").ToList()
            : [];

    private string FormatNeighbor(string name, int distance)
    {
        var endpointTag = _endpoints.TryGetValue(name, out var eps) && eps.Count > 0
            ? $" [ENDPOINT: {string.Join(", ", eps.Select(e => $"{e.Verb} {e.Route}"))}]"
            : "";
        return $"{Display(name)} (distance {distance}){endpointTag}";
    }

    private static void AppendGroup(StringBuilder sb, string title, List<string> items, int limit)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"  {title}:");
        foreach (var item in items.Take(limit)) sb.AppendLine($"    - {item}");
    }

    private int GetEndpointCount(string typeName) => _endpoints.TryGetValue(typeName, out var eps) ? eps.Count : 0;

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

    private static Dictionary<string, int> BuildTermFrequencies(string text)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(text)) map[token] = map.GetValueOrDefault(token, 0) + 1;
        return map;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var cleaned = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                if (i > 0 && char.IsUpper(c) && char.IsLetter(text[i - 1]) && char.IsLower(text[i - 1]))
                    cleaned.Append(' ');
                cleaned.Append(char.ToLowerInvariant(c));
            }
            else cleaned.Append(' ');
        }
        return cleaned.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2);
    }
}
