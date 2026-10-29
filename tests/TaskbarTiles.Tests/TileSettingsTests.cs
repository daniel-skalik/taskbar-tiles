using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileSettingsTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static (IReadOnlyList<TileField> Fields, string? Error) Parse(string fields) => TileFields.Parse(Json(fields), "t.json");

    [Fact]
    public void Every_field_type_parses()
    {
        var (fields, error) = Parse("""
        [ { "key": "host", "label": "Host", "type": "text", "help": "Name or IP" },
          { "key": "count", "label": "Pings", "type": "number", "min": 1, "max": 10 },
          { "key": "loud", "label": "Sound", "type": "toggle" },
          { "key": "mode", "label": "Mode", "type": "choice", "choices": ["fast", "slow"] },
          { "key": "interval", "label": "Every", "type": "duration", "min": "10s", "max": "1h" } ]
        """);

        Assert.Null(error);
        Assert.Equal(new TileField("host", "Host", FieldType.Text, "Name or IP"), fields[0]);
        Assert.Equal((1d, 10d), (fields[1].Min!.Value, fields[1].Max!.Value));
        Assert.Equal(FieldType.Toggle, fields[2].Type);
        Assert.Equal(new[] { "fast", "slow" }, fields[3].Choices);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromHours(1)), (fields[4].MinDuration!.Value, fields[4].MaxDuration!.Value));
    }

    [Theory]
    [InlineData("{}", "t.json: \"fields\" must be a list.")]
    [InlineData("""[ 1 ]""", "t.json: field 1 must be an object.")]
    [InlineData("""[ { "label": "A", "type": "text" } ]""", "t.json: field 1 needs a \"key\": the settings key it edits.")]
    [InlineData("""[ { "key": "hooks", "label": "A", "type": "text" } ]""", "t.json: field 1 cannot edit \"hooks\".")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "text" }, { "key": "a", "label": "B", "type": "text" } ]""", "t.json: field 2 repeats the key \"a\".")]
    [InlineData("""[ { "key": "a", "type": "text" } ]""", "t.json: field 1 needs a \"label\".")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "color" } ]""", "t.json: field 1 needs a \"type\": text, number, toggle, choice, duration or pick.")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "pick", "choices": "x" } ]""", "t.json: field 1 has \"choices\" that are not a list of strings.")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "number", "min": "1" } ]""", "t.json: field 1 has a \"min\" that is not a number.")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "duration", "max": "soon" } ]""", "t.json: field 1 has a \"max\" that is not a duration such as \"1h\".")]
    [InlineData("""[ { "key": "a", "label": "A", "type": "choice", "choices": [] } ]""", "t.json: field 1 needs \"choices\": a list of strings.")]
    public void A_bad_field_list_names_the_problem(string fields, string expected) => Assert.Equal(expected, Parse(fields).Error);

    [Fact]
    public void A_bad_field_list_breaks_the_kind()
    {
        var json = TileJson.Parse("""{ "name": "A", "connector": "script", "fields": [ { "key": "a" } ] }""", "t.json");

        Assert.Equal("t.json: field 1 needs a \"label\".", json.Error);
        Assert.Empty(TileJson.Parse("""{ "name": "A", "connector": "script" }""", "t.json").Fields!);
    }

    static readonly TileField Count = new("count", "Pings", FieldType.Number, Min: 1, Max: 10);
    static readonly TileField Every = new("interval", "Every", FieldType.Duration, MinDuration: TimeSpan.FromSeconds(10), MaxDuration: TimeSpan.FromHours(1));

    [Fact]
    public void Inputs_become_json_values_and_empty_means_the_default()
    {
        Assert.Equal((null, null), TileFields.Check(Count, "  "));
        Assert.Equal("\"8.8.8.8\"", TileFields.Check(new TileField("host", "Host", FieldType.Text), " 8.8.8.8 ").Value!.ToJsonString());
        Assert.Equal("4", TileFields.Check(Count, "4").Value!.ToJsonString());
        Assert.Equal("\"2m\"", TileFields.Check(Every, "120s").Value!.ToJsonString());
        Assert.Equal("true", TileFields.Check(new TileField("loud", "Sound", FieldType.Toggle), "true").Value!.ToJsonString());
        Assert.Equal("\"slow\"", TileFields.Check(new TileField("m", "M", FieldType.Choice, Choices: ["fast", "slow"]), "slow").Value!.ToJsonString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("many")]
    public void A_number_outside_its_bounds_is_refused(string input) =>
        Assert.Equal("Enter a number from 1 to 10.", TileFields.Check(Count, input).Error);

    [Theory]
    [InlineData("5s")]
    [InlineData("2h")]
    [InlineData("soon")]
    public void A_duration_outside_its_bounds_is_refused(string input) =>
        Assert.Equal("Enter a duration from 10s to 1h, e.g. 30s.", TileFields.Check(Every, input).Error);

    [Fact]
    public void A_choice_must_be_one_of_its_choices() =>
        Assert.Equal("Choose one of: fast, slow.", TileFields.Check(new TileField("m", "M", FieldType.Choice, Choices: ["fast", "slow"]), "medium").Error);

    [Fact]
    public void Saving_sets_changed_keys_removes_defaults_and_keeps_the_rest()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "p": { "kind": "ping", "note": 1, "settings": { "host": "x", "extra": true, "count": 3 } } } }""");

        ConfigJson.SetTileSettings(root, "p", new Dictionary<string, JsonNode?> { ["host"] = "8.8.8.8", ["count"] = 4, ["label"] = null },
            Json("""{ "host": "1.1.1.1", "count": 4 }"""));

        Assert.Equal("""{"kind":"ping","note":1,"settings":{"host":"8.8.8.8","extra":true}}""", root["tiles"]!["p"]!.ToJsonString());
    }

    [Fact]
    public void Settings_left_empty_are_removed()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "p": { "kind": "ping", "settings": { "host": "1.1.1.1" } } } }""");

        ConfigJson.SetTileSettings(root, "p", new Dictionary<string, JsonNode?> { ["host"] = "1.1.1.1" }, Json("""{ "host": "1.1.1.1" }"""));

        Assert.Equal("""{"kind":"ping"}""", root["tiles"]!["p"]!.ToJsonString());
    }

    [Fact]
    public void Saving_a_tile_that_is_gone_throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            ConfigJson.SetTileSettings(ConfigJson.ParseRoot("{}"), "p", new Dictionary<string, JsonNode?>(), Json("{}")));

    const string Config = """
    { "tiles": { "p": { "kind": "ping" }, "u": { "kind": "uptime" } }, "displays": { "primary": { "left": ["p", "u"] } } }
    """;

    static readonly TileField Host = new("host", "Host", FieldType.Text);

    static (HostController Host, FakeConfigWriter Writer) Create()
    {
        var writer = new FakeConfigWriter(Config);
        var kinds = TestKinds.Of(TestKinds.Script("ping", """{ "host": "1.1.1.1" }""", name: "Ping a host", fields: [Host]), TestKinds.Script("uptime"));
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), FakeBar.Into(), writer: writer, kinds: () => kinds);
        host.Start();
        return (host, writer);
    }

    [Fact]
    public void Only_kinds_with_fields_have_settings()
    {
        var (host, _) = Create();

        var form = host.SettingsFor("p")!;
        Assert.Equal(("p", "Ping a host"), (form.TileId, form.KindName));
        Assert.Equal(Host, Assert.Single(form.Fields));
        Assert.Equal("1.1.1.1", form.Defaults.GetProperty("host").GetString());
        Assert.Null(host.SettingsFor("u"));
        Assert.Null(host.SettingsFor("ghost"));
    }

    [Fact]
    public void Saving_settings_writes_config_and_restarts_the_tile()
    {
        var (host, writer) = Create();
        var before = host.Runtimes["p"];

        Assert.Null(host.SaveSettings("p", new Dictionary<string, JsonNode?> { ["host"] = "8.8.8.8" }));

        Assert.Equal("8.8.8.8", writer.Current.Tiles["p"].Settings.GetProperty("host").GetString());
        Assert.NotSame(before, host.Runtimes["p"]);
        Assert.Equal("8.8.8.8", host.SettingsFor("p")!.Own.GetProperty("host").GetString());

        Assert.Null(host.SaveSettings("p", new Dictionary<string, JsonNode?> { ["host"] = null }));
        Assert.False(writer.Current.Tiles["p"].Settings.TryGetProperty("host", out _)); // back to the kind's default
    }

    [Fact]
    public void A_tile_without_settings_cannot_be_saved()
    {
        var (host, _) = Create();

        Assert.NotNull(host.SaveSettings("u", new Dictionary<string, JsonNode?>()));
    }

    [Fact]
    public void The_menu_offers_settings_only_when_the_kind_has_fields()
    {
        var with = TileMenu.Entries(new SlotMenuState(IsStack: false, RotationPaused: false, HasSettings: true), editing: false);

        Assert.Equal(("Settings…", TileCommand.Settings), (with[0].Header, with[0].Tile));
        Assert.DoesNotContain(TileMenu.Entries(new SlotMenuState(false, false), editing: false), e => e.Tile == TileCommand.Settings);
    }

    [Fact]
    public void Bundled_fields_edit_keys_their_kind_has_defaults_for()
    {
        var kinds = new KindLoader(new CollectingLog()).Load(Path.Combine(AppContext.BaseDirectory, "tiles"));

        Assert.Equal(new[] { "host" }, kinds.Kinds["ping"].Fields!.Select(f => f.Key));
        Assert.Equal(new[] { "app" }, kinds.Kinds["media"].Fields!.Select(f => f.Key));
        Assert.Equal(new[] { "interval", "show" }, kinds.Kinds["battery"].Fields!.Select(f => f.Key));
        Assert.Empty(kinds.Kinds["uptime"].Fields!);
        foreach (var kind in kinds.Kinds.Values)
            foreach (var field in kind.Fields!.Where(f => f.Type != FieldType.Pick)) // a pick's default is "all"
            {
                Assert.True(kind.Settings.TryGetProperty(field.Key, out var value), $"{kind.Id}: no default for {field.Key}");
                Assert.Null(TileFields.Check(field, TileFields.Display(value)).Error);
            }
    }

    static readonly TileField Devices = new("show", "Devices", FieldType.Pick, Choices: ["Keyboard"]);

    [Fact]
    public void A_pick_parses_with_or_without_fixed_choices()
    {
        var (fields, error) = Parse("""[ { "key": "show", "label": "Devices", "type": "pick" }, { "key": "b", "label": "B", "type": "pick", "choices": ["x"] } ]""");

        Assert.Null(error);
        Assert.Equal((FieldType.Pick, 0), (fields[0].Type, fields[0].Choices!.Count));
        Assert.Equal(new[] { "x" }, fields[1].Choices);
    }

    [Fact]
    public void A_pick_saves_the_ticked_list()
    {
        Assert.Equal("""["Mouse","Buds"]""", TileFields.Check(Devices, """["Mouse","Buds"]""").Value!.ToJsonString());
        Assert.Equal("[]", TileFields.Check(Devices, "[]").Value!.ToJsonString()); // nothing ticked is a choice too
        Assert.Equal((null, null), TileFields.Check(Devices, ""));
        Assert.NotNull(TileFields.Check(Devices, "Mouse").Error);
        Assert.NotNull(TileFields.Check(Devices, "[1]").Error);
    }

    [Fact]
    public void A_pick_offers_live_then_fixed_then_saved_values_once()
    {
        var choices = TileFields.PickChoices(Devices, ["Mouse", "keyboard", " "], Json("""["Old headset", "mouse"]"""));

        Assert.Equal(new[] { "Mouse", "keyboard", "Old headset" }, choices);
        Assert.Equal(new[] { "Keyboard" }, TileFields.PickChoices(Devices, [], null));
    }

    sealed class Choosing(Func<string, IReadOnlyList<string>> choices) : ITileConnector, ITileChoices
    {
        public Task StartAsync(ITileContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
        public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
        public Task<IReadOnlyList<string>> GetChoicesAsync(string key, CancellationToken ct) => Task.FromResult(choices(key));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static async Task<TileRuntime> Running(ITileConnector connector)
    {
        var runtime = new TileRuntime("t", "fake", 132, () => connector, Json("{}"), new ImmediateDispatcher(), new CollectingLog());
        await runtime.StartAsync();
        return runtime;
    }

    [Fact]
    public async Task The_runtime_asks_the_connector_for_choices_and_never_throws()
    {
        Assert.Equal(new[] { "Mouse" }, await (await Running(new Choosing(k => k == "show" ? ["Mouse", ""] : []))).GetChoicesAsync("show"));
        Assert.Empty(await (await Running(new Choosing(_ => throw new InvalidOperationException("boom")))).GetChoicesAsync("show"));
        Assert.Empty(await (await Running(new FakeConnector())).GetChoicesAsync("show"));
    }
}
