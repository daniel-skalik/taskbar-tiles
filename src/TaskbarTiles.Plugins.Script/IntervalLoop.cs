namespace TaskbarTiles.Plugins.Script;

/// <summary>
/// Runs a job at once and then every period. A tick (or a manual Trigger) that finds the previous run
/// still going is skipped, never queued. Runs execute on the thread pool; their exceptions go to onError.
/// </summary>
public sealed class IntervalLoop(
    TimeSpan every,
    Func<CancellationToken, Task> run,
    Action<Exception> onError,
    Action? onSkipped = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((period, ct) => Task.Delay(period, ct));
    int _running;
    int _skipped;
    Task? _current;

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    /// <summary>The run started last (possibly finished already), so a caller can wait for it to wind down.</summary>
    public Task? CurrentRun => Volatile.Read(ref _current);

    public int Skipped => Volatile.Read(ref _skipped);

    /// <summary>Starts a run unless one is going. Returns false when this trigger was skipped.</summary>
    public bool Trigger(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skipped);
            try { onSkipped?.Invoke(); }
            catch { /* a failing notification must not break the trigger; there is no logger here */ }
            return false;
        }
        Volatile.Write(ref _current, RunOnceAsync(ct));
        return true;
    }

    /// <summary>Until cancelled: trigger, then wait one period.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Trigger(ct);
                await _delay(every, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(() => run(ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try { onError(ex); }
            catch { /* nothing left to report to */ }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
