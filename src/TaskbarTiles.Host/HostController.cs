using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetTiles(IReadOnlyList<TileRuntime> runtimes);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display; one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar) : IAsyncDisposable
{
    const double DefaultWidth = 132;

    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    void Sync()
    {
        var cfg = config();
        var displays = taskbars.Current;
        var layouts = displays.ToDictionary(d => d.DisplayId,
            d => DisplayResolver.VisibleTileIds(DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary)));

        SyncRuntimes(cfg, layouts.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            _bars[gone].Close();
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            if (!_bars.TryGetValue(info.DisplayId, out var bar))
                _bars[info.DisplayId] = bar = createBar(info.DisplayId);
            bar.SetTiles(layouts[info.DisplayId].Select(id => _runtimes[id].Runtime).ToList());
            bar.Apply(info);
        }
    }

    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        foreach (var (id, entry) in _runtimes.ToList())
        {
            var keep = wanted.Contains(id) && cfg.Tiles.TryGetValue(id, out var now) && SameConfig(now, entry.Config);
            if (keep) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var id in wanted)
        {
            if (_runtimes.ContainsKey(id) || !cfg.Tiles.TryGetValue(id, out var tileCfg)) continue;

            ITileConnector? connector = null;
            try { connector = catalog.Create(tileCfg.Connector); }
            catch (Exception ex) { logFor(id).Error($"Connector '{tileCfg.Connector}' failed to construct", ex); }
            connector ??= new UnknownConnector(tileCfg.Connector);

            var width = catalog.Connectors.TryGetValue(tileCfg.Connector, out var d) ? d.Width : DefaultWidth;
            var runtime = new TileRuntime(id, tileCfg.Connector, width, connector, tileCfg.Settings, ui, logFor(id));
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    public async ValueTask DisposeAsync()
    {
        foreach (var bar in _bars.Values) bar.Close();
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }
}
