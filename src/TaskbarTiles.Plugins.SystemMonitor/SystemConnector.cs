using System.Diagnostics;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.SystemMonitor;

/// <summary>
/// CPU, memory, GPU, network and disk space, read once a second. Settings: "show" (which metrics, default all),
/// "layout" ("All at once" puts them on two lines, "Rotate" shows one at a time with its chart) and "rotate" (how long
/// each stays in the Rotate layout, 2s to 1m, default 5s).
/// </summary>
[TileConnector("system", Width = 200)]
public sealed class SystemConnector : ITileConnector
{
    readonly Func<ITileLog, ISystemSampler> _createSampler;
    readonly TimeSpan _period;
    readonly CancellationTokenSource _stop = new();
    readonly CancellationToken _stopping;
    readonly SystemHistory _history = new();
    volatile bool _disposed;
    ITileContext? _ctx;
    ISystemSampler? _sampler;
    IReadOnlyList<Metric> _shown = Enum.GetValues<Metric>();
    bool _rotating;
    TimeSpan _rotateEvery;
    long _startedAt;
    volatile SystemReading? _last;
    DateTime _lastAt;
    Task? _loop;

    public SystemConnector() : this(log => new WindowsSampler(log), TimeSpan.FromSeconds(1)) { }

    public SystemConnector(Func<ITileLog, ISystemSampler> createSampler, TimeSpan period)
    {
        _createSampler = createSampler;
        _period = period;
        _stopping = _stop.Token;
    }

    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _shown = SystemView.Shown(ctx.Settings, ctx.Log);
        _rotating = SystemView.IsRotating(ctx.Settings, ctx.Log);
        _rotateEvery = SystemView.RotateInterval(ctx.Settings, ctx.Log);
        _sampler = _createSampler(ctx.Log);
        _startedAt = Stopwatch.GetTimestamp();
        _loop = LoopAsync(_stopping);
        return Task.CompletedTask;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // The sampler measures from its creation, so wait a moment for the first reading to mean something.
            await Task.Delay(_period / 2, ct);
            Sample();
            using var timer = new PeriodicTimer(_period);
            while (await timer.WaitForNextTickAsync(ct)) Sample();
        }
        catch (OperationCanceledException) { }
    }

    void Sample()
    {
        var ctx = _ctx;
        var sampler = _sampler;
        if (ctx is null || sampler is null || _disposed) return;
        try
        {
            var reading = sampler.Read();
            _history.Push(reading);
            _last = reading;
            _lastAt = DateTime.Now;
            var elapsed = Stopwatch.GetElapsedTime(_startedAt);
            ctx.Update(SystemView.Tile(reading, _history, _shown, _rotating, SystemView.Rotated(_shown, elapsed, _rotateEvery)));
        }
        catch (Exception ex)
        {
            // One bad second must not stop the monitor; say so and try again on the next tick.
            ctx.Log.Warn($"System readings failed: {ex.Message}");
        }
    }

    public Task OnActionAsync(string actionId, CancellationToken ct)
    {
        if (actionId == SystemView.TaskManagerAction)
        {
            try { using var _ = Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { _ctx?.Log.Warn($"Could not open Task Manager: {ex.Message}"); }
        }
        return Task.CompletedTask;
    }

    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) =>
        Task.FromResult<FlyoutState?>(SystemView.Flyout(_last, _lastAt));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        _sampler?.Dispose();
        _stop.Dispose();
    }
}
