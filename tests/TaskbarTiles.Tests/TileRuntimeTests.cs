using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Tests;

public class TileRuntimeTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    static TileRuntime Create(FakeConnector c, IUiDispatcher ui, TimeSpan? timeout = null, TimeSpan? notResponding = null) =>
        new("tile1", "fake", 132, c, NoSettings, ui, new CollectingLog(), timeout, notResponding);

    [Fact]
    public async Task Update_from_connector_becomes_state_and_raises_event()
    {
        var c = new FakeConnector { OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "hi" }); return Task.CompletedTask; } };
        var rt = Create(c, new ImmediateDispatcher());
        var raised = 0;
        rt.StateChanged += (_, _) => raised++;

        await rt.StartAsync();

        Assert.Equal("hi", rt.State.Title);
        Assert.Equal(1, raised);
        Assert.Equal("tile1", c.Context!.TileId);
    }

    [Fact]
    public async Task Burst_of_updates_is_coalesced_into_one_post_with_latest_state()
    {
        var c = new FakeConnector();
        var ui = new ManualDispatcher();
        var rt = Create(c, ui);
        await rt.StartAsync();

        c.Context!.Update(new TileState { Title = "1" });
        c.Context.Update(new TileState { Title = "2" });
        c.Context.Update(new TileState { Title = "3" });
        ui.Pump();

        Assert.Equal(1, ui.Posted);
        Assert.Equal("3", rt.State.Title);
    }

    [Fact]
    public async Task Start_exception_shows_error_state()
    {
        var c = new FakeConnector { OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(c, new ImmediateDispatcher());

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.Equal(TileColor.Warn, rt.State.Icon!.Color);
        Assert.Equal("boom", rt.LastError!.Message);
    }

    [Fact]
    public async Task Successful_update_after_error_clears_error()
    {
        var c = new FakeConnector { OnAction = (_, _) => throw new Exception("x") };
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();
        await rt.InvokeActionAsync("click");
        Assert.NotNull(rt.LastError);

        c.Context!.Update(new TileState { Title = "ok" });

        Assert.Null(rt.LastError);
        Assert.Equal("ok", rt.State.Title);
    }

    [Fact]
    public async Task Action_is_forwarded()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        await rt.InvokeActionAsync("next");

        Assert.Equal(new[] { "next" }, c.Actions);
    }

    [Fact]
    public async Task Slow_action_shows_not_responding_then_restores()
    {
        var c = new FakeConnector
        {
            OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "normal" }); return Task.CompletedTask; },
            OnAction = (_, ct) => Task.Delay(Timeout.Infinite, ct),
        };
        var rt = Create(c, new ImmediateDispatcher(), timeout: TimeSpan.FromMilliseconds(50), notResponding: TimeSpan.FromMilliseconds(100));
        await rt.StartAsync();

        await rt.InvokeActionAsync("click");
        Assert.Equal("Not responding", rt.State.Title);

        await Task.Delay(400);
        Assert.Equal("normal", rt.State.Title);
    }

    [Fact]
    public async Task Flyout_while_in_error_describes_the_error()
    {
        var c = new FakeConnector { OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        var flyout = await rt.GetFlyoutAsync();

        Assert.Equal("Tile error", flyout!.Title);
        Assert.Contains(flyout.Rows, r => r.Text.Contains("boom"));
    }

    [Fact]
    public async Task Resumed_reaches_the_connector()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();
        var resumed = false;
        c.Context!.Resumed += (_, _) => resumed = true;

        rt.RaiseResumed();

        Assert.True(resumed);
    }

    [Fact]
    public async Task Dispose_disposes_connector()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        await rt.DisposeAsync();

        Assert.True(c.Disposed);
    }

    [Fact]
    public async Task Unknown_connector_explains_itself()
    {
        var rt = new TileRuntime("t", "ghost", 132, new UnknownConnector("ghost"), NoSettings, new ImmediateDispatcher(), new CollectingLog());

        await rt.StartAsync();

        Assert.Equal("Unknown connector", rt.State.Title);
        Assert.Equal("ghost", rt.State.Subtitle);
    }
}
