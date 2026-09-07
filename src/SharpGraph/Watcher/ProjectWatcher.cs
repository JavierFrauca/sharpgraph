using System.Collections.Concurrent;
using SharpGraph.Docs;
using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Scanner;

namespace SharpGraph.Watcher;

/// <summary>
/// Observa los .cs y la documentación (.md/.txt/appsettings) bajo la raíz escaneada
/// y actualiza el grafo en caliente, re-parseando solo los ficheros cambiados (con
/// debounce). Mantiene la caché al día.
/// </summary>
public sealed class ProjectWatcher(GraphEngine graph, GraphStore store) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _docWatcher;
    private string? _scanPath;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingDocs = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounce;
    private readonly Lock _gate = new();
    private Timer? _saveTimer;
    private long _lastSave;
    private int _flushing;

    /// <summary>
    /// Cooldown entre guardados de caché: la caché de una solución grande son decenas
    /// de MB de JSON y reescribirla en cada flush satura el disco y colisiona con
    /// otros saves en curso.
    /// </summary>
    private const long SaveCooldownMs = 10_000;

    public void Watch(string scanPath)
    {
        Stop();
        var root = File.Exists(scanPath) ? Path.GetDirectoryName(scanPath)! : scanPath;
        if (!Directory.Exists(root)) return;

        _scanPath = scanPath;
        _watcher = new FileSystemWatcher(root, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;

        // Documentación: FileSystemWatcher solo admite un filtro por instancia, así
        // que un segundo watcher con "*" y enrutado por extensión (los .cs los cubre
        // el primero y se ignoran aquí).
        _docWatcher = new FileSystemWatcher(root, "*")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _docWatcher.Changed += OnDocChanged;
        _docWatcher.Created += OnDocChanged;
        _docWatcher.Deleted += OnDocChanged;
        _docWatcher.Renamed += OnDocRenamed;
        _docWatcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object _, FileSystemEventArgs e) => Enqueue(e.FullPath);
    private void OnRenamed(object _, RenamedEventArgs e) { Enqueue(e.OldFullPath); Enqueue(e.FullPath); }

    private void OnDocChanged(object _, FileSystemEventArgs e)
    {
        if (IsCsFile(e.FullPath)) return;
        if (!DocIndex.IsDocFile(e.FullPath)) return;
        EnqueueDoc(e.FullPath);
    }

    private void OnDocRenamed(object _, RenamedEventArgs e)
    {
        if (IsCsFile(e.OldFullPath)) Enqueue(e.OldFullPath); // renombrado fuera de .cs: fuera del grafo
        else EnqueueDoc(e.OldFullPath);
        if (!IsCsFile(e.FullPath) && DocIndex.IsDocFile(e.FullPath)) EnqueueDoc(e.FullPath);
    }

    private static bool IsCsFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private void Enqueue(string path)
    {
        if (IsExcluded(path)) return;
        _pending[path] = 0;
        RearmDebounce();
    }

    private void EnqueueDoc(string path)
    {
        if (IsExcluded(path)) return;
        _pendingDocs[path] = 0;
        RearmDebounce();
    }

    private void RearmDebounce()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Flush(), null, 400, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        // Guarda anti-solape: nunca dos pipelines de re-indexado+guardado a la vez.
        // Si el flush en curso no terminó, este callback re-arma el debounce para que
        // los pendientes se procesen justo cuando acabe (sin perder eventos).
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
        {
            lock (_gate)
            {
                _debounce?.Dispose();
                _debounce = new Timer(_ => Flush(), null, 400, Timeout.Infinite);
            }
            return;
        }
        try
        {
            var paths = _pending.Keys.ToList();
            foreach (var p in paths) _pending.TryRemove(p, out _);
            var docPaths = _pendingDocs.Keys.ToList();
            foreach (var p in docPaths) _pendingDocs.TryRemove(p, out _);
            if (paths.Count == 0 && docPaths.Count == 0) return;

            // Código: un SOLO merge para todo el lote; la fusión incremental decide
            // entre delta (ediciones de cuerpo) o rebuild único (cambio estructural).
            if (paths.Count > 0)
            {
                var fragments = new SolutionScanner(graph).RescanFiles(paths);
                if (fragments.Count > 0)
                    graph.MergeFragments(fragments);
                Console.Error.WriteLine(
                    $"[watch] updated {paths.Count} file(s) ({(graph.LastMergeIncremental ? "incremental" : "rebuild")}).");
            }

            // Docs: reindexado directo (con símbolos frescos para las menciones).
            // No toca la caché de fragmentos: los docs no viven en ella.
            if (docPaths.Count > 0)
            {
                graph.Docs.RescanFiles(docPaths, graph.SimpleTypeNames());
                Console.Error.WriteLine($"[watch] updated {docPaths.Count} doc(s).");
            }

            if (paths.Count > 0)
                SaveThrottled();
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <summary>
    /// Guarda la caché con throttle: como máximo un save cada <see cref="SaveCooldownMs"/>;
    /// si llega antes, se aplaza con un timer (uno pendiente como máximo). Los saves ya
    /// no corren en cada guardado de fichero, y el write atómico de GraphStore evita
    /// que dos saves solapados (watcher + scan) se pisen.
    /// </summary>
    private void SaveThrottled()
    {
        if (_scanPath is null) return;
        lock (_gate)
        {
            var wait = SaveCooldownMs - (Environment.TickCount64 - _lastSave);
            if (wait <= 0)
            {
                DoSave();
                return;
            }
            if (_saveTimer is null)
                _saveTimer = new Timer(
                    _ =>
                    {
                        lock (_gate)
                        {
                            _saveTimer?.Dispose();
                            _saveTimer = null;
                        }
                        DoSave();
                    },
                    null, wait, Timeout.Infinite);
        }
    }

    private void DoSave()
    {
        lock (_gate) _lastSave = Environment.TickCount64;
        store.Save(_scanPath!, graph.Fragments());
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return parts.Any(p => p is "obj" or "bin" or ".git" or "node_modules" or ".vs");
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_docWatcher is not null)
        {
            _docWatcher.EnableRaisingEvents = false;
            _docWatcher.Dispose();
            _docWatcher = null;
        }
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    public void Dispose() => Stop();
}
