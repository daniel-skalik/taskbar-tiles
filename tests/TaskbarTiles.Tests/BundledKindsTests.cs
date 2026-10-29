using System.Text.Json;
using System.IO;
using TaskbarTiles.Contracts;
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

        Assert.Equal(new[] { "battery", "claude", "claude-usage", "media", "ping", "uptime" }, kinds.Kinds.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(kinds.FoldersWithoutTileJson);
        foreach (var kind in kinds.Kinds.Values)
        {
            Assert.True(kind.Error is null, $"{kind.Id}: {kind.Error}");
            Assert.False(string.IsNullOrWhiteSpace(kind.Description), $"{kind.Id} has no description");
            if (kind.Connector == TileKind.ScriptConnectorId) Assert.True(builtIn.Connectors.ContainsKey(TileKind.ScriptConnectorId));
            else Assert.Equal(kind.Connector, kind.Plugin!.Id);
        }
        Assert.Equal("media", kinds.Kinds["media"].Plugin!.Id);
        Assert.Equal("battery", kinds.Kinds["battery"].Plugin!.Id);
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

    static async Task<ScriptResult> RunBundled(string kind, string script, string hook, string settingsJson = "{}",
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var command = new CommandResolver(Path.Combine(BundledTiles, kind), ExecutableLocator.PowerShell()).Resolve(script);
        var env = new Dictionary<string, string>
        {
            ["TILE_ID"] = kind, ["TILE_HOOK"] = hook, ["TILE_ACTION"] = "", ["TILE_SETTINGS_JSON"] = settingsJson,
        };
        foreach (var (key, value) in extraEnv ?? new Dictionary<string, string>()) env[key] = value;
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
        Assert.Equal("1 blocked · 1 working · 2 idle", outcome.Value.Subtitle);
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

    /// <summary>Runs claude-usage.ps1 on <paramref name="responseJson"/> instead of calling the usage endpoint.</summary>
    static async Task<ScriptResult> RunUsage(string hook, string responseJson)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("usage.json"), responseJson);
        return await RunBundled("claude-usage", "claude-usage.ps1", hook,
            extraEnv: new Dictionary<string, string> { ["TILE_CLAUDE_USAGE_RESPONSE"] = dir.File("usage.json") });
    }

    static string UsageSample(double hour, double week) => $$"""
        { "five_hour": { "utilization": {{hour}}, "resets_at": "{{DateTimeOffset.UtcNow.AddHours(2):o}}" },
          "seven_day": { "utilization": {{week}}, "resets_at": "2030-01-06T06:00:00+00:00" },
          "limits": [ { "kind": "weekly_scoped", "percent": 12, "scope": { "model": { "display_name": "Opus" } } } ] }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_shows_the_five_hour_limit_with_the_week_below()
    {
        var outcome = HookResults.ForTile("interval", await RunUsage("interval", UsageSample(42, 18)), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.StartsWith("5h 42% · resets ", outcome.Value!.Title);
        Assert.StartsWith("Week 18% · resets ", outcome.Value.Subtitle);
        Assert.Equal(0.42, outcome.Value.Progress!.Value, 3);
        Assert.Equal(TileColor.Ok, outcome.Value.Icon!.Color);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(85, 10, TileColor.Warn)]
    [InlineData(10, 96, TileColor.Bad)]
    public async Task Claude_usage_turns_orange_then_red(double hour, double week, TileColor expected)
    {
        var outcome = HookResults.ForTile("interval", await RunUsage("interval", UsageSample(hour, week)), TimeSpan.FromSeconds(30));

        Assert.Equal(expected, outcome.Value!.Icon!.Color);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_popup_lists_each_limit()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunUsage("flyout", UsageSample(42, 18)), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal(new[] { "5 hours", "Week", "Week, Opus" }, outcome.Value!.Rows.Select(r => r.Text));
        Assert.Equal("12%", outcome.Value.Rows[2].Value);
        Assert.Equal("open", Assert.Single(outcome.Value.Actions).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_without_plan_limits_says_why()
    {
        var outcome = HookResults.ForTile("interval", await RunUsage("interval", """{ "five_hour": null, "seven_day": null }"""), TimeSpan.FromSeconds(30));

        Assert.True(outcome.Failed);
        Assert.Contains("Pro and Max", outcome.ErrorMessage + outcome.ErrorDetails);
    }
}
