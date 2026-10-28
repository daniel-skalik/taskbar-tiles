using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public sealed class CollectingLog : ITileLog
{
    public List<string> Messages { get; } = [];
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Messages) Messages.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message)); }
    public string[] Snapshot() { lock (Messages) return Messages.ToArray(); }
}

public static class Eventually
{
    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task Holds(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Condition not met within {timeoutMs} ms.");
            await Task.Delay(20);
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public sealed class ImmediateDispatcher : TaskbarTiles.Host.Runtime.IUiDispatcher
{
    public void Post(Action action) => action();
}

public sealed class ManualDispatcher : TaskbarTiles.Host.Runtime.IUiDispatcher
{
    readonly Queue<Action> _queue = new();
    public int Posted { get; private set; }
    public void Post(Action action) { lock (_queue) { _queue.Enqueue(action); Posted++; } }
    public void Pump()
    {
        while (true)
        {
            Action next;
            lock (_queue) { if (_queue.Count == 0) return; next = _queue.Dequeue(); }
            next();
        }
    }
}

public sealed class FakeConnector : ITileConnector
{
    public ITileContext? Context { get; private set; }
    public Func<ITileContext, CancellationToken, Task> OnStart { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task> OnAction { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<FlyoutState?>> OnFlyout { get; set; } = _ => Task.FromResult<FlyoutState?>(null);
    public List<string> Actions { get; } = [];
    public bool Disposed { get; private set; }
    public Action OnDispose { get; set; } = () => { };

    public Task StartAsync(ITileContext ctx, CancellationToken ct) { Context = ctx; return OnStart(ctx, ct); }
    public Task OnActionAsync(string actionId, CancellationToken ct) { Actions.Add(actionId); return OnAction(actionId, ct); }
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => OnFlyout(ct);
    public ValueTask DisposeAsync() { OnDispose(); Disposed = true; return ValueTask.CompletedTask; }
}

public sealed class FakeTaskbarSource : TaskbarTiles.Host.Taskbar.ITaskbarSource
{
    public IReadOnlyList<TaskbarTiles.Host.Taskbar.TaskbarInfo> Current { get; set; } = [];
    public event EventHandler? Changed;
    public void Start() { }
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }

    public static TaskbarTiles.Host.Taskbar.TaskbarInfo Display(string id, bool primary) => new(
        id, primary, new(0, 0, 1000, 48), new(new(8, 0, 500, 48), true), Hidden: false, IsLightTheme: false, DpiScale: 1);
}

public sealed class FakeBar(string displayId) : TaskbarTiles.Host.ITileBarView
{
    public string DisplayId { get; } = displayId;
    public IReadOnlyList<TaskbarTiles.Host.Layout.BarSlot> Slots { get; private set; } = [];
    /// <summary>Every member of every slot, in order.</summary>
    public IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> Tiles => Slots.SelectMany(s => s.Members).ToList();
    public TaskbarTiles.Host.Taskbar.TaskbarInfo? Info { get; private set; }
    public bool Closed { get; private set; }
    public bool Editing { get; private set; }
    public int EditingCalls { get; private set; }
    public bool ThrowOnEditing { get; set; }
    public void SetEditing(bool editing)
    {
        EditingCalls++;
        if (ThrowOnEditing) throw new InvalidOperationException("bar failed");
        Editing = editing;
    }
    public void SetSlots(IReadOnlyList<TaskbarTiles.Host.Layout.BarSlot> slots) => Slots = slots;
    public void Apply(TaskbarTiles.Host.Taskbar.TaskbarInfo info) => Info = info;
    public void Close() => Closed = true;
}

/// <summary>An in-memory config.json: Save edits the JSON text the way ConfigStore does, without a file.</summary>
public sealed class FakeConfigWriter(string json) : TaskbarTiles.Host.Config.IConfigWriter
{
    public string Json { get; private set; } = json;
    public int Saves { get; private set; }
    public TaskbarTiles.Host.Config.HostConfig Current => TaskbarTiles.Host.Config.ConfigParser.Parse(Json).Config!;

    public TaskbarTiles.Host.Config.ConfigError? Save(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var root = TaskbarTiles.Host.Config.ConfigJson.ParseRoot(Json);
        edit(root);
        var text = TaskbarTiles.Host.Config.ConfigJson.Serialize(root);
        var parsed = TaskbarTiles.Host.Config.ConfigParser.Parse(text);
        if (!parsed.Success) return parsed.Error;
        Json = text;
        Saves++;
        return null;
    }
}

public static class ScriptFiles
{
    /// <summary>Writes a UTF-8 (no BOM) script under dir and returns its path relative to dir.</summary>
    public static string Write(TempDir dir, string relativePath, string body)
    {
        var full = System.IO.Path.Combine(dir.Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body, new System.Text.UTF8Encoding(false));
        return relativePath;
    }
}

public sealed class TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null, string? tileDirectory = null) : ITileContext
{
    readonly List<TileState> _updates = [];
    readonly List<(string Message, string Details)> _errors = [];

    public string TileId => tileId;
    public System.Text.Json.JsonElement Settings { get; } = System.Text.Json.JsonDocument.Parse(settingsJson).RootElement.Clone();
    public string ConfigDirectory { get; } = configDirectory ?? System.IO.Path.GetTempPath();
    public string TileDirectory => tileDirectory ?? ConfigDirectory;
    public CollectingLog Logged { get; } = new();
    public ITileLog Log => Logged;
    public event EventHandler? Resumed;

    public IReadOnlyList<TileState> Updates { get { lock (_updates) return _updates.ToList(); } }
    public IReadOnlyList<(string Message, string Details)> Errors { get { lock (_errors) return _errors.ToList(); } }

    public void Update(TileState state) { lock (_updates) _updates.Add(state); }
    public void ReportError(string message, string? details = null) { lock (_errors) _errors.Add((message, details ?? "")); }
    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Kind tables for tests, under a fake C:\tiles folder.</summary>
public static class TestKinds
{
    public const string TilesDir = @"C:\tiles";

    public static KindTable Of(params TileKind[] kinds) => new(TilesDir,
        kinds.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static TileKind Script(string id, string settingsJson = "{}", string stamp = "1", string? name = null, string description = "") =>
        new(id, System.IO.Path.Combine(TilesDir, id), name ?? id, description, TileKind.ScriptConnectorId, Json(settingsJson),
            KindDlls.None, null, stamp, "");

    /// <summary>A kind whose DLL declares connector <paramref name="id"/> as <paramref name="connectorType"/>.</summary>
    public static TileKind Plugin(string id, Type connectorType, double width = 100, string settingsJson = "{}") =>
        new(id, System.IO.Path.Combine(TilesDir, id), id, "", id, Json(settingsJson),
            new KindDlls(new Dictionary<string, ConnectorDescriptor>
            {
                [id] = new(id, connectorType, width, System.IO.Path.Combine(TilesDir, id, id + ".dll")),
            }, null, ""),
            null, "1", "");

    public static TileKind Broken(string id, string error) =>
        new(id, System.IO.Path.Combine(TilesDir, id), id, error, "", Json("{}"), KindDlls.None, error, "1", "");

    static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
}
