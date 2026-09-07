namespace SharpGraph.Docs;

/// <summary>
/// Tokenización de texto compartida por el BM25 de tipos (<c>search_semantic</c>)
/// y el índice de documentación (<c>search_docs</c>): parte camelCase/PascalCase
/// y normaliza a minúsculas. Sin stopwords: el IDF del BM25 absorbe el ruido.
/// </summary>
internal static class Tokenizer
{
    public static IEnumerable<string> Tokenize(string text)
    {
        var cleaned = new System.Text.StringBuilder(text.Length + 16);
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

    public static Dictionary<string, int> TermFrequencies(string text)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(text)) map[token] = map.GetValueOrDefault(token, 0) + 1;
        return map;
    }
}
