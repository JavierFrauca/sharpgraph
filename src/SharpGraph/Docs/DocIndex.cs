using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpGraph.Docs;

/// <summary>
/// Índice ligero de la documentación del proyecto: .md/.markdown/.txt y ficheros
/// de configuración (appsettings*.json, launchSettings.json). Vive en RAM junto al
/// grafo y se reconstruye en cada scan — parsear texto plano es barato, no necesita
/// caché en disco — y se actualiza en caliente desde el watcher.
///
/// Dos capacidades:
///  - <see cref="Search"/>: BM25 (mismo esquema que search_semantic) con títulos y
///    secciones ponderados x3. Descubre la doc relevante devolviendo ruta + sección,
///    NUNCA contenido: leer el fichero es trabajo del cliente.
///  - <see cref="MentionsOf"/>: intersección de los identificadores del texto con la
///    tabla de símbolos del grafo — conecta ADRs/docs con los tipos que explican.
///    Solo se cruzan nombres PascalCase/camelCase de 4+ caracteres para evitar
///    falsos positivos con palabras comunes del prose ("user", "datos").
/// </summary>
public sealed partial class DocIndex
{
    /// <summary>Ficheros mayores se ignoran: un ADR no pesa 1 MB; un log o un dump, sí.</summary>
    private const long MaxFileBytes = 1_000_000;
    private const int MaxHeadingsPerDoc = 60;
    /// <summary>Peso de título/sección en el TF: una sección que casa con la query es señal fuerte.</summary>
    private const int HeadingWeight = 3;
    private const int MaxMentionResults = 5;

    private sealed record Heading(int Level, string Text, int Line);

    private sealed record DocEntry(
        string Path,
        string Hash,
        string Title,
        List<Heading> Headings,
        Dictionary<string, int> Tf,
        int Length,
        HashSet<string> Mentions);

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DocEntry> _byPath = new(Cmp);
    /// <summary>Nombres simples de tipo aptos para mención, normalizados a minúsculas.</summary>
    private HashSet<string> _symbols = new(StringComparer.Ordinal);
    private string? _root;

    public int Count { get { lock (_lock) return _byPath.Count; } }

    public void Clear()
    {
        lock (_lock)
        {
            _byPath.Clear();
            _symbols.Clear();
            _root = null;
        }
    }

    /// <summary>
    /// Reindexa toda la documentación bajo la ruta escaneada (acepta .sln/.csproj/carpeta,
    /// igual que scan). Incremental por hash dentro de la sesión: tras un arranque en frío
    /// sin caché de docs, re-leer texto plano es cuestión de milisegundos.
    /// </summary>
    public void Rebuild(string scanPath, IReadOnlyCollection<string> simpleTypeNames)
    {
        var root = File.Exists(scanPath) ? Path.GetDirectoryName(scanPath)! : scanPath;
        if (!Directory.Exists(root)) return;

        var files = new HashSet<string>(
            DiscoverFiles(root).Select(NormalizePath), Cmp);
        lock (_lock)
        {
            _root = NormalizePath(root);
            UpdateSymbolsLocked(simpleTypeNames);

            foreach (var gone in _byPath.Keys.Where(k => !files.Contains(k)).ToList())
                _byPath.Remove(gone);
            foreach (var f in files)
                IndexIfChangedLocked(f);
        }
    }

    /// <summary>
    /// Reindexa solo los ficheros indicados (watcher). Refresca la tabla de símbolos
    /// para que los tipos añadidos desde el último scan también se detecten como menciones.
    /// </summary>
    public void RescanFiles(IEnumerable<string> paths, IReadOnlyCollection<string> simpleTypeNames)
    {
        lock (_lock)
        {
            UpdateSymbolsLocked(simpleTypeNames);
            foreach (var raw in paths)
            {
                var p = NormalizePath(raw);
                if (!File.Exists(p)) { _byPath.Remove(p); continue; }
                IndexIfChangedLocked(p);
            }
        }
    }

    private void IndexIfChangedLocked(string path)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return;
            text = File.ReadAllText(path);
        }
        catch { return; } // leído en plena escritura: el watcher volverá a disparar

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        if (_byPath.TryGetValue(path, out var old) && old.Hash == hash) return;
        _byPath[path] = ParseDocLocked(path, text, hash);
    }

    private DocEntry ParseDocLocked(string path, string text, string hash)
    {
        var headings = ExtractHeadings(path, text);

        // TF del cuerpo completo; las secciones pesan HeadingWeight (sus tokens se
        // añaden (HeadingWeight-1) veces extra).
        var tf = Tokenizer.TermFrequencies(text);
        foreach (var h in headings)
            foreach (var (term, count) in Tokenizer.TermFrequencies(h.Text))
                tf[term] = tf.GetValueOrDefault(term, 0) + count * (HeadingWeight - 1);
        var length = tf.Values.Sum();

        var title = headings.FirstOrDefault(h => h.Level == 1)?.Text
                    ?? headings.FirstOrDefault()?.Text
                    ?? Path.GetFileNameWithoutExtension(path);

        // Menciones: identificadores del texto presentes en la tabla de símbolos.
        var mentions = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WordRegex().Matches(text))
        {
            var word = m.Value;
            if (word.Length < 4) continue;
            var norm = word.ToLowerInvariant();
            if (_symbols.Contains(norm)) mentions.Add(norm);
        }

        return new DocEntry(path, hash, title, headings, tf, length, mentions);
    }

    private static List<Heading> ExtractHeadings(string path, string text)
    {
        var headings = new List<Heading>();
        var isMarkdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
        if (!isMarkdown) return headings;

        var inFence = false;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length && headings.Count < MaxHeadingsPerDoc; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            var m = HeadingRegex().Match(trimmed);
            if (m.Success)
                headings.Add(new Heading(m.Groups[1].Value.Length, m.Groups[2].Value.Trim(), i + 1));
        }
        return headings;
    }

    // ------------------------------------------------------------------ búsqueda

    public string Search(string query, int topK = 8)
    {
        lock (_lock)
        {
            topK = Math.Clamp(topK, 1, 20);
            if (_byPath.Count == 0)
                return "No hay documentación indexada (0 docs). Ejecuta scan() primero.";

            var terms = Tokenizer.Tokenize(query).Distinct(Cmp).ToList();
            if (terms.Count == 0) return "La query no contiene términos útiles.";

            var entries = _byPath.Values.ToList();
            var n = entries.Count;
            var totalLen = 0;
            foreach (var e in entries) totalLen += e.Length;
            var avgLen = totalLen == 0 ? 1d : (double)totalLen / n;
            const double k1 = 1.5, b = 0.75;

            // df por término de la query en una pasada (misma fórmula que search_semantic)
            var df = new Dictionary<string, int>(Cmp);
            foreach (var e in entries)
                foreach (var t in terms)
                    if (e.Tf.ContainsKey(t)) df[t] = df.GetValueOrDefault(t, 0) + 1;

            var scored = new List<(DocEntry Doc, double Score)>();
            foreach (var e in entries)
            {
                var score = 0d;
                foreach (var t in terms)
                {
                    if (!e.Tf.TryGetValue(t, out var tf) || tf == 0) continue;
                    var tdf = df.GetValueOrDefault(t, 0);
                    if (tdf == 0) continue;
                    var idf = Math.Log(1 + (n - tdf + 0.5) / (tdf + 0.5));
                    var denom = tf + k1 * (1 - b + b * (e.Length / avgLen));
                    score += idf * tf * (k1 + 1) / denom;
                }
                if (score > 0) scored.Add((e, score));
            }

            if (scored.Count == 0) return $"Sin resultados para '{query}' en {n} docs.";

            var top = scored.OrderByDescending(x => x.Score).Take(topK).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Documentación para '{query}' ({top.Count} de {n} docs):");
            foreach (var (doc, score) in top)
            {
                sb.AppendLine($"  {score,5:F1}  {RelativizeLocked(doc.Path)} — «{doc.Title}»");
                var relevant = doc.Headings
                    .Where(h => Tokenizer.Tokenize(h.Text).Any(terms.Contains))
                    .Take(3).ToList();
                var shown = relevant.Count > 0 ? relevant : doc.Headings.Take(2).ToList();
                if (shown.Count > 0)
                    sb.AppendLine($"        {string.Join(" · ", shown.Select(h => "§ " + h.Text))}");
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ menciones

    /// <summary>Docs que mencionan el tipo (hasta 5), con ruta relativa y título.</summary>
    public IReadOnlyList<(string Path, string Title)> MentionsOf(string simpleTypeName)
    {
        lock (_lock)
        {
            var norm = simpleTypeName.ToLowerInvariant();
            return _byPath.Values
                .Where(e => e.Mentions.Contains(norm))
                .Select(e => (RelativizeLocked(e.Path), e.Title))
                .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
                .Take(MaxMentionResults)
                .ToList();
        }
    }

    /// <summary>Número de docs que mencionan el tipo (indicador [docs:N] de search/understand).</summary>
    public int CountMentions(string simpleTypeName)
    {
        lock (_lock)
        {
            var norm = simpleTypeName.ToLowerInvariant();
            return _byPath.Values.Count(e => e.Mentions.Contains(norm));
        }
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// ¿Es un fichero de documentación indexable? Público porque el watcher lo usa
    /// para enrutar eventos. De .json solo se indexan configs conocidas: package.json
    /// y lock files son ruido, no documentación.
    /// </summary>
    public static bool IsDocFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".md" or ".markdown" or ".txt") return true;
        if (ext == ".json")
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                || name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static IEnumerable<string> DiscoverFiles(string root)
    {
        var found = new List<string>();
        // EnumerateFiles con patrón usa el filtro nativo del FS; el check final por
        // IsDocFile limpia la laxitud del patrón de 3 caracteres de Windows.
        foreach (var pattern in (string[])["*.md", "*.markdown", "*.txt", "*.json"])
        {
            try { found.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)); }
            catch (UnauthorizedAccessException) { }
        }
        return found
            .Where(f => !IsExcluded(f) && IsDocFile(f))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        // Los directorios de puntos de herramientas (`.zcode`, `.claude`, `.vscode`, …)
        // contienen borradores internos, no documentación del proyecto. `.github` sí
        // se indexa: issue templates y docs de CI son documentación.
        return parts.Any(p => p is
            "obj" or "bin" or ".git" or "node_modules" or ".vs"
            or ".vscode" or ".idea" or ".zcode" or ".claude"
            or ".cursor" or ".continue" or ".codex");
    }

    private void UpdateSymbolsLocked(IReadOnlyCollection<string> simpleTypeNames)
    {
        _symbols = new HashSet<string>(
            simpleTypeNames.Where(IsMentionable).Select(s => s.ToLowerInvariant()),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Solo nombres con mayúscula interna (PascalCase/camelCase) de 4+ caracteres:
    /// "GrossService" sí; "user"/"App"/"Db" no — aparecer en prosa normal sería
    /// casi siempre un falso positivo.
    /// </summary>
    private static bool IsMentionable(string name)
        => name.Length >= 4 && name.Skip(1).Any(char.IsUpper);

    /// <summary>
    /// Ruta relativa a la raíz escaneada con separadores '/': la salida la consumen
    /// LLMs y es estable entre plataformas.
    /// </summary>
    private string RelativizeLocked(string path)
    {
        if (_root is null) return path.Replace('\\', '/');
        try { return Path.GetRelativePath(_root, path).Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    /// <summary>
    /// Normaliza la clave de un path: GetFullPath resuelve relativos y unifica
    /// separadores mixtos ('/' y '\'), evitando entradas duplicadas cuando el
    /// llamante construye rutas de forma distinta al discovery.
    /// </summary>
    private static string NormalizePath(string path) => Path.GetFullPath(path);

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex WordRegex();
}
