using System.IO;
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileCommandsTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    sealed class Fixture : IDisposable
    {
        readonly List<FakeConnector> _connectors = [];

        public Fixture(Action<FakeConnector, int>? setup = null)
        {
            Commands = new TileCommands(Dir.File("config.json"), Path.Combine(Dir.Path, "logs"), p => Opened.Add(p), Log);
            Runtime = new TileRuntime("tile1", "fake", 132, () =>
            {
                var c = new FakeConnector();
                lock (_connectors)
                {
                    setup?.Invoke(c, _connectors.Count);
                    _connectors.Add(c);
                }
                return c;
            }, NoSettings, new ImmediateDispatcher(), Log, new TileRuntimeOptions { Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]) });
        }

        public TempDir Dir { get; } = new();
        public List<string> Opened { get; } = [];
        public CollectingLog Log { get; } = new();
        public TileCommands Commands { get; }
        public TileRuntime Runtime { get; }
        public int Created { get { lock (_connectors) return _connectors.Count; } }
        public FakeConnector First { get { lock (_connectors) return _connectors[0]; } }
        public void Dispose() => Dir.Dispose();
    }

    static readonly SlotMenuState MiddleTile = new(IsFirst: false, IsLast: false, IsStack: false, RotationPaused: false, CanStack: true);

    [Fact]
    public void Tile_menu_lists_tile_commands_layout_commands_then_bar_commands() =>
        Assert.Equal(
            new[]
            {
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
                "Move left", "Move right", "Add to stack", "", "Edit layout", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile, editing: false).Select(e => e.Header));

    [Fact]
    public void Stack_menu_offers_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true }, editing: false);
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true }, editing: false);

        Assert.Equal(new[] { "Remove from stack", "Pause rotation" }, running.Skip(7).Take(2).Select(e => e.Header));
        Assert.Equal(LayoutCommand.PauseRotation, running[8].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[8].Layout);
        Assert.DoesNotContain(running, e => e.Layout == LayoutCommand.AddToStack);
    }

    [Fact]
    public void Moves_and_add_to_stack_are_disabled_where_they_cannot_apply()
    {
        var only = TileMenu.Entries(new SlotMenuState(IsFirst: true, IsLast: true, IsStack: false, RotationPaused: false, CanStack: false), editing: false);

        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveLeft).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveRight).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.AddToStack).Enabled);
        Assert.True(only.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(null, editing: false);

        Assert.Equal(new[] { "Edit layout", "", "Show config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.EditLayout, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.OpenConfig, entries[2].Bar);
        Assert.Equal(BarCommand.Exit, entries[4].Bar);
    }

    [Fact]
    public void While_editing_both_menus_offer_done_editing()
    {
        Assert.Equal(BarCommand.DoneEditing, TileMenu.Entries(null, editing: true)[0].Bar);
        var tile = TileMenu.Entries(MiddleTile, editing: true);
        Assert.Contains(tile, e => e.Header == "Done editing" && e.Bar == BarCommand.DoneEditing);
        Assert.DoesNotContain(tile, e => e.Header == "Edit layout");
    }

    [Fact]
    public async Task Configure_opens_the_config_file()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Configure);

        Assert.Equal(new[] { f.Dir.File("config.json") }, f.Opened);
    }

    [Fact]
    public async Task Show_log_creates_and_opens_the_tile_log()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowLog);

        var expected = Path.Combine(f.Dir.Path, "logs", "tile-tile1.log");
        Assert.Equal(new[] { expected }, f.Opened);
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, f.Commands.LogPathFor("tile1"));
    }

    [Fact]
    public async Task Error_popup_actions_become_commands()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.InvokeAsync(f.Runtime, TileRuntime.ShowLogActionId);
        await f.Commands.InvokeAsync(f.Runtime, TileRuntime.RestartActionId);

        Assert.Single(f.Opened);
        Assert.Equal(2, f.Created);
        Assert.Empty(f.First.Actions);
    }

    [Fact]
    public async Task Other_action_ids_reach_the_connector()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.InvokeAsync(f.Runtime, "next");

        Assert.Equal(new[] { "next" }, f.First.Actions);
    }

    [Fact]
    public async Task Refresh_sends_refresh_to_the_connector()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Refresh);

        Assert.Equal(new[] { TileActions.Refresh }, f.First.Actions);
    }

    [Fact]
    public async Task Refresh_on_a_crashed_tile_restarts_it()
    {
        using var f = new Fixture((c, before) =>
        {
            if (before == 0) c.OnStart = (_, _) => throw new InvalidOperationException("boom");
        });
        await f.Runtime.StartAsync();
        Assert.True(f.Runtime.LastError!.Fatal);

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Refresh);

        Assert.Equal(2, f.Created);
        Assert.Empty(f.First.Actions);
    }

    [Fact]
    public async Task Failing_open_is_logged_not_thrown()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();
        var commands = new TileCommands(f.Dir.File("config.json"), f.Dir.Path, _ => throw new InvalidOperationException("no editor"), f.Log);

        await commands.ExecuteAsync(f.Runtime, TileCommand.Configure);
        commands.OpenConfig();

        Assert.Equal(2, f.Log.Snapshot().Count(m => m.StartsWith("ERROR") && m.Contains("no editor")));
    }
}
