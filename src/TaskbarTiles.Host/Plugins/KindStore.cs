using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// Owns the kind table: loads it once (DLLs included) and scans tiles\ again whenever something under it changes
/// (debounced). A rescan never loads a DLL; see <see cref="KindLoader.Rescan"/>.
/// </summary>
public sealed class KindStore(string tilesDir, ITileLog log) : IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    readonly KindLoader _loader = new(log);
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    volatile bool _disposed;

    public KindTable Current { get; private set; } = KindTable.Empty;

    /// <summary>Raised on a thread-pool thread when a rescan changed the table: a kind's tile.json, scripts or DLLs, or the set of folders.</summary>
    public event EventHandler<KindTable>? Changed;

    public void Load()
    {
        lock (_gate) Current = _loader.Load(tilesDir);
    }

    /// <summary>Watches tiles\ and its subfolders, creating tiles\ first so kinds added later are seen.</summary>
    public void StartWatching()
    {
        Directory.CreateDirectory(tilesDir);
        _debounce = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(tilesDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => Kick();
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => Kick();
        _watcher.Error += (_, e) =>
        {
            log.Warn($"Watching {tilesDir} failed: {e.GetException().Message}; rescanning");
            Kick();
        };
        _watcher.EnableRaisingEvents = true;
    }

    // Watcher events run on thread-pool threads; one in flight while Dispose runs must not throw there.
    void Kick()
    {
        try { _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Scans now and raises Changed when the table differs. Runs on the debounce timer; public for tests. Never throws.</summary>
    public void Rescan()
    {
        KindTable table;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                table = _loader.Rescan(Current);
                if (Same(Current, table)) return;
                Current = table;
            }
            catch (Exception ex)
            {
                log.Error($"Rescanning {tilesDir} failed", ex);
                return;
            }
        }
        if (_disposed) return;
        log.Info($"Tile kinds changed: {string.Join(", ", table.Kinds.Keys)}");
        try { Changed?.Invoke(this, table); }
        catch (Exception ex) { log.Error("A tile kinds Changed handler failed", ex); }
    }

    static bool Same(KindTable a, KindTable b) =>
        a.Kinds.Count == b.Kinds.Count
        && a.FoldersWithoutTileJson.SetEquals(b.FoldersWithoutTileJson)
        && a.Kinds.All(kv => b.Kinds.TryGetValue(kv.Key, out var other)
                             && other.Stamp == kv.Value.Stamp && other.DllStamp == kv.Value.DllStamp);

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
