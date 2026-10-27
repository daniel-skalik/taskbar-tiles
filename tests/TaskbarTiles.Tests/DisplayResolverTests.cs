using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class DisplayResolverTests
{
    static readonly DisplayLayout Primary = new([new TileSlot("p", false)]);
    static readonly DisplayLayout Exact = new([new TileSlot("e", false)]);
    static readonly DisplayLayout Any = new([new TileSlot("a", false)]);

    static HostConfig Config(params (string Key, DisplayLayout Layout)[] displays) =>
        new(new Dictionary<string, TileConfig>(), displays.ToDictionary(d => d.Key, d => d.Layout));

    [Fact]
    public void Exact_device_id_wins_over_primary()
    {
        var cfg = Config(("DEV1", Exact), ("primary", Primary), ("*", Any));
        Assert.Same(Exact, DisplayResolver.Resolve(cfg, "DEV1", isPrimary: true));
    }

    [Fact]
    public void Primary_key_applies_to_primary_display() =>
        Assert.Same(Primary, DisplayResolver.Resolve(Config(("primary", Primary), ("*", Any)), "DEV9", isPrimary: true));

    [Fact]
    public void Star_applies_to_other_displays() =>
        Assert.Same(Any, DisplayResolver.Resolve(Config(("primary", Primary), ("*", Any)), "DEV9", isPrimary: false));

    [Fact]
    public void No_match_gives_empty_layout() =>
        Assert.Empty(DisplayResolver.Resolve(Config(("primary", Primary)), "DEV9", isPrimary: false).Slots);

    [Fact]
    public void Placed_tile_ids_include_every_stack_member_once()
    {
        var layout = new DisplayLayout([new TileSlot("a", false), new StackSlot(["b", "c", "a"], TimeSpan.FromSeconds(5)), new StackSlot([], TimeSpan.FromSeconds(5))]);
        Assert.Equal(new[] { "a", "b", "c" }, DisplayResolver.PlacedTileIds(layout));
    }

    [Fact]
    public void Edit_key_is_the_device_id_when_it_has_an_entry() =>
        Assert.Equal("DEV1", DisplayResolver.EditKey(Config(("DEV1", Exact), ("primary", Primary)), "DEV1", isPrimary: true));

    [Fact]
    public void Edit_key_is_primary_on_the_primary_display_without_its_own_entry()
    {
        Assert.Equal("primary", DisplayResolver.EditKey(Config(("primary", Primary), ("*", Any)), "DEV1", isPrimary: true));
        Assert.Equal("primary", DisplayResolver.EditKey(Config(("*", Any)), "DEV1", isPrimary: true));
    }

    [Fact]
    public void Edit_key_is_the_device_id_on_other_displays_never_the_star() =>
        Assert.Equal("DEV2", DisplayResolver.EditKey(Config(("primary", Primary), ("*", Any)), "DEV2", isPrimary: false));
}
