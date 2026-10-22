using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public sealed class CollectingLog : ITileLog
{
    public List<string> Messages { get; } = [];
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Messages) Messages.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message)); }
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

    public Task StartAsync(ITileContext ctx, CancellationToken ct) { Context = ctx; return OnStart(ctx, ct); }
    public Task OnActionAsync(string actionId, CancellationToken ct) { Actions.Add(actionId); return OnAction(actionId, ct); }
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => OnFlyout(ct);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
