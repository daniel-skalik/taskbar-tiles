using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Tests;

public class TileRuntimeRestartTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();
    static readonly BackoffSchedule Never = new([TimeSpan.FromMinutes(10)]);
    static readonly BackoffSchedule Fast = new([TimeSpan.FromMilliseconds(20)]);

    /// <summary>Hands out a new FakeConnector per call; Setup sees how many were created before.</summary>
    sealed class Factory
    {
        readonly List<FakeConnector> _created = [];
        public Action<FakeConnector, int> Setup { get; set; } = (_, _) => { };
        public int Count { get { lock (_created) return _created.Count; } }
        public FakeConnector this[int i] { get { lock (_created) return _created[i]; } }

        public ITileConnector Create()
        {
            var c = new FakeConnector();
            int before;
            lock (_created) before = _created.Count;
            Setup(c, before);
            lock (_created) _created.Add(c);
            return c;
        }
    }

    static TileRuntime Create(Func<ITileConnector> factory, IUiDispatcher? ui = null, BackoffSchedule? backoff = null,
        CollectingLog? log = null, string configDirectory = "") =>
        new("tile1", "fake", 132, factory, NoSettings, ui ?? new ImmediateDispatcher(), log ?? new CollectingLog(),
            new TileRuntimeOptions { Backoff = backoff ?? Never, ConfigDirectory = configDirectory });

    [Fact]
    public async Task Reported_error_shows_error_look_and_details_without_restart()
    {
        var f = new Factory
        {
            Setup = (c, _) => c.OnStart = (ctx, _) => { ctx.ReportError("exit 3", "line one\nline two"); return Task.CompletedTask; },
        };
        var rt = Create(f.Create, backoff: Fast);

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.Equal("exit 3", rt.State.Subtitle);
        Assert.Equal("exit 3", rt.State.Tooltip);
        Assert.Equal(TileColor.Warn, rt.State.Icon!.Color);
        Assert.False(rt.LastError!.Fatal);
        var flyout = (await rt.GetFlyoutAsync()).Flyout!;
        Assert.Equal("exit 3", flyout.Subtitle);
        Assert.Equal(new[] { "line one", "line two" }, flyout.Rows.Select(r => r.Text));
        Assert.Equal(new[] { TileRuntime.RestartActionId, TileRuntime.ShowLogActionId }, flyout.Actions.Select(a => a.Id));
        Assert.True(flyout.Actions[0].Primary);
        await Task.Delay(200);
        Assert.Equal(1, f.Count);
    }

    [Fact]
    public async Task Update_clears_a_reported_error()
    {
        var c = new FakeConnector();
        var rt = Create(() => c);
        await rt.StartAsync();
        c.Context!.ReportError("bad");

        c.Context.Update(new TileState { Title = "good" });

        Assert.Null(rt.LastError);
        Assert.Equal("good", rt.State.Title);
    }

    [Fact]
    public async Task Update_and_report_are_coalesced_latest_wins()
    {
        var c = new FakeConnector();
        var ui = new ManualDispatcher();
        var rt = Create(() => c, ui);
        await rt.StartAsync();

        c.Context!.Update(new TileState { Title = "a" });
        c.Context.ReportError("broken");
        ui.Pump();

        Assert.Equal(1, ui.Posted);
        Assert.Equal("Error", rt.State.Title);
        Assert.NotNull(rt.LastError);

        c.Context.Update(new TileState { Title = "b" });
        ui.Pump();
        Assert.Equal("b", rt.State.Title);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Fatal_error_restarts_with_a_fresh_connector()
    {
        var f = new Factory();
        f.Setup = (c, before) => c.OnStart = before == 0
            ? (_, _) => throw new InvalidOperationException("first start fails")
            : (ctx, _) => { ctx.Update(new TileState { Title = "back" }); return Task.CompletedTask; };
        var rt = Create(f.Create, backoff: Fast);

        await rt.StartAsync();

        await Eventually.Holds(() => rt.State.Title == "back");
        Assert.Equal(2, f.Count);
        Assert.True(f[0].Disposed);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Repeated_failures_follow_the_backoff_schedule()
    {
        var f = new Factory { Setup = (c, _) => c.OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var log = new CollectingLog();
        var rt = Create(f.Create, backoff: new BackoffSchedule(
            [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60), TimeSpan.FromHours(1)]), log: log);

        await rt.StartAsync();

        await Eventually.Holds(() => f.Count == 3);
        await Task.Delay(300);
        Assert.Equal(3, f.Count); // the third failure waits an hour
        Assert.Contains(log.Snapshot(), m => m.Contains("restarts in"));
        await rt.DisposeAsync();
    }

    [Fact]
    public async Task Successful_update_resets_the_backoff()
    {
        var f = new Factory();
        f.Setup = (c, before) =>
        {
            if (before == 0)
            {
                c.OnStart = (_, _) => throw new InvalidOperationException("boom");
                return;
            }
            c.OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "ok" }); return Task.CompletedTask; };
            c.OnAction = (_, _) => throw new InvalidOperationException("action boom");
        };
        var rt = Create(f.Create, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1)]));
        await rt.StartAsync();
        await Eventually.Holds(() => rt.State.Title == "ok");

        await rt.InvokeActionAsync("x"); // fails again; without the reset this would wait an hour

        await Eventually.Holds(() => f.Count == 3);
        await rt.DisposeAsync();
    }

    [Fact]
    public async Task Manual_restart_disposes_the_old_connector_and_starts_a_new_one()
    {
        var f = new Factory
        {
            Setup = (c, before) => c.OnStart = (ctx, _) => { ctx.Update(new TileState { Title = $"instance {before}" }); return Task.CompletedTask; },
        };
        var rt = Create(f.Create);
        await rt.StartAsync();
        Assert.Equal("instance 0", rt.State.Title);

        await rt.RestartAsync();

        Assert.Equal(2, f.Count);
        Assert.True(f[0].Disposed);
        Assert.Equal("instance 1", rt.State.Title);
    }

    [Fact]
    public async Task Restart_shows_restarting_until_the_new_connector_updates()
    {
        var c = new FakeConnector();
        var rt = Create(() => c);
        await rt.StartAsync();

        await rt.RestartAsync();

        Assert.Equal("Restarting…", rt.State.Title);
    }

    [Fact]
    public async Task Late_update_from_a_replaced_connector_is_ignored()
    {
        var f = new Factory();
        var rt = Create(f.Create);
        await rt.StartAsync();
        var oldContext = f[0].Context!;

        await rt.RestartAsync();
        oldContext.Update(new TileState { Title = "stale" });
        oldContext.ReportError("stale error");

        Assert.NotEqual("stale", rt.State.Title);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Throwing_factory_shows_the_error_and_retries()
    {
        var calls = 0;
        var rt = Create(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("ctor boom");
            return new FakeConnector { OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "ok" }); return Task.CompletedTask; } };
        }, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(100)]));

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.True(rt.LastError!.Fatal);
        Assert.Equal("ctor boom", rt.LastError.Message);
        await Eventually.Holds(() => rt.State.Title == "ok");
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_restart()
    {
        var f = new Factory { Setup = (c, _) => c.OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(f.Create, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(50)]));
        await rt.StartAsync();

        await rt.DisposeAsync();
        await Task.Delay(250);

        Assert.Equal(1, f.Count);
    }

    [Fact]
    public async Task Context_exposes_the_config_directory()
    {
        var c = new FakeConnector();
        var rt = Create(() => c, configDirectory: @"C:\cfg");

        await rt.StartAsync();

        Assert.Equal(@"C:\cfg", c.Context!.ConfigDirectory);
    }

    [Fact]
    public async Task Throwing_resumed_handler_does_not_skip_the_others()
    {
        var log = new CollectingLog();
        var c = new FakeConnector();
        var rt = Create(() => c, log: log);
        await rt.StartAsync();
        var second = new TaskCompletionSource();
        c.Context!.Resumed += (_, _) => throw new InvalidOperationException("first handler");
        c.Context.Resumed += (_, _) => second.TrySetResult();

        rt.RaiseResumed();

        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Eventually.Holds(() => log.Snapshot().Any(m => m.StartsWith("WARN") && m.Contains("first handler")));
    }

    [Fact]
    public async Task Repeated_action_while_in_flight_is_dropped()
    {
        var gate = new TaskCompletionSource();
        var c = new FakeConnector { OnAction = (id, _) => id == "next" ? gate.Task : Task.CompletedTask };
        var rt = Create(() => c);
        await rt.StartAsync();

        var first = rt.InvokeActionAsync("next");
        await rt.InvokeActionAsync("next"); // returns at once: "next" is still running
        await rt.InvokeActionAsync("prev"); // other ids are not blocked

        await Eventually.Holds(() => c.Actions.Count == 2);
        Assert.Equal(1, c.Actions.Count(a => a == "next"));
        gate.SetResult();
        await first;
        await rt.InvokeActionAsync("next");
        Assert.Equal(2, c.Actions.Count(a => a == "next"));
    }
}
