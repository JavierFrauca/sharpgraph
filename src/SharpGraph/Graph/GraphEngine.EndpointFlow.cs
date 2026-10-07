using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpGraph.Graph;

/// <summary>
/// Flujo descendente desde un endpoint HTTP: la vista que consume la extensión
/// SharpGraph Flow para VS Code (y cualquier cliente programático). Dos salidas
/// JSON sobre el mismo grafo:
///
///   - list_endpoints: catálogo de endpoints HTTP (verb, route, file, line) para
///     poblar el árbol de la barra lateral.
///   - endpoint_flow: subgrafo DESCENDENTE desde un endpoint (controller →
///     command/query → handler → servicios → implementaciones DI) con nodos
///     clasificados por capa, metadatos file:line para click-to-open, back-edges
///     marcados (destino ya dibujado: no se re-expande) y el bloque Mermaid
///     equivalente para export/docs.
///
/// A diferencia de mermaid_* (pensado para texto/LLM), aquí los tipos externos
/// (ILogger, IMapper…) SÍ entran pero marcados como infra: el cliente decide si
/// los muestra. Mismas podas que el resto: tests/mocks fuera, ParamType/ReturnType
/// fuera, implements-fantasma de MediatR fuera, tope duro de nodos.
/// </summary>
public sealed partial class GraphEngine
{
    private sealed record FlowEdge(string From, string To, EdgeRelation Rel, int Line, bool Back);

    // ─────────────────────────── list_endpoints ───────────────────────────

    /// <summary>Catálogo JSON de todos los endpoints HTTP indexados, ordenado por
    /// controlador y línea. Es la fuente del árbol de endpoints de la extensión.</summary>
    public string ListEndpoints()
    {
        lock (_lock)
        {
            var arr = new JsonArray();
            foreach (var (key, ep) in EndpointCatalog())
            {
                arr.Add(new JsonObject
                {
                    ["controller"] = key,
                    ["controllerName"] = LastSegment(key),
                    ["verb"] = ep.Verb,
                    ["route"] = ep.Route,
                    ["method"] = ep.MethodName,
                    ["file"] = FileOfEndpoint(key, ep),
                    ["line"] = ep.Line,
                });
            }
            return new JsonObject { ["count"] = arr.Count, ["endpoints"] = arr }
                .ToJsonString(Indented);
        }
    }

    private List<(string Key, EndpointDef Ep)> EndpointCatalog()
        => _endpoints
            .Where(kv => !IsDiagramNoise(kv.Key))
            .SelectMany(kv => kv.Value.Select(ep => (kv.Key, ep)))
            .OrderBy(x => LastSegment(x.Key), Cmp)
            .ThenBy(x => x.ep.Line)
            .ToList();

    /// <summary>Fichero de un endpoint. Los controladores resuelven por su nodo; los
    /// minimal APIs planos viven bajo una clave sintética ("GET /users") sin tipo:
    /// se localiza el fragmento que declaró ese (verb, route, line).</summary>
    private string FileOfEndpoint(string key, EndpointDef ep)
    {
        var direct = _files.GetValueOrDefault(key);
        if (direct is not null) return direct;
        foreach (var frag in _fragments.Values)
            if (frag.Endpoints.Any(e => Cmp.Equals(e.Verb, ep.Verb)
                                     && Cmp.Equals(e.Route, ep.Route)
                                     && e.Line == ep.Line))
                return frag.FilePath;
        return "";
    }

    // ─────────────────────────── endpoint_flow ───────────────────────────

    /// <summary>
    /// Subgrafo descendente JSON desde un endpoint. <paramref name="endpoint"/>
    /// acepta "VERB /ruta" ("POST /api/orders"), "/ruta" (substring) o el nombre
    /// del controlador (todas sus rutas). Cada nodo lleva kind (endpoint,
    /// controller, command/query, handler, interface, implementation, validator,
    /// dto, class, external), file:line y el flag infra (ruido ocultable); cada
    /// arista lleva relation, línea de la primera referencia y back=true si su
    /// destino ya estaba dibujado (dependencia compartida o ciclo: no se
    /// re-expande). Con <paramref name="includeDtos"/> entran además los
    /// CONTRATOS de entrada/salida (aristas ParamType/ReturnType → nodos kind
    /// "dto", hojas): el nivel 4 del diagrama de la extensión.
    /// </summary>
    public string EndpointFlow(string endpoint, int maxDepth = 8, int maxNodes = 80, bool includeDtos = false)
    {
        lock (_lock)
        {
            maxDepth = Math.Clamp(maxDepth, 1, 12);
            maxNodes = Math.Clamp(maxNodes, 5, 200);

            var roots = ResolveEndpoints(endpoint, out var error, out var byName);
            if (error is not null || roots is null) return error ?? FlowError("Endpoint no encontrado.");

            if (_nodes.Count == 0) return FlowError("Grafo vacío: ejecuta scan() primero.");

            // MODO ENDPOINT-CONCRETO: si la entrada resolvió por verbo+ruta (no por
            // nombre de controlador), del controller SOLO se expanden las aristas de
            // SU método de acción (FromMember) — no el batiburrido de los demás
            // endpoints del mismo controller. Por nombre de controlador: todo.
            var single = !byName && roots.Count == 1;
            var rootCtrl = roots[0].Ctrl;
            var methodName = single ? roots[0].Ep.MethodName : null;

            // ── recolección BFS ──
            var labels = new Dictionary<string, string>(Cmp);   // nodos sintéticos $ep: → label legible
            var epMeta = new Dictionary<string, (string Ctrl, string File, int Line)>(Cmp);
            var nodes = new List<string>();
            var indexOf = new Dictionary<string, int>(Cmp);
            var edges = new List<FlowEdge>();
            var edgeKeys = new HashSet<string>(Cmp);
            var dtoNodes = new HashSet<string>(Cmp);
            var omitted = 0;

            bool TryAddNode(string key)
            {
                if (indexOf.ContainsKey(key)) return true;
                if (nodes.Count >= maxNodes) { omitted++; return false; }
                indexOf[key] = nodes.Count;
                nodes.Add(key);
                return true;
            }

            foreach (var (epKey, ctrl, ep) in roots)
            {
                labels[epKey] = $"{ep.Verb} {ep.Route}";
                epMeta[epKey] = (ctrl, _files.GetValueOrDefault(ctrl) ?? "", ep.Line);
                if (TryAddNode(epKey) && TryAddNode(ctrl) && edgeKeys.Add(epKey + "|" + ctrl))
                    edges.Add(new FlowEdge(epKey, ctrl, EdgeRelation.Call, ep.Line, Back: false));
            }

            var frontier = roots.Select(r => r.Ctrl).Distinct(Cmp).ToList();
            var firstLevel = true;
            var controllerEdgesTotal = 0;
            var controllerEdgesMatched = 0;
            for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var current in frontier)
                {
                    if (!_out.TryGetValue(current, out var outEdges)) continue;

                    // primer nivel en modo endpoint-concreto: solo las aristas que
                    // salen del método de acción de ESTE endpoint. Tres señales, por
                    // orden: 1) FromMember == método; 2) línea de la arista dentro del
                    // SPAN del método (wrappers que pierden FromMember); 3) fallback
                    // al expansionado completo (minimal APIs y similares)
                    if (single && firstLevel && methodName is not null && Cmp.Equals(current, rootCtrl))
                    {
                        controllerEdgesTotal = outEdges.Count;
                        var mine = outEdges
                            .Where(e => e.FromMember is not null && Cmp.Equals(e.FromMember, methodName))
                            .ToList();
                        if (mine.Count == 0)
                        {
                            var span = _members.GetValueOrDefault(rootCtrl)
                                ?.FirstOrDefault(m => Cmp.Equals(m.MemberName, methodName));
                            if (span is not null)
                                mine = outEdges
                                    .Where(e => e.Line >= span.StartLine && e.Line <= span.EndLine)
                                    .ToList();
                        }
                        controllerEdgesMatched = mine.Count;
                        if (mine.Count > 0) outEdges = mine;
                    }

                    foreach (var g in outEdges
                        .Where(e => IsStructuralRelation(e.Relation) ||
                                    (includeDtos && e.Relation is EdgeRelation.ParamType or EdgeRelation.ReturnType))
                        .GroupBy(e => e.To, Cmp))
                    {
                        var to = g.Key;
                        if (IsDiagramNoise(to)) continue;
                        var rel = g.Select(e => e.Relation).OrderByDescending(RelationRank).First();
                        var isDtoEdge = rel is EdgeRelation.ParamType or EdgeRelation.ReturnType;
                        if (IsMediatRArtifact(current, to, rel)) continue;
                        // la impl ⇒ interfaz ya dibujada por di-bound es el MISMO binding
                        // en sentido contrario (como IsMediatRArtifact, pero con DI): fuera
                        if (rel is EdgeRelation.Implements or EdgeRelation.Inherits &&
                            edges.Any(x => Cmp.Equals(x.From, to) && Cmp.Equals(x.To, current) && x.Rel == EdgeRelation.DiBound))
                            continue;
                        if (!edgeKeys.Add(current + "|" + to)) continue;

                        var isNew = !indexOf.ContainsKey(to);
                        if (isNew && !TryAddNode(to))
                        {
                            edgeKeys.Remove(current + "|" + to);
                            continue; // presupuesto agotado (ya contado en TryAddNode)
                        }
                        if (isNew && isDtoEdge) dtoNodes.Add(to);
                        var lines = g.Select(e => e.Line).Where(l => l > 0).ToList();
                        edges.Add(new FlowEdge(current, to, rel, lines.Count > 0 ? lines.Min() : 0, Back: !isNew));
                        // los externos (ILogger…) y los DTOs son hojas: no se expanden
                        if (isNew && !isDtoEdge && _nodes.ContainsKey(to)) next.Add(to);
                    }
                }
                frontier = next;
                firstLevel = false;
            }

            // ── clasificación por capa + metadatos ──
            var nodesJson = new JsonArray();
            foreach (var key in nodes)
            {
                var (kind, infra) = ClassifyNode(key, labels, dtoNodes, includeDtos);
                var isEp = epMeta.TryGetValue(key, out var meta);
                var node = _nodes.GetValueOrDefault(key);
                nodesJson.Add(new JsonObject
                {
                    ["id"] = $"N{indexOf[key]}",
                    ["kind"] = kind,
                    ["name"] = isEp ? labels[key] : Display(key),
                    ["fqn"] = isEp ? meta.Ctrl : key,
                    ["file"] = isEp ? meta.File : _files.GetValueOrDefault(key),
                    ["line"] = isEp ? meta.Line : node?.StartLine ?? 0,
                    ["infra"] = infra,
                });
            }

            var edgesJson = new JsonArray();
            var backCount = 0;
            foreach (var e in edges)
            {
                if (e.Back) backCount++;
                edgesJson.Add(new JsonObject
                {
                    ["from"] = $"N{indexOf[e.From]}",
                    ["to"] = $"N{indexOf[e.To]}",
                    ["relation"] = e.Rel.Label(),
                    ["line"] = e.Line,
                    ["back"] = e.Back,
                });
            }

            var rootId = $"N{indexOf[roots[0].EpKey]}";
            return new JsonObject
            {
                ["endpoint"] = labels[roots[0].EpKey],
                ["matchedEndpoints"] = roots.Count,
                ["mode"] = single ? "endpoint" : "controller",
                ["controllerEdges"] = new JsonObject
                {
                    ["total"] = controllerEdgesTotal,
                    ["matched"] = controllerEdgesMatched,
                },
                ["root"] = rootId,
                ["nodeCount"] = nodes.Count,
                ["edgeCount"] = edges.Count,
                ["backEdges"] = backCount,
                ["omitted"] = omitted,
                ["truncated"] = omitted > 0,
                ["nodes"] = nodesJson,
                ["edges"] = edgesJson,
                ["mermaid"] = RenderFlowMermaid(labels, indexOf, nodes, edges),
            }.ToJsonString(Indented);
        }
    }

    /// <summary>Resuelve la entrada a endpoints concretos: tipo controlador (simple o
    /// FQN, todas sus rutas — <paramref name="byName"/>=true, modo controlador
    /// completo), "VERB /ruta" o "/ruta" (exacto primero, luego substring, máx 5 —
    /// modo endpoint-concreto). Rellena error con JSON {"error": …} si no hay match.</summary>
    private List<(string EpKey, string Ctrl, EndpointDef Ep)>? ResolveEndpoints(string input, out string? error, out bool byName)
    {
        error = null;
        byName = false;
        var result = new List<(string, string, EndpointDef)>();

        var key = ResolveInput(input, out var amb);
        if (amb is not null)
        {
            error = FlowError(amb);
            return null;
        }
        if (key is not null && _endpoints.TryGetValue(key, out var byType))
        {
            byName = true;
            foreach (var ep in byType) result.Add((EpKeyOf(key, ep), key, ep));
            return result;
        }

        var (verb, route) = SplitVerbRoute(input);
        var matches = EndpointCatalog()
            .Where(x => (verb is null || Cmp.Equals(x.Ep.Verb, verb)) &&
                        (Cmp.Equals(x.Ep.Route, route) || x.Ep.Route.Contains(route, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0)
        {
            error = FlowError($"No encuentro el endpoint '{input}'. Usa list_endpoints para ver el " +
                              "catálogo (formatos válidos: \"POST /api/orders\", \"/api/orders\" u OrdersController).");
            return null;
        }
        var chosen = matches.Where(x => Cmp.Equals(x.Ep.Route, route)).ToList();
        if (chosen.Count == 0) chosen = matches.Take(5).ToList();
        foreach (var (k, ep) in chosen)
            result.Add((EpKeyOf(k, ep), k, ep));
        return result;
    }

    private static string EpKeyOf(string ctrl, EndpointDef ep)
        => $"$ep:{LastSegment(ctrl)}:{ep.Verb} {ep.Route}";

    private static (string? Verb, string Route) SplitVerbRoute(string input)
    {
        var trimmed = input.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && parts[0].Length is >= 3 and <= 6 && Cmp.Equals(parts[0], parts[0].ToUpperInvariant()))
            return (parts[0], parts[1]);
        return (null, trimmed);
    }

    private static string FlowError(string message)
        => new JsonObject { ["error"] = message }.ToJsonString(Indented);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Clasifica un nodo por capa para el render de la extensión. Los nodos
    /// $ep: son el propio endpoint HTTP; los externos son infra si son BCL/logging
    /// (el cliente los oculta con el toggle) y "external" si son NuGet de dominio.
    /// Los contratos ("dto") solo se clasifican cuando se piden (includeDtos):
    /// descubiertos solo por ParamType/ReturnType, o por nombre — los
    /// `new XxxResponse()` generan arista New que domina sobre ReturnType.</summary>
    private (string Kind, bool Infra) ClassifyNode(string key, Dictionary<string, string> labels,
        HashSet<string>? dtoNodes = null, bool includeDtos = false)
    {
        if (labels.ContainsKey(key)) return ("endpoint", false);

        if (!_nodes.TryGetValue(key, out var node))
        {
            var external = LastSegment(key);
            var bclInfra = key.StartsWith("Microsoft.", StringComparison.Ordinal)
                || key.StartsWith("System.", StringComparison.Ordinal)
                || key.StartsWith("MediatR", StringComparison.Ordinal)
                || key.StartsWith("AutoMapper", StringComparison.Ordinal)
                || key.StartsWith("Serilog", StringComparison.Ordinal)
                || external.Contains("Logger", StringComparison.OrdinalIgnoreCase)
                || Cmp.Equals(external, "IMediator") || Cmp.Equals(external, "IMapper");
            // todo externo (infra BCL o NuGet) sale marcado: el cliente lo oculta por defecto
            return (bclInfra ? "infra" : "external", true);
        }

        if (_endpoints.ContainsKey(key)) return ("controller", false);

        var simple = LastSegment(key);
        if (_out.TryGetValue(key, out var oe) && oe.Any(e => e.Relation == EdgeRelation.HandledBy))
            return (simple.EndsWith("Query", StringComparison.Ordinal) ? "query" : "command", false);
        if (HasIncomingHandledBy(key)) return ("handler", false);
        if (node.Kind == NodeKind.Interface) return ("interface", false);
        if (simple.EndsWith("Validator", StringComparison.Ordinal)) return ("validator", false);
        if (_diByImpl.ContainsKey(key)) return ("implementation", false);
        // contrato de entrada/salida SOLO cuando se piden DTOs: descubierto solo
        // por param/return, o por nombre (los `new XxxResponse()` generan arista
        // New que domina sobre ReturnType); sin el flag es un "class" más
        if (includeDtos &&
            (dtoNodes?.Contains(key) == true ||
             simple.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ||
             simple.EndsWith("Request", StringComparison.OrdinalIgnoreCase) ||
             simple.EndsWith("Response", StringComparison.OrdinalIgnoreCase) ||
             simple.EndsWith("Result", StringComparison.OrdinalIgnoreCase)))
            return ("dto", false);
        return ("class", false);
    }

    private bool HasIncomingHandledBy(string key)
    {
        if (!_in.TryGetValue(key, out var callers)) return false;
        foreach (var caller in callers)
            if (_out.TryGetValue(caller, out var oe) && oe.Any(e => e.Relation == EdgeRelation.HandledBy && Cmp.Equals(e.To, key)))
                return true;
        return false;
    }

    /// <summary>Bloque Mermaid equivalente al JSON (misma numeración N{i}), para
    /// export/docs: flechas punteadas en MediatR/DI y linkStyle rojo en back-edges.</summary>
    private string RenderFlowMermaid(Dictionary<string, string> labels, Dictionary<string, int> indexOf,
        List<string> nodes, List<FlowEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine($"%% endpoint_flow — {nodes.Count} nodos · {edges.Count} aristas");
        sb.AppendLine("flowchart TD");
        sb.AppendLine("  classDef entry fill:#d4edda,stroke:#1e7e34,color:#111;");
        sb.AppendLine("  classDef iface stroke-dasharray:4 3,color:#111;");
        sb.AppendLine("  classDef external fill:#ececec,stroke:#8a8a8a,stroke-dasharray:2 3,color:#333;");

        var backIndices = new List<int>();
        foreach (var key in nodes)
        {
            var id = $"N{indexOf[key]}";
            var label = labels.TryGetValue(key, out var ep)
                ? EscapeMermaid(ep)
                : EscapeMermaid(Display(key));
            var css = labels.ContainsKey(key) ? ":::entry"
                : !_nodes.ContainsKey(key) ? ":::external"
                : _nodes.TryGetValue(key, out var n) && n.Kind == NodeKind.Interface ? ":::iface"
                : "";
            sb.AppendLine($"  {id}[\"{label}\"]{css}");
        }
        for (var i = 0; i < edges.Count; i++)
        {
            var (arrow, lbl) = MgArrow(edges[i].Rel);
            if (edges[i].Back) backIndices.Add(i);
            sb.AppendLine(lbl is null
                ? $"  N{indexOf[edges[i].From]} {arrow} N{indexOf[edges[i].To]}"
                : $"  N{indexOf[edges[i].From]} {arrow}|{lbl}| N{indexOf[edges[i].To]}");
        }
        foreach (var i in backIndices)
            sb.AppendLine($"  linkStyle {i} stroke:#f14c4c,stroke-dasharray:4 3;");
        sb.AppendLine("```");
        return sb.ToString();
    }
}
