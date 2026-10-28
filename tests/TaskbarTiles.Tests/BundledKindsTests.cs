using System.Text.Json;
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

        Assert.Equal(new[] { "claude", "media", "ping", "uptime" }, kinds.Kinds.Keys.Order(StringComparer.Ordinal));
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

    const string AgentsSample = """
        [
          { "id": "a8", "cwd": "D:\\s1", "kind": "background", "name": "Generator", "state": "blocked" },
          { "pid": 1, "cwd": "D:\\tiles", "kind": "interactive", "name": "Widget", "status": "busy" },
          { "pid": 2, "cwd": "D:\\s1", "kind": "interactive", "name": "Bundles", "status": "idle" },
          { "pid": 3, "cwd": "D:\\burner", "kind": "interactive", "status": "idle" }
        ]
        """;

    /// <summary>Runs claude.ps1 with "claude" set to a stub that prints <paramref name="agentsJson"/> (or fails when null).</summary>
    static async Task<ScriptResult> RunClaude(string hook, string? agentsJson)
    {
        using var stub = new TempDir();
        File.WriteAllText(stub.File("agents.json"), agentsJson ?? "");
        File.WriteAllText(stub.File("claude.cmd"), agentsJson is null ? "@exit /b 3\r\n" : "@type \"%~dp0agents.json\"\r\n");
        var settings = JsonSerializer.Serialize(new { claude = stub.File("claude.cmd") });
        return await RunBundled("claude", "claude.ps1", hook, settings);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_counts_sessions_and_warns_when_one_is_blocked()
    {
        var outcome = HookResults.ForTile("interval", await RunClaude("interval", AgentsSample), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("Claude", outcome.Value!.Title);
        Assert.Equal("1 working · 2 idle · 1 blocked", outcome.Value.Subtitle);
        Assert.Equal("warning", outcome.Value.Icon!.Glyph);
        Assert.Equal("Generator (blocked)\nWidget (working)", outcome.Value.Tooltip);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_with_no_sessions_says_so()
    {
        var outcome = HookResults.ForTile("interval", await RunClaude("interval", "[]"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal("No sessions", outcome.Value!.Subtitle);
        Assert.Equal("check", outcome.Value.Icon!.Glyph);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_popup_lists_blocked_then_working_then_idle_sessions()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunClaude("flyout", AgentsSample), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(new[] { "Generator", "Widget", "Bundles", "burner" }, outcome.Value!.Rows.Select(r => r.Text)); // unnamed: folder name
        Assert.Equal(new[] { "blocked", "working", "idle", "idle" }, outcome.Value.Rows.Select(r => r.Value));
        Assert.Equal("open", Assert.Single(outcome.Value.Actions).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_failing_puts_the_tile_in_the_error_look()
    {
        var outcome = HookResults.ForTile("interval", await RunClaude("interval", null), TimeSpan.FromSeconds(30));

        Assert.True(outcome.Failed);
        Assert.Contains("exited with code 3", outcome.ErrorDetails);
    }
}
