using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpGraph.Graph;

namespace SharpGraph.Persistence;

/// <summary>
/// Caché en disco de los fragmentos del grafo, por solución. Permite arranque
/// en frío instantáneo: al reabrir el proyecto se cargan los fragmentos y solo
/// se re-parsean los ficheros cambiados. Sin dependencias externas (JSON).
/// </summary>
public sealed class GraphStore
{
    /// <summary>
    /// Versión del extractor. Súbela cuando cambie la lógica de parsing/modelo:
    /// invalida cachés viejas aunque el contenido de los ficheros no haya cambiado.
    /// v7: FileFragment gana ReturnSignatures, PendingCallSites, PendingLocals (Fase B).
    /// </summary>
    private const int ParserVersion = 7;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private sealed record Envelope(int Version, List<FileFragment> Fragments);

    private readonly string _cacheDir;

    public GraphStore()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpGraph", "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    private string CacheFileFor(string scanPath)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(scanPath).ToLowerInvariant())));
        return Path.Combine(_cacheDir, key + ".json");
    }

    public bool TryLoad(string scanPath, out List<FileFragment> fragments)
    {
        fragments = [];
        var file = CacheFileFor(scanPath);
        if (!File.Exists(file)) return false;
        try
        {
            var json = File.ReadAllText(file);
            var env = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (env is null || env.Version != ParserVersion || env.Fragments is null) return false;
            // descartar fragmentos de ficheros que ya no existen
            fragments = env.Fragments.Where(f => File.Exists(f.FilePath)).ToList();
            return fragments.Count > 0;
        }
        catch (Exception ex)
        {
            // Caché corrupta o ilegible: se descarta y se re-escanea desde cero.
            // No es un error fatal; el grafo se reconstruye del código fuente.
            Console.Error.WriteLine($"Cache load failed ({Path.GetFileName(file)}): {ex.Message}");
            return false;
        }
    }

    public void Save(string scanPath, IReadOnlyCollection<FileFragment> fragments)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Envelope(ParserVersion, fragments.ToList()), JsonOpts);
            File.WriteAllText(CacheFileFor(scanPath), json);
        }
        catch (Exception ex)
        {
            // la caché es best-effort: si falla, simplemente se re-escanea
            Console.Error.WriteLine($"Cache save failed: {ex.Message}");
        }
    }
}
