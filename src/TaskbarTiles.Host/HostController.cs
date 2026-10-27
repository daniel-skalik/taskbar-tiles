using System.IO;
using System.Text.Json;
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

    /// <summary>Edit mode: dashed tiles, drag, ×, + and Done.</summary>
    void SetEditing(bool editing);
    void Close();
}

/// <summary>Layout changes a bar asks for. HostController implements it. UI thread only.</summary>
public interface ILayoutEditor
{
    /// <summary>Edit mode is on for every bar at once; each bar still edits only its own display's layout.</summary>
    bool Editing { get; }

    void SetEditMode(bool editing);

    /// <summary>
    /// Applies <paramref name="edit"/> to the layout the display shows and saves config.json under the display's
    /// edit key (<see cref="DisplayResolver.EditKey"/>). Returns null when saved or when nothing changed, otherwise why
    /// nothing was saved.
    /// </summary>
    ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit);

    /// <summary>What edit mode's "+" offers on this display.</summary>
    IReadOnlyList<GalleryItem> GalleryFor(string displayId);

    /// <summary>
    /// Places the gallery item at the end of the display's layout, adding an instance of its kind when it is new. With
    /// <paramref name="stackWith"/> it joins that slot instead, as "Create stack…" does (<see cref="LayoutEdits.StackInto"/>).
    /// </summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item, int? stackWith = null);

    /// <summary>
    /// × and "Remove tile": removes slot <paramref name="index"/>, or with <paramref name="member"/> one member of a stack,
    /// from the display's layout. A tile no display entry (including "*") places afterwards loses its definition under
    /// "tiles" in the same save; other unplaced definitions are kept.
    /// </summary>
    ConfigError? RemoveTile(string displayId, int index, int? member);

    /// <summary>
    /// Drag and drop between displays' bars. Slot <paramref name="fromIndex"/> of the source display lands before, into or
    /// after slot <paramref name="target"/> of the target display, or at its end when target is null; tiles the target
    /// display already places only leave the source. One save writes both displays' edit keys. On the same display this
    /// is <see cref="LayoutEdits.Drop"/> (a null target changes nothing).
    /// </summary>
    ConfigError? MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position);
}

/// <summary>
/// Keeps runtimes and bars in sync with config, the tile kinds and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// <paramref name="catalog"/> holds the connectors built into the app (script); <paramref name="kinds"/> the kind table.
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
    Func<KindTable>? kinds = null) : ILayoutEditor, IAsyncDisposable
{
    /// <summary>How one tile runs; a runtime is kept while its Fingerprint is unchanged.</summary>
    sealed record TilePlan(string ConnectorId, double Width, Func<ITileConnector> Create, JsonElement Settings, string Directory, string Fingerprint);

    readonly Dictionary<string, (TileRuntime Runtime, string Fingerprint)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public bool Editing { get; private set; }

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded or the kind table changed.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    /// <summary>
    /// Turns edit mode on or off on every bar, so a display without tiles (whose bar is otherwise hidden) can get some.
    /// </summary>
    public void SetEditMode(bool editing)
    {
        if (_disposed || Editing == editing) return;
        Editing = editing;
        foreach (var (id, bar) in _bars)
        {
            try { bar.SetEditing(editing); }
            catch (Exception ex) { logFor("host").Error($"Switching edit mode on display '{id}' failed", ex); }
        }
    }

    public ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit)
    {
        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        var changed = edit(t.Layout);
        if (LayoutEdits.SameSlots(t.Layout, changed)) return null;
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root => ConfigJson.SetDisplay(root, t.Key, changed)));
    }

    public ConfigError? RemoveTile(string displayId, int index, int? member)
    {
        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        var changed = LayoutEdits.Remove(t.Layout, index, member);
        if (LayoutEdits.SameSlots(t.Layout, changed)) return null;
        var removed = LayoutEdits.RemovedTileIds(t.Layout, changed);
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root =>
        {
            ConfigJson.SetDisplay(root, t.Key, changed);
            ConfigJson.RemoveUnplacedTiles(root, removed); // after the change, so this display no longer counts
        }));
    }

    public ConfigError? MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position)
    {
        if (fromDisplayId == toDisplayId)
            return EditLayout(fromDisplayId, l => target is { } to
                ? LayoutEdits.Drop(l, fromIndex, to, position)
                : LayoutEdits.Drop(l, fromIndex, l.Slots.Count - 1, DropPosition.After)); // no target: to the end

        var (source, refused) = Editable(fromDisplayId);
        if (source is not { } s) return refused;
        var (destination, refusedTarget) = Editable(toDisplayId);
        if (destination is not { } d) return refusedTarget;
        if (s.Layout.Slots.ElementAtOrDefault(fromIndex) is not { } slot) return null; // a stale index: nothing to move

        if (s.Key == d.Key) return new ConfigError($"Displays '{fromDisplayId}' and '{toDisplayId}' are both saved under \"{s.Key}\"; a move between them cannot be saved.");

        var newSource = LayoutEdits.Remove(s.Layout, fromIndex);
        var newTarget = LayoutEdits.DropIncoming(d.Layout, slot, target, position);
        var targetChanged = !LayoutEdits.SameSlots(d.Layout, newTarget); // else a display inheriting "*" would be forked into its own entry
        return Saved($"a move from display '{fromDisplayId}' (\"{s.Key}\") to display '{toDisplayId}' (\"{d.Key}\")",
            writer!.Save(root =>
            {
                ConfigJson.SetDisplay(root, s.Key, newSource);
                if (targetChanged) ConfigJson.SetDisplay(root, d.Key, newTarget);
            }));
    }

    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
    {
        var cfg = config();
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        var layout = display is null ? DisplayLayout.Empty : DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        return TileGallery.Build(cfg, layout, Kinds());
    }

    KindTable Kinds() => kinds?.Invoke() ?? KindTable.Empty;

    public ConfigError? AddFromGallery(string displayId, GalleryItem item, int? stackWith = null)
    {
        DisplayLayout Place(DisplayLayout layout, string id)
        {
            var placed = LayoutEdits.Append(layout, id);
            return stackWith is { } target ? LayoutEdits.StackInto(placed, placed.Slots.Count - 1, target) : placed;
        }

        if (item.NewTile is not { } definition) return EditLayout(displayId, l => Place(l, item.TileId));

        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root =>
        {
            // Pick the id from the file being saved, so a tile added by hand but not reloaded yet is never replaced.
            var taken = root["tiles"] is System.Text.Json.Nodes.JsonObject tiles ? tiles.Select(tile => tile.Key) : [];
            var id = TileGallery.UniqueId(item.TileId, taken);
            ConfigJson.SetTile(root, id, definition);
            ConfigJson.SetDisplay(root, t.Key, Place(t.Layout, id));
        }));
    }

    /// <summary>The layout the display shows and the key its edits are saved under, or (null, why it cannot be edited).</summary>
    ((DisplayLayout Layout, string Key)? Target, ConfigError? Refused) Editable(string displayId)
    {
        var (display, refused) = EditTarget(displayId);
        if (display is null) return (null, refused);
        var cfg = config();
        return ((DisplayResolver.Resolve(cfg, displayId, display.IsPrimary), DisplayResolver.EditKey(cfg, displayId, display.IsPrimary)), null);
    }

    static string LayoutOf(string displayId, string key) => $"the layout of display '{displayId}' under \"{key}\"";

    /// <summary>The display to edit, or (null, why not).</summary>
    (TaskbarInfo? Display, ConfigError? Refused) EditTarget(string displayId)
    {
        if (_disposed) return (null, new ConfigError("Taskbar Tiles is shutting down."));
        if (writer is null) return (null, new ConfigError("This host cannot save config.json."));
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        return display is null ? (null, new ConfigError($"Display '{displayId}' is no longer connected.")) : (display, null);
    }

    /// <summary>After a save: logs what was saved and rebuilds the bars, or returns why nothing was saved.</summary>
    ConfigError? Saved(string what, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved {what}");
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
                {
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                    if (Editing) bar.SetEditing(true);
                }
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
        var table = Kinds();
        var plans = new Dictionary<string, TilePlan>(StringComparer.Ordinal);
        foreach (var id in wanted)
            if (cfg.Tiles.TryGetValue(id, out var tile)) plans[id] = Plan(tile, table);

        foreach (var (id, entry) in _runtimes.ToList())
        {
            if (plans.TryGetValue(id, out var plan) && plan.Fingerprint == entry.Fingerprint) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var (id, plan) in plans)
        {
            if (_runtimes.ContainsKey(id)) continue;
            var runtime = new TileRuntime(id, plan.ConnectorId, plan.Width, plan.Create, plan.Settings, ui, logFor($"tile-{id}"),
                _runtimeOptions with { TileDirectory = plan.Directory });
            _runtimes[id] = (runtime, plan.Fingerprint);
            _ = runtime.StartAsync();
        }
    }

    /// <summary>
    /// The tile's kind connector with the effective settings and the kind folder, or a tile error when the kind is missing
    /// or broken. The fingerprint changes with the kind's tile.json and scripts (its stamp), so editing one kind restarts
    /// only that kind's tiles.
    /// </summary>
    TilePlan Plan(TileConfig tile, KindTable table)
    {
        if (table.Find(tile.Kind) is not { } kind)
        {
            var missing = $@"There is no tiles\{tile.Kind} folder with a tile.json.";
            return new(tile.Kind, PluginLoader.DefaultTileWidth, () => new BrokenKindConnector(missing), tile.Settings,
                Path.Combine(table.TilesDir, tile.Kind), $"missing\n{tile.Kind}\n{tile.Settings.GetRawText()}");
        }

        var settings = kind.SettingsFor(tile.Settings);
        var fingerprint = $"{kind.Directory}\n{kind.Stamp}\n{kind.Error}\n{settings.GetRawText()}";
        if (kind.Error is { } error)
            return new(kind.Connector.Length > 0 ? kind.Connector : kind.Id, PluginLoader.DefaultTileWidth,
                () => new BrokenKindConnector(error), settings, kind.Directory, fingerprint);

        // The runtime calls Create on the thread pool (plugin constructors are plugin code) and again for every restart.
        // A throwing constructor shows the error look and is retried with backoff.
        if (kind.Plugin is { } plugin)
            return new(plugin.Id, plugin.Width, () => ConnectorCatalog.Instantiate(plugin), settings, kind.Directory, fingerprint);

        var connectorId = kind.Connector; // "script": built into the app
        var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
        return new(connectorId, width, () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId), settings,
            kind.Directory, fingerprint);
    }

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
