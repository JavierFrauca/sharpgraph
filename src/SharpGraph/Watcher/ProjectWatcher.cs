using System.Collections.Concurrent;
using SharpGraph.Graph;
using SharpGraph.Persistence;
using SharpGraph.Scanner;

namespace SharpGraph.Watcher;

/// <summary>
/// Observa los .cs bajo la raíz escaneada y actualiza el grafo en caliente,
/// re-parseando solo el fichero cambiado (con debounce). Mantiene la caché al día.
/// </summary>
public sealed class ProjectWatcher(CodeGraph graph, GraphStore store) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private string? _scanPath;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounce;
    private readonly Lock _gate = new();

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
    }

    private void OnChanged(object _, FileSystemEventArgs e) => Enqueue(e.FullPath);
    private void OnRenamed(object _, RenamedEventArgs e) { Enqueue(e.OldFullPath); Enqueue(e.FullPath); }

    private void Enqueue(string path)
    {
        if (IsExcluded(path)) return;
        _pending[path] = 0;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Flush(), null, 400, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        var paths = _pending.Keys.ToList();
        foreach (var p in paths) _pending.TryRemove(p, out _);
        if (paths.Count == 0) return;

        var scanner = new SolutionScanner(graph);
        foreach (var p in paths)
        {
            try { scanner.RescanFile(p); } catch { /* ignore */ }
        }

        if (_scanPath is not null)
            store.Save(_scanPath, graph.Fragments());

        Console.Error.WriteLine($"[watch] updated {paths.Count} file(s). {graph.Stats()}");
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
        lock (_gate) { _debounce?.Dispose(); _debounce = null; }
    }

    public void Dispose() => Stop();
}
