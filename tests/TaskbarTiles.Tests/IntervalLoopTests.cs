using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class IntervalLoopTests
{
    static readonly Action<Exception> Ignore = _ => { };

    [Fact]
    public async Task Trigger_while_a_run_is_going_is_skipped()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), async _ => { Interlocked.Increment(ref runs); await gate.Task; }, Ignore);

        Assert.True(loop.Trigger(CancellationToken.None));
        Assert.False(loop.Trigger(CancellationToken.None));
        Assert.Equal(1, loop.Skipped);

        gate.SetResult();
        await Eventually.Holds(() => !loop.IsRunning);
        Assert.True(loop.Trigger(CancellationToken.None));
        await Eventually.Holds(() => Volatile.Read(ref runs) == 2);
    }

    [Fact]
    public async Task Loop_runs_at_once_and_every_period_skipping_overlaps()
    {
        using var cts = new CancellationTokenSource();
        var delays = 0;
        var started = 0;
        var skippedCallbacks = 0;
        var loop = new IntervalLoop(TimeSpan.FromSeconds(30),
            async ct => { Interlocked.Increment(ref started); await Task.Delay(Timeout.Infinite, ct); },
            Ignore,
            onSkipped: () => Interlocked.Increment(ref skippedCallbacks),
            delay: (period, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(30), period);
                if (++delays == 3) cts.Cancel();
                return Task.CompletedTask;
            });

        await loop.RunAsync(cts.Token);

        Assert.Equal(3, delays);
        Assert.Equal(2, loop.Skipped);
        Assert.Equal(2, Volatile.Read(ref skippedCallbacks));
        await Eventually.Holds(() => Volatile.Read(ref started) == 1);
    }

    [Fact]
    public async Task A_failing_run_is_reported_and_does_not_block_the_next()
    {
        var errors = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), _ => throw new InvalidOperationException("boom"),
            _ => Interlocked.Increment(ref errors));

        loop.Trigger(CancellationToken.None);
        await Eventually.Holds(() => Volatile.Read(ref errors) == 1 && !loop.IsRunning);

        Assert.True(loop.Trigger(CancellationToken.None));
        await Eventually.Holds(() => Volatile.Read(ref errors) == 2);
    }

    [Fact]
    public async Task Cancelled_run_is_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var errors = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), ct => Task.Delay(Timeout.Infinite, ct), _ => Interlocked.Increment(ref errors));

        loop.Trigger(cts.Token);
        cts.Cancel();

        await Eventually.Holds(() => !loop.IsRunning);
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task Throwing_callbacks_neither_escape_nor_kill_the_loop()
    {
        var gate = new TaskCompletionSource();
        var loop = new IntervalLoop(TimeSpan.FromHours(1), async _ => { await gate.Task; throw new InvalidOperationException("run"); },
            _ => throw new InvalidOperationException("onError"),
            onSkipped: () => throw new InvalidOperationException("onSkipped"));

        Assert.True(loop.Trigger(CancellationToken.None));
        Assert.False(loop.Trigger(CancellationToken.None));
        Assert.Equal(1, loop.Skipped);

        gate.SetResult();
        await Eventually.Holds(() => !loop.IsRunning);
        Assert.True(loop.Trigger(CancellationToken.None));
    }
}
