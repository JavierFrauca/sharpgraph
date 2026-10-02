using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpGraph.Graph;

namespace SharpGraph.Persistence;

/// <summary>
/// Caché en disco de los fragmentos del grafo, por solución. Permite arranque
/// en frío instantáneo: al reabrir el proyecto se cargan los fragmentos y solo
/// se re-parsean los ficheros cambiados.
///
/// Formato v2 (.sgcache): binario con tabla de strings deduplicada
/// (<see cref="FragmentBinary"/>), ~5-10× menor y de carga casi instantánea
/// frente al JSON de la v1. Las cachés .json legacy se leen para migrar y el
/// siguiente save las reemplaza por binarias.
/// </summary>
public sealed class GraphStore
{
    /// <summary>
    /// Versión del extractor. Súbela cuando cambie la lógica de parsing/modelo:
    /// invalida cachés viejas aunque el contenido de los ficheros no haya cambiado.
    /// v7: FileFragment gana ReturnSignatures, PendingCallSites, PendingLocals (Fase B).
    /// </summary>
    private const int ParserVersion = 7;

    /// <summary>
    /// Tope de cachés de soluciones distintas conservadas en disco: al superarlo
    /// se borran las más antiguas (LRU por LastWriteTime). La caché de Payroll-grade
    /// ronda las decenas de MB; sin eviction el directorio solo crece.
    /// </summary>
    private const int MaxCacheFiles = 10;

    /// <summary>
    /// Un .tmp solo es válido durante los milisegundos entre write y move; si
    /// sobrevive este umbral es de un proceso muerto y se borra al arrancar.
    /// </summary>
    private static readonly TimeSpan TmpMaxAge = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private sealed record Envelope(int Version, List<FileFragment> Fragments);

    private readonly string _cacheDir;

    public GraphStore() : this(null) { }

    /// <summary>Cache dir inyectable para tests; null = %LOCALAPPDATA%\SharpGraph\cache.</summary>
    public GraphStore(string? cacheDir)
    {
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpGraph", "cache");
        Directory.CreateDirectory(_cacheDir);
        CleanupOrphanedTmp();
    }

    private string CacheFileFor(string scanPath)
        => Path.Combine(_cacheDir, CacheKey(scanPath) + ".sgcache");

    private string LegacyJsonFileFor(string scanPath)
        => Path.Combine(_cacheDir, CacheKey(scanPath) + ".json");

    private static string CacheKey(string scanPath)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(scanPath).ToLowerInvariant())));

    public bool TryLoad(string scanPath, out List<FileFragment> fragments)
    {
        fragments = [];
        var sw = Stopwatch.StartNew();
        try
        {
            var file = CacheFileFor(scanPath);
            if (File.Exists(file))
            {
                fragments = FragmentBinary.Read(File.ReadAllBytes(file), ParserVersion);
                sw.Stop();
                var readMs = sw.ElapsedMilliseconds;
                // descartar fragmentos de ficheros que ya no existen: UN walk del
                // árbol en vez de un File.Exists por fichero (stat × N es orden de
                // magnitud más lento, sobre todo con rutas de nombre corto 8.3)
                sw.Restart();
                fragments = FilterExisting(scanPath, fragments);
                sw.Stop();
                if (fragments.Count == 0) return false;
                Console.Error.WriteLine(
                    $"Cache loaded (binary): {fragments.Count} fragments in {readMs}ms read + {sw.ElapsedMilliseconds}ms stat ({new FileInfo(file).Length / 1024.0 / 1024.0:F1} MB)");
                return true;
            }

            // migración: caché v1 JSON del mismo ParserVersion — se lee y el
            // próximo Save la reemplaza por binaria.
            var legacy = LegacyJsonFileFor(scanPath);
            if (File.Exists(legacy))
            {
                var env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(legacy), JsonOpts);
                if (env is null || env.Version != ParserVersion || env.Fragments is null) return false;
                fragments = env.Fragments.Where(f => File.Exists(f.FilePath)).ToList();
                if (fragments.Count == 0) return false;
                sw.Stop();
                Console.Error.WriteLine(
                    $"Cache loaded (legacy json): {fragments.Count} fragments in {sw.ElapsedMilliseconds}ms — se migrará a binario en el próximo save");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Caché corrupta o ilegible: se descarta y se re-escanea desde cero.
            // No es un error fatal; el grafo se reconstruye del código fuente.
            Console.Error.WriteLine($"Cache load failed: {ex.Message}");
            return false;
        }
    }

    public void Save(string scanPath, IReadOnlyCollection<FileFragment> fragments)
    {
        // defender cachés buenas de saves accidentales con el grafo vacío
        // (p.ej. un load fallido que propagó una lista vacía): no persistir 0.
        if (fragments.Count == 0) return;
        try
        {
            var sw = Stopwatch.StartNew();
            var file = CacheFileFor(scanPath);
            // Escritura atómica: a un temporal único y luego move encima del destino.
            // Dos saves solapados (watcher + scan) no corrompen la caché ni fallan
            // por compartir el fichero destino: simplemente gana el último move.
            var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(tmp, FragmentBinary.Write(fragments, ParserVersion));
            File.Move(tmp, file, overwrite: true);
            sw.Stop();
            Console.Error.WriteLine(
                $"Cache saved (binary): {fragments.Count} fragments in {sw.ElapsedMilliseconds}ms ({new FileInfo(file).Length / 1024.0 / 1024.0:F1} MB)");

            // la caché legacy json ya no aporta: fuera tras un save binario correcto
            var legacy = LegacyJsonFileFor(scanPath);
            if (File.Exists(legacy)) TryDelete(legacy);

            EvictOldCaches(file);
        }
        catch (Exception ex)
        {
            // la caché es best-effort: si falla, simplemente se re-escanea
            Console.Error.WriteLine($"Cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Borra los .tmp huérfanos que deja un write atómico interrumpido (proceso
    /// muerto a mitad de save). Umbral de antigüedad para no tocar el .tmp de
    /// otro proceso SharpGraph vivo que esté salvando justo ahora.
    /// </summary>
    private void CleanupOrphanedTmp()
    {
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(_cacheDir, "*.tmp"))
                if (File.GetLastWriteTimeUtc(tmp) < DateTime.UtcNow - TmpMaxAge)
                    TryDelete(tmp);
        }
        catch
        {
            // el cleanup nunca debe tumbar el arranque
        }
    }

    /// <summary>
    /// Filtra los fragmentos cuyo fichero sigue existiendo. Un solo enumerate
    /// recursivo del root (con IgnoreInaccessible) alimenta un HashSet y la
    /// comprobación es O(1); los fragmentos fuera del root (raro: sln con
    /// proyectos externos) caen a File.Exists individual.
    /// </summary>
    private static List<FileFragment> FilterExisting(string scanPath, List<FileFragment> fragments)
    {
        try
        {
            var rootFull = Path.GetFullPath(File.Exists(scanPath)
                ? Path.GetDirectoryName(scanPath)!
                : scanPath);
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            var onDisk = new HashSet<string>(
                Directory.EnumerateFiles(rootFull, "*.cs", opts),
                StringComparer.OrdinalIgnoreCase);

            static bool UnderRoot(string path, string root)
            {
                var full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && (full.Length == root.Length
                        || full[root.Length] == Path.DirectorySeparatorChar
                        || full[root.Length] == Path.AltDirectorySeparatorChar);
            }

            return fragments
                .Where(f => UnderRoot(f.FilePath, rootFull)
                    ? onDisk.Contains(Path.GetFullPath(f.FilePath))
                    : File.Exists(f.FilePath))
                .ToList();
        }
        catch
        {
            // si el walk falla (root desaparecido, permisos), fallback al stat por fichero
            return fragments.Where(f => File.Exists(f.FilePath)).ToList();
        }
    }

    /// <summary>LRU simple: conserva como máximo <see cref="MaxCacheFiles"/> cachés.</summary>
    private void EvictOldCaches(string justSaved)
    {
        try
        {
            var caches = Directory.EnumerateFiles(_cacheDir)
                .Where(f => f.EndsWith(".sgcache", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var old in caches.Skip(MaxCacheFiles))
            {
                Console.Error.WriteLine($"Cache evicted (LRU): {old.Name} ({old.Length / 1024.0 / 1024.0:F1} MB)");
                TryDelete(old.FullName);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* best-effort */ }
    }
}
