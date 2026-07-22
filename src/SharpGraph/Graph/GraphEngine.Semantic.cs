using System.Text;

namespace SharpGraph.Graph;

public sealed partial class GraphEngine
{
    /// <summary>PageRank (power iteration) sobre las aristas del grafo.</summary>
    private void ComputeRankLocked()
    {
        var nodes = _out.Keys.ToList();
        var n = nodes.Count;
        if (n == 0) return;

        const double d = 0.85;      // damping factor estándar de PageRank (Brin & Page, 1998)
        const int iterations = 20; // converge en ~10-15 iteraciones para grafos <1000 nodos; 20 da margen

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

    /// <summary>Hubs incluyendo tipos externos/BCL (infraestructura transversal).</summary>
    public string HubsWithExternal(int topK = 15) => Hubs(topK, includeExternal: true);

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
