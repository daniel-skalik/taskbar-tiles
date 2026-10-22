using TaskbarTiles.Contracts;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class HostControllerTests
{
    sealed class ThrowingConnector : ITileConnector
    {
        public ThrowingConnector() => throw new InvalidOperationException("boom");
        public Task StartAsync(ITileContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
        public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class SlowDisposeConnector : ITileConnector
    {
        public Task StartAsync(ITileContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
        public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
        public async ValueTask DisposeAsync() => await Task.Delay(50).ConfigureAwait(false);
    }

    sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
    }

    sealed class CapturingConnector : ITileConnector
    {
        [ThreadStatic] public static bool InsideStart;
        public static bool ConstructedInsideStart { get; private set; }
        public static TaskCompletionSource<ITileContext> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturingConnector() => ConstructedInsideStart = InsideStart;
        public Task StartAsync(ITileContext ctx, CancellationToken ct) { Started.TrySetResult(ctx); return Task.CompletedTask; }
        public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
        public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    const string TwoTiles = """
    { "tiles": { "a": { "connector": "ghost" }, "b": { "connector": "ghost" } },
      "displays": { "primary": { "slots": ["a", "b"] }, "*": { "slots": ["b"] } } }
    """;

    (HostController Host, FakeTaskbarSource Source, Dictionary<string, FakeBar> Bars, Func<HostConfig> Cfg, Action<HostConfig> SetCfg) Create(HostConfig cfg)
    {
        var current = cfg;
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var bars = new Dictionary<string, FakeBar>();
        var host = new HostController(ConnectorCatalog.Empty, () => current, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars[id] = new FakeBar(id));
        return (host, source, bars, () => current, c => current = c);
    }

    [Fact]
    public void Start_creates_a_bar_per_display_with_its_tiles()
    {
        var (host, _, bars, _, _) = Create(Parse(TwoTiles));

        host.Start();

        Assert.Equal(new[] { "a", "b" }, bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal("MAIN", bars["MAIN"].Info!.DisplayId);
    }

    [Fact]
    public void Tile_on_two_displays_shares_one_runtime()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        host.Start();

        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("SECOND", false)];
        source.Raise();

        Assert.Same(bars["MAIN"].Tiles[1], bars["SECOND"].Tiles[0]);
        Assert.Equal(2, host.Runtimes.Count);
    }

    [Fact]
    public void Removed_display_closes_its_bar()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("SECOND", false)];
        host.Start();

        source.Current = [FakeTaskbarSource.Display("MAIN", true)];
        source.Raise();

        Assert.True(bars["SECOND"].Closed);
    }

    [Fact]
    public void Config_change_keeps_unchanged_runtimes_and_drops_removed_ones()
    {
        var (host, _, bars, _, setCfg) = Create(Parse(TwoTiles));
        host.Start();
        var a = host.Runtimes["a"];

        setCfg(Parse("""{ "tiles": { "a": { "connector": "ghost" } }, "displays": { "primary": { "slots": ["a"] } } }"""));
        host.ConfigChanged();

        Assert.Same(a, host.Runtimes["a"]);
        Assert.False(host.Runtimes.ContainsKey("b"));
        Assert.Equal(new[] { "a" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Changed_settings_recreate_the_runtime()
    {
        var (host, _, _, _, setCfg) = Create(Parse(TwoTiles));
        host.Start();
        var a = host.Runtimes["a"];

        setCfg(Parse("""{ "tiles": { "a": { "connector": "ghost", "settings": { "x": 1 } }, "b": { "connector": "ghost" } }, "displays": { "primary": { "slots": ["a", "b"] } } }"""));
        host.ConfigChanged();

        Assert.NotSame(a, host.Runtimes["a"]);
    }

    [Fact]
    public void Unknown_connector_gets_placeholder_runtime()
    {
        var (host, _, _, _, _) = Create(Parse(TwoTiles));
        host.Start();
        Assert.Equal("ghost", host.Runtimes["a"].ConnectorId);
    }

    [Fact]
    public async Task Throwing_plugin_constructor_shows_an_error_tile()
    {
        var catalog = new ConnectorCatalog(new Dictionary<string, ConnectorDescriptor>
        {
            ["bad"] = new("bad", typeof(ThrowingConnector), 100, "bad.dll"),
        });
        var cfg = Parse("""{ "tiles": { "a": { "connector": "bad" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var bars = new Dictionary<string, FakeBar>();
        var log = new CollectingLog();
        var host = new HostController(catalog, () => cfg, source, new ImmediateDispatcher(),
            _ => log, id => bars[id] = new FakeBar(id));

        host.Start();

        Assert.Equal("bad", host.Runtimes["a"].ConnectorId);
        Assert.Equal(new[] { "a" }, bars["MAIN"].Tiles.Select(t => t.TileId));
        await Eventually.Holds(() => host.Runtimes["a"].State.Title == "Error");
        Assert.Equal("boom", host.Runtimes["a"].LastError!.Message); // not "Exception has been thrown by the target of an invocation."
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Connectors_are_built_off_the_calling_thread_and_get_the_config_directory()
    {
        var catalog = new ConnectorCatalog(new Dictionary<string, ConnectorDescriptor>
        {
            ["cap"] = new("cap", typeof(CapturingConnector), 100, "cap.dll"),
        });
        var cfg = Parse("""{ "tiles": { "a": { "connector": "cap" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var host = new HostController(catalog, () => cfg, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), configDirectory: @"C:\cfg");

        CapturingConnector.InsideStart = true;
        try { host.Start(); }
        finally { CapturingConnector.InsideStart = false; }

        var ctx = await CapturingConnector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"C:\cfg", ctx.ConfigDirectory);
        Assert.False(CapturingConnector.ConstructedInsideStart);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_does_not_deadlock_under_single_threaded_context()
    {
        var catalog = new ConnectorCatalog(new Dictionary<string, ConnectorDescriptor>
        {
            ["slow"] = new("slow", typeof(SlowDisposeConnector), 100, "slow.dll"),
        });
        var cfg = Parse("""{ "tiles": { "a": { "connector": "slow" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var host = new HostController(catalog, () => cfg, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id));
        host.Start();

        var previous = SynchronizationContext.Current;
        Task disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            disposal = host.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await disposal.WaitAsync(TimeSpan.FromSeconds(2)); // throws TimeoutException if it needs the blocked context
    }

    [Fact]
    public void Duplicate_display_ids_yield_one_bar_without_throwing()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("MAIN", true)];

        host.Start();

        Assert.Single(bars);
    }

    [Fact]
    public void Failing_display_does_not_skip_the_others()
    {
        var current = Parse(TwoTiles);
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("BAD", true), FakeTaskbarSource.Display("GOOD", false)] };
        var bars = new Dictionary<string, FakeBar>();
        var log = new CollectingLog();
        var host = new HostController(ConnectorCatalog.Empty, () => current, source, new ImmediateDispatcher(),
            _ => log, id => id == "BAD" ? throw new InvalidOperationException("no bar") : bars[id] = new FakeBar(id));

        host.Start();

        Assert.True(bars.ContainsKey("GOOD"));
        Assert.Contains(log.Messages, m => m.StartsWith("ERROR"));
    }

    [Fact]
    public async Task Sync_after_dispose_is_a_no_op()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        host.Start();
        await host.DisposeAsync();

        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("SECOND", false)];
        source.Raise();
        host.ConfigChanged();
        host.NotifyResumed();

        Assert.False(bars.ContainsKey("SECOND"));
        Assert.Empty(host.Runtimes);
    }
}
