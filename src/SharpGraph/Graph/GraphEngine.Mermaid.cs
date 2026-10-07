using System.Text;

namespace SharpGraph.Graph;

/// <summary>
/// Exportación del grafo a diagramas Mermaid en TEXTO PLANO, sin dependencias de
/// renderizado. Tres vistas sobre el mismo grafo:
///
///   - mermaid_context:  travesía bidireccional desde un ancla (callers hacia los
///     endpoints HTTP arriba, árbol de dependencias abajo) → flowchart.
///   - mermaid_sequence: cadenas ancla → endpoint con sus llamadas salientes →
///     sequenceDiagram.
///   - mermaid_overview: mapa de arquitectura multi-endpoint agrupado por namespace.
///
/// El output es un bloque ```mermaid``` pegable tal cual en Markdown (GitHub,
/// GitLab, Obsidian y mermaid.live lo renderizan nativamente). Misma filosofía que
/// el resto de tools: texto acotado, cero binarios, cero dependencias nuevas.
/// </summary>
public sealed partial class GraphEngine
{
    // ─────────────────────────── mermaid_context ───────────────────────────

    /// <summary>
    /// Diagrama Mermaid del contexto de un tipo: subgrafo bidireccional (callers
    /// hasta endpoints + dependencias) serializado como flowchart. Los parámetros
    /// nombran SEMÁNTICA (callers/deps), no orientación visual: eso lo decide
    /// <paramref name="direction"/> (TD/LR).
    /// </summary>
    public string MermaidContext(
        string typeName,
        int callersDepth = 3,
        int depsDepth = 3,
        int maxNodes = 40,
        string direction = "TD",
        bool includeExternal = false)
    {
        lock (_lock)
        {
            callersDepth = Math.Clamp(callersDepth, 0, 6);
            depsDepth = Math.Clamp(depsDepth, 0, 6);
            maxNodes = Math.Clamp(maxNodes, 5, 100);
            var dir = direction.Equals("LR", StringComparison.OrdinalIgnoreCase) ? "LR" : "TD";

            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null) return $"Type '{typeName}' not found. Try search().";

            var mg = new MgBuilder(maxNodes);
            mg.AddNode(key);
            CollectCallers(key, callersDepth, mg, includeExternal);
            CollectDeps(key, depsDepth, mg, includeExternal);

            var title = $"mermaid_context: {Display(key)} — callers≤{callersDepth} · deps≤{depsDepth}";
            return RenderFlowchart(title, mg, dir, anchorKey: key, includeExternal: includeExternal);
        }
    }

    // ─────────────────────────── mermaid_sequence ───────────────────────────

    /// <summary>
    /// SequenceDiagram de las cadenas estructurales que van del ancla a los endpoints
    /// HTTP que la invocan (misma búsqueda que trace_to_endpoints, sin heurísticos),
    /// seguidas de las llamadas salientes del ancha a nivel de método (como flow a
    /// 1 nivel). Ideal para "enséñame el recorrido completo de esta petición".
    /// </summary>
    public string MermaidSequence(string typeName, string? member = null, int maxPaths = 3, int maxDepth = 8)
    {
        lock (_lock)
        {
            maxPaths = Math.Clamp(maxPaths, 1, 8);
            maxDepth = Math.Clamp(maxDepth, 1, 12);

            var key = ResolveInput(typeName, out var amb);
            if (amb is not null) return amb;
            if (key is null) return $"Type '{typeName}' not found. Try search().";

            // 1) caminos estructurados ancla → endpoint (solo aristas reales:
            //    sin el pivote heurístico de trace_to_endpoints)
            var paths = new List<(List<string> Nodes, List<EdgeRelation> Rels)>();
            var path = new List<string> { key };
            var rels = new List<EdgeRelation>();
            var visited = new HashSet<string>(Cmp) { key };
            var visits = new int[1];
            CollectEndpointPaths(key, path, rels, visited, paths, maxDepth, maxPaths, visits);
            // cadenas cortas primero: se leen mejor y gastan menos tokens
            paths = paths.OrderBy(p => p.Nodes.Count).ThenBy(p => p.Nodes[^1], Cmp).ToList();

            // 2) llamadas salientes del ancla (member concreto o todos los públicos)
            var outgoing = CollectAnchorOutgoing(key, member, limit: 20);

            if (paths.Count == 0 && outgoing.Count == 0)
                return $"'{Display(key)}' no tiene caminos a endpoints ni llamadas salientes resueltas. " +
                       "Prueba trace_to_endpoints / find_callers para contexto.";

            var who = member is null ? Display(key) : $"{Display(key)}.{member}";
            return RenderSequence(key, who, paths, outgoing);
        }
    }

    // ─────────────────────────── mermaid_overview ───────────────────────────

    /// <summary>
    /// Mapa de arquitectura: BFS multi-fuente desde todos los endpoints HTTP (o los
    /// que coincidan con <paramref name="area"/>) hacia sus dependencias, agrupado
    /// en subgraphs por namespace. Sin endpoints indexados cae al top-5 por PageRank.
    /// Es el diagrama de cabecera para docs/architecture/overview.md.
    /// </summary>
    public string MermaidOverview(string? area = null, int depsDepth = 2, int maxNodes = 60)
    {
        lock (_lock)
        {
            depsDepth = Math.Clamp(depsDepth, 1, 5);
            maxNodes = Math.Clamp(maxNodes, 10, 150);

            var seeds = _endpoints.Keys.Where(k => !IsDiagramNoise(k)).ToList();
            if (!string.IsNullOrWhiteSpace(area))
            {
                seeds = seeds.Where(k => k.Contains(area, StringComparison.OrdinalIgnoreCase) ||
                                         (_nodes.TryGetValue(k, out var n) && n.Namespace.Contains(area, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (seeds.Count == 0)
                    return $"No hay endpoints cuyo tipo o namespace contenga '{area}'. Endpoints indexados: {_endpoints.Count}.";
            }
            var note = "";
            if (seeds.Count == 0)
            {
                seeds = _nodes.Keys.Where(k => !IsDiagramNoise(k)).OrderByDescending(Rank).Take(5).ToList();
                if (seeds.Count == 0) return "Grafo vacío: ejecuta scan() primero.";
                note = "%% sin endpoints indexados — semillas: top-5 por PageRank";
            }

            var mg = new MgBuilder(maxNodes);
            foreach (var seed in seeds) mg.AddNode(seed);

            // BFS multi-fuente hacia las dependencias, mismo filtro que mermaid_context
            var frontier = new List<string>(seeds);
            for (var depth = 1; depth <= depsDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var current in frontier)
                {
                    if (!_out.TryGetValue(current, out var edges)) continue;
                    foreach (var g in edges.Where(e => IsStructuralRelation(e.Relation)).GroupBy(e => e.To, Cmp))
                    {
                        var to = g.Key;
                        if (IsDiagramNoise(to) || !_nodes.ContainsKey(to)) continue;
                        var rel = g.Select(e => e.Relation).OrderByDescending(RelationRank).First();
                        if (IsMediatRArtifact(current, to, rel)) continue;
                        var enqueued = !mg.Visited.Contains(to);
                        if (mg.AddEdge(current, to, rel) && enqueued) next.Add(to);
                    }
                }
                frontier = next;
            }

            var title = $"mermaid_overview — {seeds.Count} entrada(s) · deps≤{depsDepth}";
            return RenderFlowchart(title, mg, "TD", groupOf: NamespaceOf, extraNote: note);
        }
    }

    // ─────────────────────────── recolectores ───────────────────────────

    /// <summary>
    /// BFS hacia los llamadores (hasta maxDepth niveles). Los endpoints se dibujan
    /// pero no se expanden: son entrada del sistema y no hay nada útil por encima.
    /// Propaga DI igual que impact(): si 'current' es una implementación, la interfaz
    /// que la registra entra como paso intermedio con arista di-bound.
    /// </summary>
    private void CollectCallers(string anchor, int maxDepth, MgBuilder mg, bool includeExternal)
    {
        if (maxDepth <= 0) return;
        var frontier = new List<string> { anchor };
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                // (1) callers estructurales: caller → current
                if (_in.TryGetValue(current, out var callers))
                    foreach (var caller in callers)
                    {
                        if (IsDiagramNoise(caller)) continue;
                        if (!includeExternal && !_nodes.ContainsKey(caller)) continue;
                        var rel = DominantRelation(caller, current);
                        if (IsMediatRArtifact(caller, current, rel)) continue;
                        var expandable = !mg.Visited.Contains(caller);
                        if (mg.AddEdge(caller, current, rel) &&
                            expandable && !_endpoints.ContainsKey(caller))
                            next.Add(caller);
                    }

                // (2) propagación DI: implementación ← interfaz que la registra
                if (_diByImpl.TryGetValue(current, out var bindings))
                    foreach (var b in bindings)
                    {
                        var svc = b.ServiceType;
                        if (IsDiagramNoise(svc) || Cmp.Equals(svc, current)) continue;
                        if (!includeExternal && !_nodes.ContainsKey(svc)) continue;
                        var expandable = !mg.Visited.Contains(svc);
                        if (mg.AddEdge(svc, current, EdgeRelation.DiBound) &&
                            expandable && !_endpoints.ContainsKey(svc))
                            next.Add(svc);
                    }
            }
            frontier = next;
        }
    }

    /// <summary>BFS hacia las dependencias: aristas estructurales salientes, con
    /// rebind DI natural (IFoo -.-> Foo aparece por la propia arista di-bound del
    /// grafo). Paralelas entre el mismo par colapsan a la relación dominante.</summary>
    private void CollectDeps(string anchor, int maxDepth, MgBuilder mg, bool includeExternal)
    {
        if (maxDepth <= 0) return;
        var frontier = new List<string> { anchor };
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                if (!_out.TryGetValue(current, out var edges)) continue;
                foreach (var g in edges.Where(e => IsStructuralRelation(e.Relation)).GroupBy(e => e.To, Cmp))
                {
                    var to = g.Key;
                    if (IsDiagramNoise(to)) continue;
                    if (!includeExternal && !_nodes.ContainsKey(to)) continue;
                    var rel = g.Select(e => e.Relation).OrderByDescending(RelationRank).First();
                    if (IsMediatRArtifact(current, to, rel)) continue;
                    var expandable = !mg.Visited.Contains(to);
                    if (mg.AddEdge(current, to, rel) && expandable)
                        next.Add(to);
                }
            }
            frontier = next;
        }
    }

    /// <summary>DFS estructurada ancla → endpoints. Igual que el Dfs de
    /// trace_to_endpoints pero devolviendo (cadena, relaciones) en vez de texto,
    /// y sin pivote heurístico: el diagrama solo dibuja aristas reales.</summary>
    private void CollectEndpointPaths(string current, List<string> path, List<EdgeRelation> rels,
        HashSet<string> visited, List<(List<string>, List<EdgeRelation>)> results,
        int maxDepth, int maxPaths, int[] visits)
    {
        if (results.Count >= maxPaths || ++visits[0] > 20_000) return;

        if (_endpoints.TryGetValue(current, out var eps) && eps.Count > 0)
        {
            results.Add((new List<string>(path), new List<EdgeRelation>(rels)));
            return; // el endpoint es entrada: no se sigue por encima
        }
        if (path.Count > maxDepth) return;
        if (!_in.TryGetValue(current, out var callers)) return;

        foreach (var caller in callers)
        {
            if (visited.Contains(caller) || IsDiagramNoise(caller)) continue;
            if (IsMediatRArtifact(caller, current, DominantRelation(caller, current))) continue;
            visited.Add(caller);
            path.Add(caller);
            rels.Add(DominantRelation(caller, current));
            CollectEndpointPaths(caller, path, rels, visited, results, maxDepth, maxPaths, visits);
            visited.Remove(caller);
            path.RemoveAt(path.Count - 1);
            rels.RemoveAt(rels.Count - 1);
        }
    }

    private List<(string Callee, string CalleeMember)> CollectAnchorOutgoing(string anchor, string? member, int limit)
    {
        var calls = member is not null
            ? CallsOf(anchor, member)
            : _members.TryGetValue(anchor, out var members)
                ? members.Where(m => m.IsPublic).OrderBy(m => m.StartLine)
                    .SelectMany(m => CallsOf(anchor, m.MemberName))
                : _callsByCaller.TryGetValue(anchor, out var all) ? all : [];
        return calls
            .DistinctBy(c => (c.CalleeType, c.CalleeMember))
            .Take(limit)
            .Select(c => (c.CalleeType, c.CalleeMember))
            .ToList();
    }

    private string? NamespaceOf(string key)
        => _nodes.TryGetValue(key, out var n) && !string.IsNullOrEmpty(n.Namespace) ? n.Namespace : null;

    // ─────────────────────────── render ───────────────────────────

    /// <summary>Serializa el subgrafo acumulado como bloque ```mermaid``` flowchart.
    /// Los IDs de nodo son N0..Nn (los FQN llevan puntos, ilegales como ID) y el
    /// nombre legible va en el label; endpoints con su ruta, ancla destacada y
    /// opcionalmente subgraphs por namespace.</summary>
    private string RenderFlowchart(string title, MgBuilder mg, string direction,
        string? anchorKey = null, bool includeExternal = false,
        Func<string, string?>? groupOf = null, string? extraNote = null)
    {
        var ids = new Dictionary<string, string>(Cmp);
        for (var i = 0; i < mg.Order.Count; i++) ids[mg.Order[i]] = $"N{i}";

        string? CssOf(string key)
        {
            if (anchorKey is not null && Cmp.Equals(key, anchorKey)) return "anchor";
            if (!_nodes.TryGetValue(key, out var node)) return includeExternal ? "external" : null;
            if (_endpoints.ContainsKey(key)) return "entry";
            return node.Kind == NodeKind.Interface ? "iface" : null;
        }

        string LabelOf(string key)
        {
            var name = EscapeMermaid(Display(key));
            if (_endpoints.TryGetValue(key, out var eps))
            {
                var routes = string.Join("<br/>", eps
                    .DistinctBy(e => (e.Verb, e.Route))
                    .Select(e => EscapeMermaid($"{e.Verb} {e.Route}")));
                return $"{routes}<br/>{name}";
            }
            return name;
        }

        void AppendNode(StringBuilder sb, string key, string indent = "  ")
            => sb.AppendLine($"{indent}{ids[key]}[\"{LabelOf(key)}\"]" + (CssOf(key) is { } css ? $":::{css}" : ""));

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine($"%% {title} — {mg.Order.Count} nodos · {mg.Edges.Count} aristas" +
                      (mg.Omitted > 0 ? $" · TRUNCADO (+{mg.Omitted} sin dibujar; sube maxNodes)" : ""));
        if (extraNote?.Length > 0) sb.AppendLine(extraNote);
        sb.AppendLine($"flowchart {direction}");
        sb.AppendLine("  classDef anchor fill:#fff3cd,stroke:#b8860b,stroke-width:3px,color:#111;");
        sb.AppendLine("  classDef entry fill:#d4edda,stroke:#1e7e34,color:#111;");
        sb.AppendLine("  classDef iface stroke-dasharray:4 3,color:#111;");
        sb.AppendLine("  classDef external fill:#ececec,stroke:#8a8a8a,stroke-dasharray:2 3,color:#333;");

        // nodos sueltos + subgraphs (máx 12 namespaces; el resto queda sin agrupar)
        var grouped = new List<(string Name, HashSet<string> Keys)>();
        if (groupOf is not null)
        {
            grouped = mg.Order
                .Select(k => (Key: k, Group: groupOf(k)))
                .Where(x => x.Group is not null)
                .GroupBy(x => x.Group!)
                .OrderByDescending(g => g.Count())
                .Take(12)
                .Select(g => (g.Key, g.Select(x => x.Key).ToHashSet(Cmp)))
                .ToList();
        }
        var groupedKeys = grouped.SelectMany(g => g.Keys).ToHashSet(Cmp);

        foreach (var node in mg.Order.Where(k => !groupedKeys.Contains(k)))
            AppendNode(sb, node);
        for (var i = 0; i < grouped.Count; i++)
        {
            sb.AppendLine($"  subgraph SG{i}[\"{EscapeMermaid(grouped[i].Name)}\"]");
            foreach (var k in mg.Order.Where(grouped[i].Keys.Contains))
                AppendNode(sb, k, indent: "    ");
            sb.AppendLine("  end");
        }

        foreach (var (from, to, rel) in mg.Edges)
        {
            var (arrow, label) = MgArrow(rel);
            sb.AppendLine(label is null
                ? $"  {ids[from]} {arrow} {ids[to]}"
                : $"  {ids[from]} {arrow}|{label}| {ids[to]}");
        }
        sb.AppendLine("```");
        return sb.ToString();
    }

    private string RenderSequence(string anchorKey, string who,
        List<(List<string> Nodes, List<EdgeRelation> Rels)> paths,
        List<(string Callee, string CalleeMember)> outgoing)
    {
        // participantes: cada camino del endpoint hacia el ancla + receptores salientes
        var ids = new Dictionary<string, string>(Cmp);
        var order = new List<string>();
        string IdOf(string node)
        {
            if (!ids.TryGetValue(node, out var id))
            {
                id = $"P{ids.Count}";
                ids[node] = id;
                order.Add(node);
            }
            return id;
        }

        foreach (var p in paths)
            for (var i = p.Nodes.Count - 1; i >= 0; i--) // endpoint primero: orden de lectura
                IdOf(p.Nodes[i]);
        IdOf(anchorKey);
        var outgoingIds = outgoing
            .Select(o => (Callee: o.Callee, Member: o.CalleeMember, Id: IdOf(o.Callee)))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine($"%% mermaid_sequence: {who} — {paths.Count} camino(s) a endpoint" +
                      (paths.Count == 0 ? " (sin cadena estructural a endpoint)" : ""));
        sb.AppendLine("sequenceDiagram");
        sb.AppendLine("  autonumber");

        foreach (var node in order)
        {
            var label = EscapeSequence(Display(node));
            if (_endpoints.TryGetValue(node, out var eps))
                label = $"{EscapeSequence($"{eps[0].Verb} {eps[0].Route}")}<br/>{label}";
            sb.AppendLine($"  participant {ids[node]} as {label}");
        }

        // mensajes de cada cadena, en orden de ejecución (endpoint → ancla)
        foreach (var p in paths)
            for (var i = p.Nodes.Count - 1; i >= 1; i--)
            {
                var (arrow, label) = MgArrow(p.Rels[i - 1]);
                var dash = arrow == "-.->" ? "-->>" : "->>";
                sb.AppendLine($"  {ids[p.Nodes[i]]}{dash}{ids[p.Nodes[i - 1]]}: {label ?? "call"}");
            }

        // llamadas salientes del ancla, a nivel de método
        foreach (var o in outgoingIds)
            sb.AppendLine($"  {ids[anchorKey]}->>{o.Id}: {EscapeSequence(o.Member)}()");

        sb.AppendLine("```");
        return sb.ToString();
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Subgrafo acumulado para render: nodos en orden BFS + aristas
    /// deduplicadas (primera arista entre un par gana). El presupuesto de nodos es
    /// duro: al agotarse los candidatos se cuentan como omitidos y se avisan en el
    /// bloque.</summary>
    private sealed class MgBuilder(int maxNodes)
    {
        public List<string> Order { get; } = [];
        /// <summary>Añadidos + descartados por presupuesto: evita reintentar nodos imposibles.</summary>
        public HashSet<string> Visited { get; } = new(Cmp);
        public List<(string From, string To, EdgeRelation Rel)> Edges { get; } = [];
        private readonly HashSet<string> _edgeKeys = new(Cmp);
        public int Omitted { get; private set; }

        public bool AddNode(string key)
        {
            if (!Visited.Add(key)) return true; // ya estaba: ok
            if (Order.Count >= maxNodes) { Omitted++; return false; }
            Order.Add(key);
            return true;
        }

        public bool AddEdge(string from, string to, EdgeRelation rel)
        {
            if (Cmp.Equals(from, to)) return false;
            if (!_edgeKeys.Add(from + "|" + to)) return false; // ya dibujada otra relación
            if (!AddNode(from) || !AddNode(to)) return false;
            Edges.Add((from, to, rel));
            return true;
        }
    }

    /// <summary>ParamType/ReturnType son metadatos de firma, no estructura: fuera de
    /// los diagramas (get_usages los detalla).</summary>
    private static bool IsStructuralRelation(EdgeRelation r)
        => r is not (EdgeRelation.ParamType or EdgeRelation.ReturnType);

    /// <summary>Nodos que nunca aportan en un diagrama: sintéticos (&lt;top-level&gt;)
    /// y tipos de test/mocks.</summary>
    private static bool IsDiagramNoise(string key)
        => key.StartsWith('<') || IsTestType(key);

    /// <summary>
    /// El visitor registra los argumentos genéricos de las interfaces base como
    /// referencias: IRequestHandler&lt;X, _&gt; deja, además del handled-by correcto,
    /// una arista Handler ⇒ X [implements] que duplica el binding MediatR y pinta
    /// un ciclo falso. Si el par ya tiene sends/handled-by en dirección opuesta,
    /// esa implements es el MISMO binding: fuera del diagrama.
    /// </summary>
    private bool IsMediatRArtifact(string from, string to, EdgeRelation rel)
    {
        if (rel is not (EdgeRelation.Implements or EdgeRelation.Inherits)) return false;
        return HasEdgeWithRelation(to, from, EdgeRelation.HandledBy)
            || HasEdgeWithRelation(to, from, EdgeRelation.Sends);
    }

    private bool HasEdgeWithRelation(string from, string to, EdgeRelation rel)
        => _out.TryGetValue(from, out var edges) &&
           edges.Any(e => e.Relation == rel && Cmp.Equals(e.To, to));

    /// <summary>Relación → flecha Mermaid. Sólida sin label = call (el caso común,
    /// para no ensuciar); punteada con label = resolución por convención (MediatR/DI);
    /// gruesa = herencia/implementación.</summary>
    private static (string Arrow, string? Label) MgArrow(EdgeRelation r) => r switch
    {
        EdgeRelation.Sends => ("-.->", "sends"),
        EdgeRelation.HandledBy => ("-.->", "handled-by"),
        EdgeRelation.DiBound => ("-.->", "di-bound"),
        EdgeRelation.Implements or EdgeRelation.Inherits => ("==>", null),
        EdgeRelation.Call => ("-->", null),
        _ => ("-->", r.Label()),
    };

    /// <summary>
    /// Escapa un label de flowchart entre comillas. Las entidades HTML son la forma
    /// segura en mermaid.live y GitHub; las llaves de las rutas ({id}) también, para
    /// que Mermaid no las interprete como sintaxis de nodo romboide.
    /// </summary>
    private static string EscapeMermaid(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("{", "&#123;")
        .Replace("}", "&#125;");

    /// <summary>Escapado mínimo para sequenceDiagram (texto plano tras ':' — no hay
    /// comillas ni labels HTML salvo &lt;br/&gt; que sí es válido).</summary>
    private static string EscapeSequence(string s)
        => s.Replace("\r", " ").Replace("\n", " ").Replace(";", ",");
}
