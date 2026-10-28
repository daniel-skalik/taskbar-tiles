using System.Text.Json;

namespace TaskbarTiles.Tests;

public class TileKindTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Instance_keys_replace_the_kinds_keys_one_by_one()
    {
        var kind = TestKinds.Script("ping", """{ "host": "1.1.1.1", "count": 4, "hooks": { "interval": { "every": "15s", "run": "ping.ps1" } } }""");

        var settings = kind.SettingsFor(Json("""{ "host": "192.168.1.1", "label": "home" }"""));

        Assert.Equal("""{"host":"192.168.1.1","count":4,"hooks":{"interval":{"every":"15s","run":"ping.ps1"}},"label":"home"}""",
            settings.GetRawText());
    }

    [Fact]
    public void The_merge_is_shallow()
    {
        var kind = TestKinds.Script("x", """{ "opts": { "a": 1, "b": 2 } }""");

        Assert.Equal("""{"opts":{"a":5}}""", kind.SettingsFor(Json("""{ "opts": { "a": 5 } }""")).GetRawText());
    }

    [Fact]
    public void No_instance_settings_means_the_kinds_settings() =>
        Assert.Equal("""{"host":"1.1.1.1"}""", TestKinds.Script("ping", """{ "host": "1.1.1.1" }""").SettingsFor(Json("{}")).GetRawText());
}
