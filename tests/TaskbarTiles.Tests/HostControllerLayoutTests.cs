using System.Text.Json;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class HostControllerLayoutTests
{
    const string Config = """
    { "tiles": { "a": { "connector": "ghost" }, "b": { "connector": "ghost" }, "c": { "connector": "ghost" } },
      "displays": {
        "primary": { "slots": [ { "tile": "a", "pin": true }, { "stack": ["b", "c"], "rotate": "8s" } ] },
        "*": { "slots": ["c"] } } }
    """;

    sealed class Fixture
    {
        public Fixture(string json, params (string Id, bool Primary)[] displays)
        {
            Writer = new FakeConfigWriter(json);
            Source = new FakeTaskbarSource
            {
                Current = displays.Length == 0
                    ? [FakeTaskbarSource.Display("MAIN", true)]
                    : displays.Select(d => FakeTaskbarSource.Display(d.Id, d.Primary)).ToList(),
            };
            Host = new HostController(ConnectorCatalog.Empty, () => Writer.Current, Source, new ImmediateDispatcher(),
                _ => new CollectingLog(), id => Bars[id] = new FakeBar(id), writer: Writer);
            Host.Start();
        }

        public FakeConfigWriter Writer { get; }
        public FakeTaskbarSource Source { get; }
        public Dictionary<string, FakeBar> Bars { get; } = [];
        public HostController Host { get; }

        public JsonElement Displays => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("displays");
    }

    [Fact]
    public void A_stack_is_one_slot_and_all_its_members_get_runtimes()
    {
        var f = new Fixture(Config);

        var slots = f.Bars["MAIN"].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Index);
        Assert.True(slots[0].Pinned);
        Assert.False(slots[0].IsStack);
        Assert.Equal(1, slots[1].Index);
        Assert.Equal(TimeSpan.FromSeconds(8), slots[1].Rotate);
        Assert.Equal(new[] { "b", "c" }, slots[1].Members.Select(m => m.TileId));
        Assert.Equal(new[] { "a", "b", "c" }, f.Host.Runtimes.Keys.Order());
    }

    [Fact]
    public void An_empty_stack_shows_nothing_but_later_slots_keep_their_index()
    {
        var f = new Fixture("""
        { "tiles": { "a": { "connector": "ghost" } }, "displays": { "primary": { "slots": [ { "stack": [] }, "a" ] } } }
        """);

        var slot = Assert.Single(f.Bars["MAIN"].Slots);
        Assert.Equal(1, slot.Index);
    }

    [Fact]
    public void Editing_the_primary_display_writes_primary()
    {
        var f = new Fixture(Config);

        var error = f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After));

        Assert.Null(error);
        Assert.Equal(JsonValueKind.Object, f.Displays.GetProperty("primary").GetProperty("slots")[0].ValueKind); // the stack now first
        Assert.False(f.Displays.TryGetProperty("MAIN", out _));
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_a_display_that_uses_the_star_forks_its_own_entry()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false), ("THIRD", false));

        var error = f.Host.EditLayout("SECOND", l => LayoutEdits.Append(l, "a"));

        Assert.Null(error);
        Assert.Equal(new[] { "c", "a" }, f.Displays.GetProperty("SECOND").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "c" }, f.Displays.GetProperty("*").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "c", "a" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "c" }, f.Bars["THIRD"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_a_display_with_its_own_entry_writes_that_entry()
    {
        var json = Config.Replace("\"*\":", "\"MAIN\": { \"slots\": [\"a\"] }, \"*\":");
        var f = new Fixture(json);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Append(l, "b")));

        Assert.Equal(new[] { "a", "b" }, f.Displays.GetProperty("MAIN").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2, f.Displays.GetProperty("primary").GetProperty("slots").GetArrayLength()); // untouched
    }

    [Fact]
    public void An_edit_that_changes_nothing_saves_nothing()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 0, DropPosition.Before)));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void A_rejected_save_returns_the_error_and_changes_nothing()
    {
        var f = new Fixture(Config);

        var error = f.Host.EditLayout("MAIN", l => LayoutEdits.Append(l, "ghost"));

        Assert.NotNull(error);
        Assert.Contains("ghost", error!.Message);
        Assert.Equal(0, f.Writer.Saves);
        Assert.Equal(new[] { "a", "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_an_unknown_display_is_an_error()
    {
        var f = new Fixture(Config);

        Assert.NotNull(f.Host.EditLayout("GONE", l => LayoutEdits.Append(l, "a")));
        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void Without_a_writer_edits_are_refused()
    {
        var cfg = ConfigParser.Parse(Config).Config!;
        var host = new HostController(ConnectorCatalog.Empty, () => cfg, new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] },
            new ImmediateDispatcher(), _ => new CollectingLog(), id => new FakeBar(id));
        host.Start();

        Assert.NotNull(host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After)));
    }

    [Fact]
    public void Removing_a_stack_member_keeps_its_runtime_while_another_display_places_it()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));
        var c = f.Host.Runtimes["c"];

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Remove(l, 1, member: 1)));

        Assert.Same(c, f.Host.Runtimes["c"]); // still on SECOND through "*"
        Assert.Equal(new[] { "a", "b" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.True(f.Writer.Current.Tiles.ContainsKey("c")); // the definition stays
    }

    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        f.Host.SetEditMode(true);

        Assert.True(f.Host.Editing);
        Assert.True(f.Bars["MAIN"].Editing);
        Assert.True(f.Bars["SECOND"].Editing);

        f.Source.Current = [.. f.Source.Current, FakeTaskbarSource.Display("THIRD", false)];
        f.Source.Raise();
        Assert.True(f.Bars["THIRD"].Editing);

        f.Host.SetEditMode(false);
        Assert.All(f.Bars.Values, b => Assert.False(b.Editing));
    }
}
