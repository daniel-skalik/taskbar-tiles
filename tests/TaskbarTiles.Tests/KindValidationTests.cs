using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindValidationTests
{
    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    [Fact]
    public void Every_kind_with_a_folder_passes_even_a_broken_one()
    {
        var kinds = TestKinds.Of(TestKinds.Script("ping"), TestKinds.Broken("media", @"tiles\media\tile.json is not valid JSON"));

        Assert.Null(KindValidation.Check(Parse("""{ "tiles": { "a": { "kind": "Ping" }, "m": { "kind": "media" } } }"""), kinds));
    }

    [Fact]
    public void A_kind_without_a_folder_names_the_tile_and_the_folder()
    {
        var error = KindValidation.Check(Parse("""{ "tiles": { "a": { "kind": "ping" }, "w": { "kind": "weather" } } }"""),
            TestKinds.Of(TestKinds.Script("ping")));

        Assert.Equal(@"Tile 'w' uses kind 'weather', but there is no tiles\weather folder.", error!.Message);
    }

    [Fact]
    public void A_folder_without_tile_json_gets_a_hint()
    {
        var kinds = TestKinds.Of() with { FoldersWithoutTileJson = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "meeting" } };

        var error = KindValidation.Check(Parse("""{ "tiles": { "m": { "kind": "meeting" } } }"""), kinds);

        Assert.Equal(@"Tile 'm' uses kind 'meeting', but tiles\meeting has no tile.json.", error!.Message);
    }
}
