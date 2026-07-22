using System.Text;

namespace SharpGraph.Graph;

public sealed partial class GraphEngine
{
    /// <summary>
    /// Mejora 5: leer un fichero .cs entero del proyecto escaneado, numerado,
    /// truncado a maxLines. Compite con GraphEngine `explore` en el caso "muéstrame
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
                catch (Exception ex)
                {
                    return $"Could not read source file for '{typeName}': {ex.Message}";
                }
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
}
