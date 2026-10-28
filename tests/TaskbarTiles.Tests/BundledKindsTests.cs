using System.IO;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

/// <summary>The kinds in the repo's tiles\ folder, as the build copies them (with the media plugin) to the output.</summary>
public class BundledKindsTests
{
    static string BundledTiles => Path.Combine(AppContext.BaseDirectory, "tiles");

    [Fact]
    public void Every_bundled_kind_parses_and_names_a_connector_that_exists()
    {
        var kinds = new KindLoader(new CollectingLog()).Load(BundledTiles);
        var builtIn = new PluginLoader(new CollectingLog()).LoadFrom([Path.Combine(AppContext.BaseDirectory, "plugins")]);

        Assert.Equal(new[] { "media", "ping", "uptime" }, kinds.Kinds.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(kinds.FoldersWithoutTileJson);
        foreach (var kind in kinds.Kinds.Values)
        {
            Assert.True(kind.Error is null, $"{kind.Id}: {kind.Error}");
            Assert.False(string.IsNullOrWhiteSpace(kind.Description), $"{kind.Id} has no description");
            if (kind.Connector == TileKind.ScriptConnectorId) Assert.True(builtIn.Connectors.ContainsKey(TileKind.ScriptConnectorId));
            else Assert.Equal(kind.Connector, kind.Plugin!.Id);
        }
        Assert.Equal("media", kinds.Kinds["media"].Plugin!.Id);
    }

    [Fact]
    public void Script_kinds_have_valid_hooks_that_run_files_in_their_folder()
    {
        var kinds = new KindLoader(new CollectingLog()).Load(BundledTiles);

        foreach (var kind in kinds.Kinds.Values.Where(k => k.Connector == TileKind.ScriptConnectorId))
        {
            var settings = ScriptSettings.Parse(kind.Settings);
            Assert.Empty(settings.Errors);
            foreach (var hook in settings.Hooks.Values)
                Assert.True(File.Exists(Path.Combine(kind.Directory, hook.Run)), $"{kind.Id}: {hook.Run} is missing");
        }
    }

    static async Task<ScriptResult> RunBundled(string kind, string script, string hook, string settingsJson = "{}")
    {
        var command = new CommandResolver(Path.Combine(BundledTiles, kind), ExecutableLocator.PowerShell()).Resolve(script);
        var env = new Dictionary<string, string>
        {
            ["TILE_ID"] = kind, ["TILE_HOOK"] = hook, ["TILE_ACTION"] = "", ["TILE_SETTINGS_JSON"] = settingsJson,
        };
        return await new ProcessScriptRunner().RunAsync(command, env, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_prints_a_clock_tile()
    {
        var outcome = HookResults.ForTile("interval", await RunBundled("uptime", "uptime.ps1", "interval"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Matches(@"^\d{2}:\d{2}$", outcome.Value!.Title);
        Assert.StartsWith("up ", outcome.Value.Subtitle);
        Assert.Equal("clock", outcome.Value.Icon!.Glyph);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_prints_a_popup()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunBundled("uptime", "uptime.ps1", "flyout"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(new[] { "Time", "Booted", "Uptime" }, outcome.Value!.Rows.Select(r => r.Text));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_prints_a_status_tile_with_a_button()
    {
        var outcome = HookResults.ForTile("interval", await RunBundled("ping", "ping.ps1", "interval", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("127.0.0.1", outcome.Value!.Title);
        Assert.EndsWith(" ms", outcome.Value.Subtitle);
        Assert.Equal("ping", Assert.Single(outcome.Value.Buttons).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_prints_a_popup_with_an_action()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunBundled("ping", "ping.ps1", "flyout", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(4, outcome.Value!.Rows.Count);
        Assert.Equal("ping", Assert.Single(outcome.Value.Actions).Id);
    }
}
