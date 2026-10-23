using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetSlots(IReadOnlyList<BarSlot> slots);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>Layout changes a bar asks for. HostController implements it. UI thread only.</summary>
public interface ILayoutEditor
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the layout the display shows and saves config.json under the display's
    /// edit key (<see cref="DisplayResolver.EditKey"/>). Returns null when saved or when nothing changed, otherwise why
    /// nothing was saved.
    /// </summary>
    ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit);

    /// <summary>What edit mode's "+" offers on this display.</summary>
    IReadOnlyList<GalleryItem> GalleryFor(string displayId);

    /// <summary>Places the gallery item at the end of the display's layout, adding its tile definition when it is new.</summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item);
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar,
    string? configDirectory = null,
    IConfigWriter? writer = null,
    IReadOnlyList<ScriptTemplate>? templates = null) : ILayoutEditor, IAsyncDisposable
{
    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

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
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    public ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit)
    {
        var (display, refused) = EditTarget(displayId);
        if (display is null) return refused;
        var cfg = config();
        var layout = DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        var changed = edit(layout);
        if (LayoutEdits.SameSlots(layout, changed)) return null;

        var key = DisplayResolver.EditKey(cfg, displayId, display.IsPrimary);
        return Saved(displayId, key, writer!.Save(root => ConfigJson.SetDisplay(root, key, changed)));
    }

    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
    {
        var cfg = config();
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        var layout = display is null ? DisplayLayout.Empty : DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        return TileGallery.Build(cfg, layout, catalog.Connectors.Keys, templates ?? ScriptTemplates.Samples);
    }

    public ConfigError? AddFromGallery(string displayId, GalleryItem item)
    {
        if (item.NewTile is not { } definition) return EditLayout(displayId, l => LayoutEdits.Append(l, item.TileId));

        var (display, refused) = EditTarget(displayId);
        if (display is null) return refused;
        var cfg = config();
        var layout = DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        var key = DisplayResolver.EditKey(cfg, displayId, display.IsPrimary);
        return Saved(displayId, key, writer!.Save(root =>
        {
            // Pick the id from the file being saved, so a tile added by hand but not reloaded yet is never replaced.
            var taken = root["tiles"] is System.Text.Json.Nodes.JsonObject tiles ? tiles.Select(t => t.Key) : [];
            var id = TileGallery.UniqueId(item.TileId, taken);
            ConfigJson.SetTile(root, id, definition);
            ConfigJson.SetDisplay(root, key, LayoutEdits.Append(layout, id));
        }));
    }

    /// <summary>The display to edit, or (null, why not).</summary>
    (TaskbarInfo? Display, ConfigError? Refused) EditTarget(string displayId)
    {
        if (_disposed) return (null, new ConfigError("Taskbar Tiles is shutting down."));
        if (writer is null) return (null, new ConfigError("This host cannot save config.json."));
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        return display is null ? (null, new ConfigError($"Display '{displayId}' is no longer connected.")) : (display, null);
    }

    ConfigError? Saved(string displayId, string key, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved the layout of display '{displayId}' under \"{key}\"");
        ui.Post(Sync); // not inline: the request usually comes from a handler of a view this rebuilds
        return null;
    }

    void Sync()
    {
        if (_disposed) return;
        var cfg = config();
        var displays = taskbars.Current.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        var layouts = displays.ToDictionary(d => d.DisplayId, d => DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary));

        SyncRuntimes(cfg, layouts.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            try { _bars[gone].Close(); }
            catch (Exception ex) { logFor("host").Error($"Closing bar for display '{gone}' failed", ex); }
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            try
            {
                if (!_bars.TryGetValue(info.DisplayId, out var bar))
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                bar.SetSlots(BarSlots(layouts[info.DisplayId]));
                bar.Apply(info);
            }
            catch (Exception ex)
            {
                logFor("host").Error($"Updating bar for display '{info.DisplayId}' failed", ex);
            }
        }
    }

    /// <summary>Config slots as the bar shows them; an empty stack shows nothing but keeps its index.</summary>
    IReadOnlyList<BarSlot> BarSlots(DisplayLayout layout)
    {
        var slots = new List<BarSlot>();
        for (var i = 0; i < layout.Slots.Count; i++)
        {
            switch (layout.Slots[i])
            {
                case TileSlot t when _runtimes.TryGetValue(t.TileId, out var entry):
                    slots.Add(new BarSlot(i, [entry.Runtime], t.Pin, null));
                    break;
                case StackSlot s when s.TileIds.Count > 0 && s.TileIds.All(_runtimes.ContainsKey):
                    slots.Add(new BarSlot(i, s.TileIds.Select(id => _runtimes[id].Runtime).ToList(), false, s.Rotate));
                    break;
            }
        }
        return slots;
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

            var connectorId = tileCfg.Connector;
            // The runtime calls this on the thread pool (plugin constructors are plugin code) and again for every restart.
            // A throwing constructor shows the error look and is retried with backoff.
            Func<ITileConnector> create = () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId);
            var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
            var runtime = new TileRuntime(id, connectorId, width, create, tileCfg.Settings, ui, logFor($"tile-{id}"), _runtimeOptions);
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    /// <summary>
    /// Bars (WPF windows) are closed synchronously before the first await, so call this on the UI thread.
    /// Everything after the first await is off-thread safe (ConfigureAwait(false)).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var bar in _bars.Values)
        {
            try { bar.Close(); }
            catch (Exception ex) { logFor("host").Error("Closing a bar failed", ex); }
        }
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
