using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>
/// One tile's connector and its latest state. Connector calls run on the thread pool with a timeout;
/// state changes are applied and announced on the UI thread.
/// </summary>
public sealed class TileRuntime : IAsyncDisposable
{
    public static TileState Initial { get; } = new() { Title = "…" };

    static readonly TileState NotResponding = new()
    {
        Title = "Not responding",
        Icon = new TileIcon("clock", TileColor.Warn),
    };

    readonly ITileConnector _connector;
    readonly IUiDispatcher _ui;
    readonly ITileLog _log;
    readonly TileContext _context;
    readonly TimeSpan _callTimeout;
    readonly TimeSpan _notRespondingFor;
    readonly CancellationTokenSource _lifetime = new();

    TileState? _pending;
    int _postQueued;
    TileState _lastGood = Initial;

    public TileRuntime(string tileId, string connectorId, double width, ITileConnector connector, JsonElement settings,
        IUiDispatcher ui, ITileLog log, TimeSpan? callTimeout = null, TimeSpan? notRespondingFor = null)
    {
        TileId = tileId;
        ConnectorId = connectorId;
        Width = width;
        _connector = connector;
        _ui = ui;
        _log = log;
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(10);
        _notRespondingFor = notRespondingFor ?? TimeSpan.FromSeconds(3);
        _context = new TileContext(this, settings, log);
    }

    public string TileId { get; }
    public string ConnectorId { get; }
    public double Width { get; }

    /// <summary>Read on the UI thread.</summary>
    public TileState State { get; private set; } = Initial;

    public Exception? LastError { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    public async Task StartAsync()
    {
        try
        {
            await Task.Run(() => _connector.StartAsync(_context, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task InvokeActionAsync(string actionId)
    {
        try
        {
            await RunWithTimeout(async ct => { await _connector.OnActionAsync(actionId, ct); return true; });
        }
        catch (TimeoutException) { ShowNotResponding(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task<FlyoutState?> GetFlyoutAsync()
    {
        if (LastError is { } error) return ErrorFlyout(error);
        try
        {
            return await RunWithTimeout(ct => _connector.GetFlyoutAsync(ct));
        }
        catch (TimeoutException) { ShowNotResponding(); return null; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
        catch (Exception ex) { Fail(ex); return ErrorFlyout(ex); }
    }

    public void RaiseResumed() => _context.RaiseResumed();

    /// <summary>Called by TileContext from any thread. Only the newest pending state is applied.</summary>
    internal void Enqueue(TileState state)
    {
        Volatile.Write(ref _pending, state);
        if (Interlocked.Exchange(ref _postQueued, 1) != 0) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _postQueued, 0);
            var latest = Interlocked.Exchange(ref _pending, null);
            if (latest is null) return;
            LastError = null;
            _lastGood = latest;
            SetState(latest);
        });
    }

    async Task<T> RunWithTimeout<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            return await Task.Run(() => call(cts.Token)).WaitAsync(_callTimeout);
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            throw;
        }
    }

    void Fail(Exception ex)
    {
        _log.Error($"Tile '{TileId}' ({ConnectorId}) failed", ex);
        _ui.Post(() =>
        {
            LastError = ex;
            SetState(new TileState
            {
                Title = "Error",
                Subtitle = ex.Message,
                Icon = new TileIcon("warning", TileColor.Warn),
                Tooltip = ex.Message,
            });
        });
    }

    void ShowNotResponding()
    {
        _log.Warn($"Tile '{TileId}' ({ConnectorId}) did not answer within {_callTimeout.TotalSeconds:0} s");
        _ui.Post(() => SetState(NotResponding));
        _ = Task.Delay(_notRespondingFor).ContinueWith(_ => _ui.Post(() =>
        {
            if (ReferenceEquals(State, NotResponding)) SetState(_lastGood);
        }), TaskScheduler.Default);
    }

    void SetState(TileState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    static FlyoutState ErrorFlyout(Exception ex) => new(
        "Tile error",
        ex.GetType().Name,
        ex.ToString().Split(Environment.NewLine).Take(8).Select(line => new FlyoutRow(line.Trim())).ToList(),
        []);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _connector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
        _lifetime.Dispose();
    }
}
