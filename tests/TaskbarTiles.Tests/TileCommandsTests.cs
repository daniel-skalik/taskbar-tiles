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

    static readonly SlotMenuState MiddleTile = new(IsStack: false, RotationPaused: false, StackChoices: [new StackChoice(2, "b", "Bee")]);

    [Fact]
    public void Tile_menu_lists_tile_commands_layout_commands_then_bar_commands() =>
        Assert.Equal(
            new[]
            {
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
                "Create stack…", "", "Edit layout", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile, editing: false).Select(e => e.Header));

    [Fact]
    public void Create_stack_opens_the_picker_instead_of_a_submenu()
    {
        var entry = TileMenu.Entries(MiddleTile with { StackChoices = [] }, editing: false).Single(e => e.Header == "Create stack…");

        Assert.True(entry.Enabled); // the picker also offers new tiles, so it is never empty for lack of choices here
        Assert.Equal(LayoutCommand.CreateStack, entry.Layout);
        Assert.Null(entry.Submenu);
        Assert.Null(entry.Choice);
    }

    [Fact]
    public void Stack_menu_offers_add_to_stack_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true }, editing: false);
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true }, editing: false);

        Assert.Equal(new[] { "Add to stack", "Remove from stack", "Pause rotation" }, running.Skip(5).Take(3).Select(e => e.Header));
        Assert.Equal(LayoutCommand.AddToStack, Assert.Single(running[5].Submenu!).Layout);
        Assert.Equal(LayoutCommand.PauseRotation, running[7].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[7].Layout);
        Assert.DoesNotContain(running, e => e.Header == "Create stack…");
    }

    [Fact]
    public void Add_to_stack_without_choices_is_disabled()
    {
        var stack = TileMenu.Entries(new SlotMenuState(IsStack: true, RotationPaused: false, StackChoices: []), editing: false);

        Assert.False(stack.Single(e => e.Header == "Add to stack").Enabled);
        Assert.True(stack.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }

    static MenuSlot Plain(int index, string id, string? title = null) => new(index, false, [id], title);
    static MenuSlot Stack(int index, params string[] ids) => new(index, true, ids, null);

    [Fact]
    public void Create_stack_choices_are_the_other_plain_tiles()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Stack(1, "s1", "s2"), Plain(2, "b", "Bravo"), Plain(4, "a", "Alpha") };

        Assert.Equal(new[] { new StackChoice(2, "b", "Bravo") }, TileMenu.StackChoices(slots, clicked: 0));
    }

    [Fact]
    public void Add_to_stack_choices_are_the_plain_tiles_not_in_the_stack()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Stack(1, "a", "b"), Plain(2, "c", "Charlie") };

        Assert.Equal(new[] { new StackChoice(2, "c", "Charlie") }, TileMenu.StackChoices(slots, clicked: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("…")]
    public void A_title_without_letters_or_digits_shows_the_tile_id(string? title) =>
        Assert.Equal("ping", TileMenu.StackChoices([Plain(0, "a", "Alpha"), Plain(1, "ping", title)], clicked: 0).Single().Label);

    [Fact]
    public void Repeated_labels_get_the_tile_id()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Plain(1, "ping", "Ping"), Plain(2, "ping-2", "Ping"), Plain(3, "up", " Uptime ") };

        Assert.Equal(new[] { "Ping (ping)", "Ping (ping-2)", "Uptime" }, TileMenu.StackChoices(slots, clicked: 0).Select(c => c.Label));
    }

    [Fact]
    public void An_unknown_clicked_slot_has_no_choices() =>
        Assert.Empty(TileMenu.StackChoices([Plain(0, "a"), Plain(1, "b")], clicked: 7));

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
