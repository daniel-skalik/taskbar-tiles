using System.IO;
using TaskbarTiles.Contracts;

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
    public IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> Tiles { get; private set; } = [];
    public TaskbarTiles.Host.Taskbar.TaskbarInfo? Info { get; private set; }
    public bool Closed { get; private set; }
    public void SetTiles(IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> runtimes) => Tiles = runtimes;
    public void Apply(TaskbarTiles.Host.Taskbar.TaskbarInfo info) => Info = info;
    public void Close() => Closed = true;
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
