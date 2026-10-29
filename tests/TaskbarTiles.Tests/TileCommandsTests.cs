using System.IO;
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileCommandsTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    sealed class Fixture : IDisposable
    {
        readonly List<FakeConnector> _connectors = [];

        public Fixture(Action<FakeConnector, int>? setup = null, string tileDirectory = "")
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
            }, NoSettings, new ImmediateDispatcher(), Log,
                new TileRuntimeOptions { Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]), TileDirectory = tileDirectory });
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

    static readonly SlotMenuState MiddleTile = new(IsStack: false, RotationPaused: false);

    static IEnumerable<string> Headers(IEnumerable<MenuEntry> entries) => entries.Select(e => e.Header);

    [Fact]
    public void Tile_menu_keeps_everyday_commands_on_top_and_the_rest_in_submenus()
    {
        var entries = TileMenu.Entries(MiddleTile, editing: false);

        Assert.Equal(new[] { "Refresh now", "", "Create stack…", "", "Edit layout", "", "Show", "More" }, Headers(entries));
        Assert.Equal(new[] { "Tile folder", "Connector log", "Config file" }, Headers(entries[6].Children!));
        Assert.Equal(new[] { TileCommand.ShowFolder, TileCommand.ShowLog }, entries[6].Children!.Take(2).Select(e => e.Tile!.Value));
        Assert.Equal(BarCommand.OpenConfig, entries[6].Children![2].Bar);
        Assert.Equal(new[] { "Restart tile", "Exit Taskbar Tiles" }, Headers(entries[7].Children!));
        Assert.Equal((TileCommand.Restart, BarCommand.Exit), (entries[7].Children![0].Tile!.Value, entries[7].Children![1].Bar!.Value));
        Assert.False(entries[6].IsSeparator);
    }

    [Fact]
    public void Create_stack_opens_the_picker_instead_of_a_submenu()
    {
        var entry = TileMenu.Entries(MiddleTile, editing: false).Single(e => e.Header == "Create stack…");

        Assert.True(entry.Enabled); // the picker also offers new tiles, so it is never empty for lack of choices here
        Assert.Equal(LayoutCommand.CreateStack, entry.Layout);
    }

    [Fact]
    public void Stack_menu_offers_add_to_stack_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true }, editing: false);
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true }, editing: false);

        Assert.Equal(new[] { "Add to stack…", "Remove from stack", "Pause rotation" }, running.Skip(2).Take(3).Select(e => e.Header));
        Assert.Equal(LayoutCommand.AddToStack, running[2].Layout); // opens the picker, like "Create stack…"
        Assert.True(running[2].Enabled);
        Assert.Equal(LayoutCommand.PauseRotation, running[4].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[4].Layout);
        Assert.DoesNotContain(running, e => e.Header == "Create stack…");
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

        Assert.Equal(new[] { "Edit layout", "", "Show", "More" }, Headers(entries));
        Assert.Equal(BarCommand.EditLayout, entries[0].Bar);
        Assert.Equal(BarCommand.OpenConfig, Assert.Single(entries[2].Children!).Bar);
        Assert.Equal(BarCommand.Exit, Assert.Single(entries[3].Children!).Bar);
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
    public async Task Show_tile_folder_reveals_the_kind_folder()
    {
        using var kind = new TempDir();
        using var f = new Fixture(tileDirectory: kind.Path);
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);

        Assert.Equal(new[] { kind.Path }, f.Opened);
    }

    [Fact]
    public async Task Show_tile_folder_reveals_the_config_file_when_the_folder_is_gone()
    {
        using var f = new Fixture(tileDirectory: Path.Combine(Path.GetTempPath(), "tt-gone-" + Guid.NewGuid().ToString("N")));
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);

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

        await commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);
        commands.OpenConfig();

        Assert.Equal(2, f.Log.Snapshot().Count(m => m.StartsWith("ERROR") && m.Contains("no editor")));
    }
}
