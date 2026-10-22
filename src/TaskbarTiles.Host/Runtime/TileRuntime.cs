using System.Collections.Concurrent;
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

public enum FlyoutOutcome
{
    /// <summary>The connector answered: it has no popup (the tile click should become a "click" action).</summary>
    NoPopup,
    /// <summary>A popup (or an error description) is available in <see cref="FlyoutResult.Flyout"/>.</summary>
    Popup,
    /// <summary>The call timed out, was cancelled or the runtime is disposed; do nothing.</summary>
    Unavailable,
}

public readonly record struct FlyoutResult(FlyoutOutcome Outcome, FlyoutState? Flyout = null)
{
    public static FlyoutResult NoPopup { get; } = new(FlyoutOutcome.NoPopup);
    public static FlyoutResult Unavailable { get; } = new(FlyoutOutcome.Unavailable);
    public static FlyoutResult Of(FlyoutState popup) => new(FlyoutOutcome.Popup, popup);
}

/// <summary>
/// What is wrong with a tile. Fatal errors (the connector threw) restart the connector with backoff;
/// reported errors (ITileContext.ReportError) only change the look until the next successful update.
/// </summary>
public sealed record TileError(string Message, string Details, bool Fatal)
{
    public static TileError From(Exception ex) => new(ex.Message, ex.ToString(), Fatal: true);
}

public sealed record TileRuntimeOptions
{
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan NotRespondingFor { get; init; } = TimeSpan.FromSeconds(3);
    public BackoffSchedule Backoff { get; init; } = BackoffSchedule.Default;

    /// <summary>Handed to connectors as ITileContext.ConfigDirectory.</summary>
    public string ConfigDirectory { get; init; } = "";
}

/// <summary>
/// One tile's connector and its latest state. Connector calls run on the thread pool with a timeout;
/// state changes are applied and announced on the UI thread. A connector that throws is replaced by a
/// fresh instance from the factory after a backoff delay.
/// </summary>
public sealed class TileRuntime : IAsyncDisposable
{
    public const string RestartActionId = "host:restart";
    public const string ShowLogActionId = "host:show-log";

    public static TileState Initial { get; } = new() { Title = "…" };

    static readonly TileState NotResponding = new()
    {
        Title = "Not responding",
        Icon = new TileIcon("clock", TileColor.Warn),
    };

    static readonly TileState Restarting = new()
    {
        Title = "Restarting…",
        Icon = new TileIcon("sync"),
    };

    /// <summary>One connector instance and everything tied to its life.</summary>
    sealed class Instance(int generation, ITileConnector connector, TileContext context, CancellationTokenSource cts)
    {
        public int Generation { get; } = generation;
        public ITileConnector Connector { get; } = connector;
        public TileContext Context { get; } = context;
        public CancellationTokenSource Cts { get; } = cts;
        /// <summary>Captured up front: Cts.Token throws once the source is disposed.</summary>
        public CancellationToken Token { get; } = cts.Token;
    }

    /// <summary>The next state to show. Success resets the backoff; a fatal Error schedules a restart.</summary>
    sealed record Pending(int Generation, TileState State, TileError? Error, bool Success);

    readonly Func<ITileConnector> _createConnector;
    readonly JsonElement _settings;
    readonly IUiDispatcher _ui;
    readonly ITileLog _log;
    readonly TileRuntimeOptions _options;
    readonly CancellationTokenSource _lifetime = new();
    readonly object _instanceLock = new();
    readonly ConcurrentDictionary<string, byte> _actionsInFlight = new(StringComparer.Ordinal);

    Instance? _current;   // null while (re)starting or after a failed construction
    int _generation;      // written under _instanceLock; bumped on every start and restart
    Pending? _pending;
    int _postQueued;
    int _disposed;
    int _notRespondingGeneration;
    int _failures;        // consecutive restarts scheduled without a success; UI thread only
    int _restartScheduledFor; // generation an automatic restart is already pending for (0 = none); UI thread only
    TileState _beforeNotResponding = Initial;

    public TileRuntime(string tileId, string connectorId, double width, Func<ITileConnector> createConnector,
        JsonElement settings, IUiDispatcher ui, ITileLog log, TileRuntimeOptions? options = null)
    {
        TileId = tileId;
        ConnectorId = connectorId;
        Width = width;
        _createConnector = createConnector;
        _settings = settings;
        _ui = ui;
        _log = log;
        _options = options ?? new TileRuntimeOptions();
    }

    public string TileId { get; }
    public string ConnectorId { get; }
    public double Width { get; }

    /// <summary>Read on the UI thread.</summary>
    public TileState State { get; private set; } = Initial;

    /// <summary>Read on the UI thread. Set while the tile shows the error look.</summary>
    public TileError? LastError { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Creates the connector and starts it. Call once; RestartAsync replaces it later.</summary>
    public Task StartAsync()
    {
        int generation;
        lock (_instanceLock) generation = ++_generation;
        return LaunchAsync(generation);
    }

    /// <summary>"Restart tile": replaces the connector now and resets the backoff count.</summary>
    public Task RestartAsync()
    {
        if (IsDisposed) return Task.CompletedTask;
        _ui.Post(() => _failures = 0); // relies on the dispatcher being FIFO: runs before the new generation's first update
        return RestartCoreAsync(expectedGeneration: null);
    }

    /// <summary>Automatic restarts pass the generation they were scheduled for and bail out if it has moved on.</summary>
    async Task RestartCoreAsync(int? expectedGeneration)
    {
        Instance? old;
        int generation;
        lock (_instanceLock)
        {
            if (IsDisposed || (expectedGeneration is { } expected && expected != _generation)) return;
            old = _current;
            _current = null;
            generation = ++_generation;
        }
        _log.Info($"Restarting tile '{TileId}' ({ConnectorId})");
        Post(new Pending(generation, Restarting, null, Success: false));
        if (old is not null) await DisposeInstanceAsync(old).ConfigureAwait(false);
        await LaunchAsync(generation).ConfigureAwait(false);
    }

    async Task LaunchAsync(int generation)
    {
        Instance instance;
        try
        {
            // The connector's constructor is plugin code: keep it off the caller (UI) thread.
            var connector = await Task.Run(_createConnector).ConfigureAwait(false);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var context = new TileContext(this, generation, _settings, _log, _options.ConfigDirectory);
            instance = new Instance(generation, connector, context, cts);
        }
        catch (Exception ex)
        {
            Fail(ex, generation);
            return;
        }

        bool stale;
        lock (_instanceLock)
        {
            stale = IsDisposed || generation != _generation;
            if (!stale) _current = instance;
        }
        if (stale)
        {
            await DisposeInstanceAsync(instance).ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.Run(() => instance.Connector.StartAsync(instance.Context, instance.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex, generation);
        }
    }

    public async Task InvokeActionAsync(string actionId)
    {
        if (IsDisposed || Volatile.Read(ref _current) is not { } instance) return;
        // Repeated clicks on the same button must not pile up connector calls.
        if (!_actionsInFlight.TryAdd(actionId, 0)) return;
        try
        {
            await RunWithTimeout(instance, async ct => { await instance.Connector.OnActionAsync(actionId, ct); return true; });
        }
        catch (TimeoutException) { ShowNotResponding(); }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex, instance.Generation); }
        finally { _actionsInFlight.TryRemove(actionId, out _); }
    }

    public async Task<FlyoutResult> GetFlyoutAsync()
    {
        if (IsDisposed) return FlyoutResult.Unavailable;
        if (LastError is { } error) return FlyoutResult.Of(ErrorFlyout(error));
        if (Volatile.Read(ref _current) is not { } instance) return FlyoutResult.Unavailable;
        try
        {
            var flyout = await RunWithTimeout(instance, ct => instance.Connector.GetFlyoutAsync(ct));
            return flyout is null ? FlyoutResult.NoPopup : FlyoutResult.Of(flyout);
        }
        catch (TimeoutException) { ShowNotResponding(); return FlyoutResult.Unavailable; }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { return FlyoutResult.Unavailable; }
        catch (Exception ex)
        {
            if (IsDisposed) return FlyoutResult.Unavailable;
            Fail(ex, instance.Generation);
            return FlyoutResult.Of(ErrorFlyout(TileError.From(ex)));
        }
    }

    /// <summary>Plugins never run on the UI thread, so handlers are raised on the thread pool.</summary>
    public void RaiseResumed()
    {
        if (IsDisposed || Volatile.Read(ref _current) is not { } instance) return;
        _ = Task.Run(() => instance.Context.RaiseResumed(ex =>
            _log.Warn($"Tile '{TileId}' ({ConnectorId}) failed in a Resumed handler: {ex.Message}")));
    }

    /// <summary>Called by TileContext.Update from any thread.</summary>
    internal void Enqueue(int generation, TileState state) =>
        Post(new Pending(generation, Normalize(state), null, Success: true));

    /// <summary>Called by TileContext.ReportError from any thread.</summary>
    internal void Report(int generation, string? message, string? details)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "The connector reported an error." : message.Trim();
        Post(new Pending(generation, ErrorState(text), new TileError(text, details ?? "", Fatal: false), Success: false));
    }

    /// <summary>Plugin output is untrusted: make a malformed state renderable instead of letting it blank the bar.</summary>
    internal static TileState Normalize(TileState? state)
    {
        if (state is null) return Initial;

        static string Glyph(string? g) => string.IsNullOrEmpty(g) ? "info" : g;

        var buttons = (state.Buttons ?? [])
            .Where(b => b is not null)
            .Select(b => string.IsNullOrEmpty(b.Glyph) ? b with { Glyph = Glyph(b.Glyph) } : b)
            .ToList();
        var icon = state.Icon is { } i && string.IsNullOrEmpty(i.Glyph) ? i with { Glyph = Glyph(i.Glyph) } : state.Icon;
        double? progress = state.Progress is { } p && double.IsFinite(p) ? p : null;
        List<double>? spark = null;
        if (state.Sparkline is { } raw)
        {
            var finite = raw.Where(double.IsFinite).ToList();
            if (finite.Count >= 2 && double.IsFinite(finite.Max() - finite.Min())) spark = finite;
        }

        return state with { Buttons = buttons, Icon = icon, Progress = progress, Sparkline = spark };
    }

    /// <summary>Latest wins: only the newest pending state of the current generation is applied.</summary>
    void Post(Pending pending)
    {
        if (IsDisposed || pending.Generation != Volatile.Read(ref _generation)) return;
        // Never let a late writer of an older generation replace a newer entry.
        while (true)
        {
            var current = Volatile.Read(ref _pending);
            if (current is not null && current.Generation > pending.Generation) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pending, pending, current), current)) break;
        }
        if (Interlocked.Exchange(ref _postQueued, 1) != 0) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _postQueued, 0);
            var latest = Interlocked.Exchange(ref _pending, null);
            if (latest is null || IsDisposed || latest.Generation != Volatile.Read(ref _generation)) return;
            Apply(latest);
        });
    }

    // UI thread.
    void Apply(Pending pending)
    {
        LastError = pending.Error;
        if (pending.Success) _failures = 0;
        if (pending.Error is { Fatal: true }) ScheduleRestart(pending.Generation);
        SetState(pending.State);
    }

    // UI thread.
    void ScheduleRestart(int generation)
    {
        // More fatals from the instance that already has a restart pending must not advance the backoff.
        if (_restartScheduledFor == generation) return;
        _restartScheduledFor = generation;
        var delay = _options.Backoff.Delay(_failures++);
        _log.Info($"Tile '{TileId}' ({ConnectorId}) restarts in {delay.TotalSeconds:0.##} s");
        _ = Task.Delay(delay, _lifetime.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || IsDisposed || generation != Volatile.Read(ref _generation)) return;
            _ = RestartCoreAsync(generation);
        }, TaskScheduler.Default);
    }

    async Task<T> RunWithTimeout<T>(Instance instance, Func<CancellationToken, Task<T>> call)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(instance.Token);
        var cancelOffThread = false;
        try
        {
            return await Task.Run(() => call(cts.Token)).WaitAsync(_options.CallTimeout);
        }
        catch (TimeoutException)
        {
            // Cancel callbacks are plugin code and run inline, so never on the awaiting (UI) thread.
            // The task also owns disposing the source, after the cancel has run.
            cancelOffThread = true;
            _ = Task.Run(() =>
            {
                try { cts.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                finally { cts.Dispose(); }
            });
            throw;
        }
        finally
        {
            if (!cancelOffThread) cts.Dispose();
        }
    }

    void Fail(Exception ex, int generation)
    {
        if (IsDisposed) return;
        _log.Error($"Tile '{TileId}' ({ConnectorId}) failed", ex);
        Post(new Pending(generation, ErrorState(ex.Message), TileError.From(ex), Success: false));
    }

    void ShowNotResponding()
    {
        if (IsDisposed) return;
        _log.Warn($"Tile '{TileId}' ({ConnectorId}) did not answer within {_options.CallTimeout.TotalSeconds:0} s");
        var generation = Interlocked.Increment(ref _notRespondingGeneration);
        _ui.Post(() =>
        {
            if (IsDisposed) return;
            // Remember what was shown before (error state included); overlapping timeouts keep the first.
            if (!ReferenceEquals(State, NotResponding)) _beforeNotResponding = State;
            SetState(NotResponding);
        });
        _ = Task.Delay(_options.NotRespondingFor).ContinueWith(_ => _ui.Post(() =>
        {
            if (IsDisposed) return;
            if (generation == Volatile.Read(ref _notRespondingGeneration) && ReferenceEquals(State, NotResponding))
                SetState(_beforeNotResponding);
        }), TaskScheduler.Default);
    }

    void SetState(TileState state)
    {
        State = state;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error($"Tile '{TileId}' ({ConnectorId}) has a failing StateChanged subscriber", ex);
        }
    }

    static TileState ErrorState(string message) => new()
    {
        Title = "Error",
        Subtitle = message,
        Icon = new TileIcon("warning", TileColor.Warn),
        Tooltip = message,
    };

    static FlyoutState ErrorFlyout(TileError error)
    {
        var text = string.IsNullOrWhiteSpace(error.Details) ? error.Message : error.Details;
        var rows = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .Select(line => new FlyoutRow(line))
            .ToList();
        return new FlyoutState("Tile error", error.Message, rows,
            [new FlyoutAction(RestartActionId, "Restart tile", Primary: true), new FlyoutAction(ShowLogActionId, "Open log")]);
    }

    async Task DisposeInstanceAsync(Instance instance)
    {
        try
        {
            // Cancellation callbacks and disposal are plugin code: keep them off the caller (UI) thread.
            await Task.Run(async () =>
            {
                try { instance.Cts.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                try { await instance.Connector.DisposeAsync().ConfigureAwait(false); }
                finally { instance.Cts.Dispose(); } // after the cancel; in-flight calls hold Token, not the source
            }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Instance? instance;
        lock (_instanceLock)
        {
            instance = _current;
            _current = null;
        }
        try
        {
            // Cancellation callbacks and the connector's disposal are plugin code: keep them off the caller (UI) thread.
            await Task.Run(async () =>
            {
                try { _lifetime.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                if (instance is not null) await instance.Connector.DisposeAsync().ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
        // _lifetime is deliberately not disposed: calls may still be in flight and read its token.
    }
}
