using System.IO;
using System.Text.Json;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class HostControllerLayoutTests
{
    const string Config = """
    { "tiles": { "a": { "kind": "ghost" }, "b": { "kind": "ghost" }, "c": { "kind": "ghost" } },
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
                _ => new CollectingLog(), FakeBar.Into(Bars, RightBars), writer: Writer);
            Host.Start();
        }

        public FakeConfigWriter Writer { get; }
        public FakeTaskbarSource Source { get; }
        public Dictionary<string, FakeBar> Bars { get; } = [];
        public Dictionary<string, FakeBar> RightBars { get; } = [];
        public HostController Host { get; }

        public JsonElement Displays => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("displays");

        public string[] TileIds => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("tiles").EnumerateObject().Select(p => p.Name).ToArray();
    }

    /// <summary>Config plus a tile defined by hand but not placed on any display.</summary>
    static readonly string WithSpare = Config.Replace(
        "\"c\": { \"kind\": \"ghost\" } }", "\"c\": { \"kind\": \"ghost\" }, \"spare\": { \"kind\": \"ghost\" } }");

    [Fact]
    public void Removing_a_tile_never_touches_its_kind_folder()
    {
        using var tiles = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(tiles.Path, "ghost")).FullName;
        File.WriteAllText(Path.Combine(folder, "tile.json"), """{ "name": "Ghost", "connector": "script" }""");
        File.WriteAllText(Path.Combine(folder, "ghost.ps1"), "'{}'");
        var kinds = new KindLoader(new CollectingLog()).Load(tiles.Path);
        var writer = new FakeConfigWriter(Config);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), FakeBar.Into(), writer: writer, kinds: () => kinds);
        host.Start();

        Assert.Null(host.RemoveTile("MAIN", 0, null));

        Assert.False(writer.Current.Tiles.ContainsKey("a")); // the instance is gone (no display places it)
        Assert.True(File.Exists(Path.Combine(folder, "tile.json")));
        Assert.True(File.Exists(Path.Combine(folder, "ghost.ps1")));
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
        { "tiles": { "a": { "kind": "ghost" } }, "displays": { "primary": { "slots": [ { "stack": [] }, "a" ] } } }
        """);

        var slot = Assert.Single(f.Bars["MAIN"].Slots);
        Assert.Equal(1, slot.Index);
    }

    /// <summary>Config with tile c on the primary display's right side.</summary>
    const string BothSides = """
    { "tiles": { "a": { "kind": "ghost" }, "b": { "kind": "ghost" }, "c": { "kind": "ghost" } },
      "displays": { "primary": { "left": ["a", "b"], "right": ["c"] }, "*": { "left": ["a", "c"] } } }
    """;

    static string[] Ids(JsonElement display, string side) =>
        display.TryGetProperty(side, out var slots) ? slots.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

    [Fact]
    public void Each_side_has_its_own_bar()
    {
        var f = new Fixture(BothSides);

        Assert.Equal(BarSide.Left, f.Bars["MAIN"].Side);
        Assert.Equal(new[] { "a", "b" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(BarSide.Right, f.RightBars["MAIN"].Side);
        Assert.Equal("c", Assert.Single(f.RightBars["MAIN"].Tiles).TileId);
        Assert.Equal(0, Assert.Single(f.RightBars["MAIN"].Slots).Index); // indexes count within the side
    }

    [Fact]
    public void Moving_a_tile_to_the_other_side_saves_both_sides_in_one_entry()
    {
        var f = new Fixture(BothSides);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 1, "MAIN", null, DropPosition.After, BarSide.Left, BarSide.Right));

        var primary = f.Displays.GetProperty("primary");
        Assert.Equal(new[] { "a" }, Ids(primary, "left"));
        Assert.Equal(new[] { "c", "b" }, Ids(primary, "right"));
        Assert.Equal(new[] { "c", "b" }, f.RightBars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Dropping_onto_the_other_side_lands_before_the_target()
    {
        var f = new Fixture(BothSides);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "MAIN", 0, DropPosition.Before, BarSide.Right, BarSide.Left));

        var primary = f.Displays.GetProperty("primary");
        Assert.Equal(new[] { "c", "a", "b" }, Ids(primary, "left"));
        Assert.False(primary.TryGetProperty("right", out _)); // an empty right side is not written
    }

    [Fact]
    public void A_tile_dropped_on_another_display_is_skipped_when_its_other_side_shows_it()
    {
        var f = new Fixture(BothSides, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "SECOND", null, DropPosition.After, BarSide.Left, BarSide.Right));

        Assert.Equal(new[] { "b" }, Ids(f.Displays.GetProperty("primary"), "left")); // a leaves MAIN
        Assert.False(f.Displays.TryGetProperty("SECOND", out _)); // SECOND already shows a through "*"
    }

    [Fact]
    public void Edits_and_removals_change_only_their_own_side()
    {
        var f = new Fixture(BothSides);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Append(l, "a"), BarSide.Right));
        Assert.Equal(new[] { "c", "a" }, Ids(f.Displays.GetProperty("primary"), "right"));

        Assert.Null(f.Host.RemoveTile("MAIN", 0, null, BarSide.Right));
        Assert.Equal(new[] { "a", "b" }, Ids(f.Displays.GetProperty("primary"), "left"));
        Assert.Equal(new[] { "a" }, Ids(f.Displays.GetProperty("primary"), "right"));
        Assert.Contains("c", f.TileIds); // still placed by "*"
    }

    [Fact]
    public void Icon_edges_are_read_only_while_a_display_has_right_side_tiles()
    {
        var f = new Fixture(Config);
        Assert.False(f.Source.TrackIconEdges);

        f.Host.MoveToDisplay("MAIN", 0, "MAIN", null, DropPosition.After, BarSide.Left, BarSide.Right);
        Assert.True(f.Source.TrackIconEdges);

        f.Host.MoveToDisplay("MAIN", 0, "MAIN", null, DropPosition.After, BarSide.Right, BarSide.Left);
        Assert.False(f.Source.TrackIconEdges);
    }

    [Fact]
    public void Editing_the_primary_display_writes_primary()
    {
        var f = new Fixture(Config);

        var error = f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After));

        Assert.Null(error);
        Assert.Equal(JsonValueKind.Object, f.Displays.GetProperty("primary").GetProperty("left")[0].ValueKind); // the stack now first
        Assert.False(f.Displays.TryGetProperty("MAIN", out _));
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_a_display_that_uses_the_star_forks_its_own_entry()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false), ("THIRD", false));

        var error = f.Host.EditLayout("SECOND", l => LayoutEdits.Append(l, "a"));

        Assert.Null(error);
        Assert.Equal(new[] { "c", "a" }, f.Displays.GetProperty("SECOND").GetProperty("left").EnumerateArray().Select(e => e.GetString()));
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

        Assert.Equal(new[] { "a", "b" }, f.Displays.GetProperty("MAIN").GetProperty("left").EnumerateArray().Select(e => e.GetString()));
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
            new ImmediateDispatcher(), _ => new CollectingLog(), FakeBar.Into());
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
    public void Removing_the_last_placement_of_a_tile_deletes_its_definition_in_the_same_save()
    {
        var f = new Fixture(WithSpare);

        Assert.Null(f.Host.RemoveTile("MAIN", 0, null));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c", "spare" }, f.TileIds);
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.False(f.Host.Runtimes.ContainsKey("a"));
    }

    [Fact]
    public void Removing_a_stack_member_nothing_else_places_deletes_its_definition()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.RemoveTile("MAIN", 1, 0)); // b

        Assert.Equal(new[] { "a", "c" }, f.TileIds);
        Assert.Equal(new[] { "a", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void A_tile_another_display_entry_places_keeps_its_definition()
    {
        var f = new Fixture(Config); // only MAIN is connected; "*" still places c

        Assert.Null(f.Host.RemoveTile("MAIN", 1, 1));

        Assert.Equal(new[] { "a", "b", "c" }, f.TileIds);
    }

    [Fact]
    public void Edits_that_remove_no_tile_never_prune_unplaced_definitions()
    {
        var f = new Fixture(WithSpare);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After)));
        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.RemoveFromStack(l, 0, 0)));

        Assert.Equal(2, f.Writer.Saves);
        Assert.Contains("spare", f.TileIds);
    }

    [Fact]
    public void Removing_with_a_stale_index_saves_nothing()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.RemoveTile("MAIN", 9, null));
        Assert.NotNull(f.Host.RemoveTile("GONE", 0, null));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void Moving_a_slot_to_another_display_writes_both_entries_in_one_save()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "SECOND", 0, DropPosition.Before));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "a", "c" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.True(f.Bars["SECOND"].Slots[0].Pinned); // the pin moves along
        Assert.Equal(1, f.Displays.GetProperty("primary").GetProperty("left").GetArrayLength());
        Assert.Equal(2, f.Displays.GetProperty("SECOND").GetProperty("left").GetArrayLength());
        Assert.Equal(new[] { "c" }, f.Displays.GetProperty("*").GetProperty("slots").EnumerateArray().Select(e => e.GetString())); // never written
    }

    [Fact]
    public void Moving_a_stack_to_a_display_that_places_one_member_moves_only_the_rest()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 1, "SECOND", null, DropPosition.After));

        Assert.Equal(new[] { "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "c", "b" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.False(f.Bars["SECOND"].Slots[1].IsStack);
    }

    [Fact]
    public void Moving_a_tile_to_a_display_that_already_places_it_only_removes_it()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("SECOND", 0, "MAIN", null, DropPosition.After));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Empty(f.Bars["SECOND"].Tiles);
        Assert.Equal(new[] { "a", "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(0, f.Displays.GetProperty("SECOND").GetProperty("left").GetArrayLength());
        Assert.Contains("c", f.TileIds);
    }

    [Fact]
    public void Moving_a_tile_to_a_display_that_inherits_the_star_entry_and_shows_it_does_not_fork_that_display()
    {
        var f = new Fixture(Config.Replace("{ \"stack\": [\"b\", \"c\"], \"rotate\": \"8s\" }", "\"c\""), ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 1, "SECOND", null, DropPosition.After)); // SECOND shows c through "*"

        Assert.Equal(1, f.Writer.Saves);
        Assert.False(f.Displays.TryGetProperty("SECOND", out _));
        Assert.Equal(new[] { "c" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Two_displays_that_save_under_one_key_refuse_a_move_between_them()
    {
        var f = new Fixture(Config, ("MAIN", true), ("primary", false)); // MAIN edits "primary"; so does the display named that

        Assert.NotNull(f.Host.MoveToDisplay("MAIN", 0, "primary", null, DropPosition.After));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void A_display_without_tiles_accepts_a_move()
    {
        var f = new Fixture(Config.Replace("\"*\": { \"slots\": [\"c\"] }", "\"*\": { \"slots\": [] }"), ("MAIN", true), ("SECOND", false));
        Assert.Empty(f.Bars["SECOND"].Slots);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "SECOND", null, DropPosition.After));

        Assert.Equal(new[] { "a" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Moving_within_one_display_is_a_drop()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "MAIN", 1, DropPosition.After));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Dropping_on_the_same_display_without_a_target_moves_the_slot_to_the_end()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "MAIN", null, DropPosition.After));
        Assert.Null(f.Host.MoveToDisplay("MAIN", 1, "MAIN", null, DropPosition.After)); // already last: nothing to save

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Moving_from_or_to_an_unknown_display_or_a_stale_slot_saves_nothing()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.NotNull(f.Host.MoveToDisplay("GONE", 0, "SECOND", null, DropPosition.After));
        Assert.NotNull(f.Host.MoveToDisplay("MAIN", 0, "GONE", null, DropPosition.After));
        Assert.Null(f.Host.MoveToDisplay("MAIN", 9, "SECOND", null, DropPosition.After));

        Assert.Equal(0, f.Writer.Saves);
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

    [Fact]
    public void Setting_the_same_edit_mode_again_does_nothing()
    {
        var f = new Fixture(Config);

        f.Host.SetEditMode(false);
        Assert.Equal(0, f.Bars["MAIN"].EditingCalls);

        f.Host.SetEditMode(true);
        f.Host.SetEditMode(true);
        Assert.Equal(1, f.Bars["MAIN"].EditingCalls);
    }

    [Fact]
    public void A_bar_that_fails_to_switch_edit_mode_does_not_stop_the_others()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));
        f.Bars["MAIN"].ThrowOnEditing = true;

        f.Host.SetEditMode(true);

        Assert.True(f.Host.Editing);
        Assert.True(f.Bars["SECOND"].Editing);
    }
}
