using System.Security.Cryptography;
using System.Text;
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

        Assert.Equal(new[] { "aquarium", "battery", "claude-usage", "clipboard", "cozy-farm", "docker", "git", "linear", "media", "racers", "snake", "system", "todo", "uptime" }, kinds.Kinds.Keys.Order(StringComparer.Ordinal));
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
          "seven_day": { "utilization": {{week}}, "resets_at": "{{DateTimeOffset.UtcNow.AddDays(2).AddMinutes(-30):o}}" },
          "limits": [ { "kind": "weekly_scoped", "percent": 12, "scope": { "model": { "display_name": "Opus" } } } ] }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_shows_the_five_hour_limit_with_the_week_below()
    {
        var outcome = HookResults.ForTile("interval", await RunUsage("interval", UsageSample(42, 18)), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.StartsWith("5h 42%/60% · resets ", outcome.Value!.Title); // 3 of the 5 hours gone
        Assert.StartsWith("Week 18%/86% · resets ", outcome.Value.Subtitle); // day 6 of 7 begun
        Assert.Equal(0.42, outcome.Value.Progress!.Value, 3);
        Assert.Equal(0.60, outcome.Value.ProgressMark!.Value, 3);
        Assert.Equal(0.70, outcome.Value.ProgressForecast!.Value, 3); // 42% in 3 of 5 hours: 70% at the reset
        Assert.Contains("5 hours: 42% used (60% at an even pace)", outcome.Value.Tooltip);
        Assert.Equal(TileColor.Ok, outcome.Value.Icon!.Color);
        Assert.NotNull(outcome.Value.Image); // claude.png from the tile folder
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
        Assert.Equal(new[] { "5 hours", "At this rate", "Week", "At this rate", "Week, Opus" }, outcome.Value!.Rows.Select(r => r.Text));
        Assert.Equal("70% at reset", outcome.Value.Rows[1].Value);
        Assert.Equal("25% at reset", outcome.Value.Rows[3].Value); // 18% in 120.5 of 168 hours
        Assert.Equal("12%", outcome.Value.Rows[4].Value);
        Assert.Equal(new[] { "refresh", "open" }, outcome.Value.Actions.Select(a => a.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_says_when_a_limit_runs_out_at_this_rate()
    {
        // 90% in 3 of 5 hours: 100% 20 minutes from now, well before the reset.
        var popup = HookResults.ForFlyout("flyout", await RunUsage("flyout", UsageSample(90, 18)), TimeSpan.FromSeconds(30));
        var tile = HookResults.ForTile("interval", await RunUsage("interval", UsageSample(90, 18)), TimeSpan.FromSeconds(30));

        Assert.False(popup.Failed, $"{popup.ErrorMessage}\n{popup.ErrorDetails}");
        var at = DateTime.Now.AddMinutes(20);
        Assert.Matches(@"^runs out \d\d:\d\d", popup.Value!.Rows[1].Value);
        var shown = TimeSpan.Parse(popup.Value.Rows[1].Value!["runs out ".Length..]);
        Assert.True(Math.Abs((shown - at.TimeOfDay).TotalMinutes) is < 2 or > 1438, popup.Value.Rows[1].Value);
        Assert.Equal(1.0, tile.Value!.ProgressForecast!.Value, 3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_has_no_forecast_early_in_a_window()
    {
        var sample = $$"""{ "five_hour": { "utilization": 3, "resets_at": "{{DateTimeOffset.UtcNow.AddHours(4.8):o}}" } }""";
        var tile = HookResults.ForTile("interval", await RunUsage("interval", sample), TimeSpan.FromSeconds(30));
        var popup = HookResults.ForFlyout("flyout", await RunUsage("flyout", sample), TimeSpan.FromSeconds(30));

        Assert.False(tile.Failed, $"{tile.ErrorMessage}\n{tile.ErrorDetails}");
        Assert.Null(tile.Value!.ProgressForecast);
        Assert.Equal(new[] { "5 hours" }, popup.Value!.Rows.Select(r => r.Text));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claude_usage_without_plan_limits_says_why()
    {
        var outcome = HookResults.ForTile("interval", await RunUsage("interval", """{ "five_hour": null, "seven_day": null }"""), TimeSpan.FromSeconds(30));

        Assert.True(outcome.Failed);
        Assert.Contains("Pro and Max", outcome.ErrorMessage + outcome.ErrorDetails);
    }

    /// <summary>Runs docker.ps1 on sample `docker ps` lines ("down": the engine is not running) instead of docker.</summary>
    static async Task<ScriptResult> RunDocker(string hook, string sample)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("ps.txt"), sample);
        return await RunBundled("docker", "docker.ps1", hook,
            extraEnv: new Dictionary<string, string> { ["TILE_DOCKER_SAMPLE"] = dir.File("ps.txt") });
    }

    const string DockerSample = """
        {"Names":"web","State":"running","Status":"Up 2 hours"}
        {"Names":"api","State":"running","Status":"Up 2 hours (unhealthy)"}
        {"Names":"db","State":"exited","Status":"Exited (0) 5 hours ago"}
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Docker_counts_containers_and_flags_unhealthy_ones()
    {
        var outcome = HookResults.ForTile("interval", await RunDocker("interval", DockerSample), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("1 with problems · 2 running · 1 stopped", outcome.Value!.Subtitle);
        Assert.Equal(new TileIcon("package", TileColor.Warn), outcome.Value.Icon);
        Assert.NotNull(outcome.Value.Image); // docker.png from the tile folder, with an orange dot
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Docker_popup_lists_problems_first()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunDocker("flyout", DockerSample), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal(new[] { "api", "web", "db" }, outcome.Value!.Rows.Select(r => r.Text));
        Assert.Equal("open", Assert.Single(outcome.Value.Actions).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Docker_popup_groups_compose_projects_like_docker_desktop()
    {
        const string sample = """
            {"Names":"shop-web-1","State":"running","Status":"Up 1 hour","Labels":"com.docker.compose.project=shop,com.docker.compose.service=web"}
            {"Names":"shop-db-1","State":"exited","Status":"Exited (1) 2 hours ago","Labels":"com.docker.compose.project=shop"}
            {"Names":"redis","State":"running","Status":"Up 3 hours","Labels":""}
            """;

        var outcome = HookResults.ForFlyout("flyout", await RunDocker("flyout", sample), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        var indent = new string('\u2003', 2);
        Assert.Equal(new[] { "redis", "shop", indent + "web-1", indent + "db-1" }, outcome.Value!.Rows.Select(r => r.Text));
        Assert.Equal("1 running · 1 stopped", outcome.Value.Rows[1].Value);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Docker_not_running_is_a_normal_tile()
    {
        var outcome = HookResults.ForTile("interval", await RunDocker("interval", "down"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal("Not running", outcome.Value!.Subtitle);
    }

    /// <summary>Runs git.ps1 against <paramref name="repo"/>.</summary>
    static Task<ScriptResult> RunGit(string hook, string repo) =>
        RunBundled("git", "git.ps1", hook, JsonSerializer.Serialize(new { repo }));

    static void Git(string repo, string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{repo}\" {arguments}") { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(info)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Git_shows_the_branch_and_local_changes()
    {
        using var dir = new TempDir();
        var repo = dir.File("my-app");
        Directory.CreateDirectory(repo);
        Git(repo, "init -q -b main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
        Git(repo, "add a.txt");
        Git(repo, "-c user.name=t -c user.email=t@t commit -q -m first");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "new");

        var tile = HookResults.ForTile("interval", await RunGit("interval", repo), TimeSpan.FromSeconds(30));
        var popup = HookResults.ForFlyout("flyout", await RunGit("flyout", repo), TimeSpan.FromSeconds(30));

        Assert.False(tile.Failed, $"{tile.ErrorMessage}\n{tile.ErrorDetails}");
        Assert.Empty(tile.Warnings);
        Assert.Equal("my-app · main", tile.Value!.Title);
        Assert.Equal("1 changed, 1 new", tile.Value.Subtitle);
        Assert.Equal(new TileIcon("branch", TileColor.Accent), tile.Value.Icon);
        Assert.False(popup.Failed, $"{popup.ErrorMessage}\n{popup.ErrorDetails}");
        Assert.Contains(popup.Value!.Rows, r => r.Text == "first");
        Assert.Equal(new[] { "fetch", "folder" }, popup.Value.Actions.Select(a => a.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Git_without_a_repository_asks_for_one_and_fails_on_a_plain_folder()
    {
        using var dir = new TempDir();

        var unset = HookResults.ForTile("interval", await RunGit("interval", ""), TimeSpan.FromSeconds(30));
        var plain = HookResults.ForTile("interval", await RunGit("interval", dir.Path), TimeSpan.FromSeconds(30));

        Assert.Equal("Set a repository in Settings", unset.Value!.Subtitle);
        Assert.True(plain.Failed);
        Assert.Contains("is not a Git repository", plain.ErrorDetails);
    }

    /// <summary>Runs todo.ps1 on <paramref name="file"/>; action hooks get TILE_ACTION and TILE_INPUT.</summary>
    static Task<ScriptResult> RunTodo(string hook, string file, string action = "", string input = "", bool showDone = true) =>
        RunBundled("todo", "todo.ps1", hook,
            settingsJson: $$"""{ "file": "{{file.Replace('\\', '/')}}", "showDone": {{(showDone ? "true" : "false")}} }""",
            extraEnv: new Dictionary<string, string> { ["TILE_ACTION"] = action, ["TILE_INPUT"] = input });

    static string TodoHash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    static async Task TodoAction(string file, string action, string input = "")
    {
        var result = await RunTodo("action", file, action, input);
        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("", result.StdOut.Trim()); // prints nothing, so the host refreshes
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_counts_open_tasks_and_shows_the_first()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "# Work\r\n- [ ] Write spec\r\n- [x] Old\r\n* [ ] Ship\r\n");

        var outcome = HookResults.ForTile("interval", await RunTodo("interval", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("2 to do", outcome.Value!.Title);
        Assert.Equal("Write spec", outcome.Value.Subtitle);
        Assert.Equal(new TileIcon("check", TileColor.Accent), outcome.Value.Icon);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_without_a_file_asks_for_a_task()
    {
        using var dir = new TempDir();

        var outcome = HookResults.ForTile("interval", await RunTodo("interval", dir.File("none.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal("All done", outcome.Value!.Title);
        Assert.Equal("Add a task", outcome.Value.Subtitle);
        Assert.Equal(TileColor.Ok, outcome.Value.Icon!.Color);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_add_creates_the_file_and_its_folder()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.File("sub"), "deeper", "todo.md");

        await TodoAction(file, "add", "  Buy\nmilk  ");

        Assert.Equal("- [ ] Buy milk\r\n", File.ReadAllText(file));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_add_goes_after_the_last_task_and_keeps_LF()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "# A\n- [ ] One\n\nNotes\n");

        await TodoAction(dir.File("todo.md"), "add", "Two");

        Assert.Equal("# A\n- [ ] One\n- [ ] Two\n\nNotes\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_toggle_flips_the_line_named_by_number_and_hash()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "- [ ] One\n- [ ] Two\n");

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] One\n- [ ] Two\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_toggle_finds_the_task_by_text_after_an_edit_and_ignores_a_gone_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "- [ ] Zero\n- [ ] One\n- [ ] Two\n"); // "Zero" was added after the popup opened

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] Zero\n- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));

        await TodoAction(dir.File("todo.md"), $"toggle:1:{TodoHash("Nope")}");
        Assert.Equal("- [ ] Zero\n- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_clear_removes_done_tasks_and_keeps_the_rest_BOM_and_ending()
    {
        using var dir = new TempDir();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(dir.File("todo.md"), [.. bom, .. Encoding.UTF8.GetBytes("# H\r\n- [x] A\r\n- [ ] B\r\n- [X] C")]);

        await TodoAction(dir.File("todo.md"), "clear");

        Assert.Equal([.. bom, .. Encoding.UTF8.GetBytes("# H\r\n- [ ] B")], File.ReadAllBytes(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_clear_of_a_done_only_file_leaves_an_empty_file()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "- [x] A\n- [X] B\n");

        await TodoAction(dir.File("todo.md"), "clear");

        Assert.Empty(File.ReadAllBytes(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_popup_lists_open_tasks_then_up_to_ten_done()
    {
        using var dir = new TempDir();
        var lines = new List<string> { "- [ ] Open one" };
        for (var i = 1; i <= 12; i++) lines.Add($"- [x] Done {i}");
        File.WriteAllText(dir.File("todo.md"), string.Join("\n", lines) + "\n");

        var outcome = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        var f = outcome.Value!;
        Assert.Equal("To do", f.Title);
        Assert.Equal("todo.md", f.Subtitle);
        Assert.True(f.StayOpen);
        Assert.Equal(new FlyoutInput("add", "Add a task"), f.Input);
        Assert.Equal(11, f.Rows.Count);
        Assert.Equal(("Open one", false, $"toggle:1:{TodoHash("Open one")}"), (f.Rows[0].Text, f.Rows[0].Check!.Value, f.Rows[0].Action!));
        Assert.Equal(("Done 1", true), (f.Rows[1].Text, f.Rows[1].Check!.Value));
        Assert.Equal(new[] { "clear", "open" }, f.Actions.Select(a => a.Id));

        var hidden = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md"), showDone: false), TimeSpan.FromSeconds(30));
        Assert.Single(hidden.Value!.Rows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_popup_with_no_tasks_says_so()
    {
        using var dir = new TempDir();

        var outcome = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.Equal("No tasks yet", Assert.Single(outcome.Value!.Rows).Text);
        Assert.Null(outcome.Value.Rows[0].Action);
        Assert.Equal(new[] { "open" }, outcome.Value.Actions.Select(a => a.Id));
    }

    const string LinearIssues = """
        { "data": { "organization": { "urlKey": "acme" }, "viewer": { "assignedIssues": { "nodes": [
          { "identifier": "ENG-7", "title": "Old todo", "url": "https://linear.app/acme/issue/ENG-7/old", "priority": 0,
            "updatedAt": "2026-11-01T10:00:00Z", "state": { "name": "Todo", "type": "unstarted" } },
          { "identifier": "ENG-9", "title": "Urgent todo", "url": "https://linear.app/acme/issue/ENG-9/urgent", "priority": 1,
            "updatedAt": "2026-11-02T10:00:00Z", "state": { "name": "Todo", "type": "unstarted" } },
          { "identifier": "ENG-3", "title": "Fix login", "url": "https://linear.app/acme/issue/ENG-3/fix", "priority": 3,
            "updatedAt": "2026-11-03T10:00:00Z", "state": { "name": "In Progress", "type": "started" } }
        ] } } } }
        """;

    const string LinearRejected = """{ "errors": [ { "message": "Authentication required", "extensions": { "code": "AUTHENTICATION_ERROR" } } ] }""";

    /// <summary>
    /// Runs linear.ps1 with dir\key.txt standing in for Credential Manager and <paramref name="response"/> for the API,
    /// so no test reaches the real credential store or the network.
    /// </summary>
    static Task<ScriptResult> RunLinear(TempDir dir, string hook, string response = LinearIssues, string action = "", string input = "")
    {
        File.WriteAllText(dir.File("response.json"), response);
        return RunBundled("linear", "linear.ps1", hook, extraEnv: new Dictionary<string, string>
        {
            ["TILE_LINEAR_KEYFILE"] = dir.File("key.txt"), ["TILE_LINEAR_RESPONSE"] = dir.File("response.json"),
            ["TILE_ACTION"] = action, ["TILE_INPUT"] = input,
        });
    }

    static TempDir LinearDirWithKey()
    {
        var dir = new TempDir();
        File.WriteAllText(dir.File("key.txt"), "lin_api_saved");
        return dir;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_counts_in_progress_and_shows_the_top_issue()
    {
        using var dir = LinearDirWithKey();

        var outcome = HookResults.ForTile("interval", await RunLinear(dir, "interval"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("1 in progress", outcome.Value!.Title);
        Assert.Equal("ENG-3 Fix login", outcome.Value.Subtitle);
        Assert.Equal(new TileIcon("activity", TileColor.Accent), outcome.Value.Icon);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_popup_orders_issues_and_links_each_one()
    {
        using var dir = LinearDirWithKey();

        var outcome = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        var f = outcome.Value!;
        Assert.Equal("Linear", f.Title);
        Assert.Equal("1 in progress · 2 to do", f.Subtitle);
        Assert.Equal(new[] { "ENG-3 Fix login", "ENG-9 Urgent todo", "ENG-7 Old todo" }, f.Rows.Select(r => r.Text));
        Assert.Equal(new[] { "In Progress", "Todo", "Todo" }, f.Rows.Select(r => r.Value));
        Assert.Equal("open:https://linear.app/acme/issue/ENG-3/fix", f.Rows[0].Action);
        Assert.Equal(TileColor.Accent, f.Rows[0].Icon!.Color);
        Assert.Equal(TileColor.Neutral, f.Rows[1].Icon!.Color);
        Assert.Equal(new[] { "forget", "open:https://linear.app/acme/my-issues/assigned" }, f.Actions.Select(a => a.Id));
        Assert.Null(f.Input);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_without_a_key_offers_setup()
    {
        using var dir = new TempDir();

        var tile = HookResults.ForTile("interval", await RunLinear(dir, "interval"), TimeSpan.FromSeconds(30));
        Assert.False(tile.Failed, $"{tile.ErrorMessage}\n{tile.ErrorDetails}");
        Assert.Equal(("Linear", "Click to set up"), (tile.Value!.Title, tile.Value.Subtitle));

        var popup = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout"), TimeSpan.FromSeconds(30));
        Assert.Empty(popup.Warnings);
        var f = popup.Value!;
        Assert.Equal("Paste your personal API key", f.Subtitle);
        Assert.Equal(new FlyoutInput("setkey", "Personal API key", Secret: true), f.Input);
        Assert.True(f.StayOpen);
        Assert.Equal(new[] { "open:https://linear.app/settings/account/security" }, f.Actions.Select(a => a.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_setkey_stores_a_key_Linear_accepts()
    {
        using var dir = new TempDir();

        var result = await RunLinear(dir, "action", action: "setkey", input: " lin_api_new ");

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("", result.StdOut.Trim());
        Assert.Equal("lin_api_new", File.ReadAllText(dir.File("key.txt")));
        var popup = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout"), TimeSpan.FromSeconds(30));
        Assert.Null(popup.Value!.Input); // the issue list now
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_setkey_keeps_out_a_rejected_key_and_says_why_once()
    {
        using var dir = new TempDir();

        var result = await RunLinear(dir, "action", LinearRejected, "setkey", "lin_api_bad");

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.DoesNotContain("lin_api_bad", result.StdOut + result.StdErr);
        Assert.False(File.Exists(dir.File("key.txt")));
        var first = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout"), TimeSpan.FromSeconds(30));
        Assert.Equal("Key rejected by Linear", first.Value!.Subtitle);
        var second = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout"), TimeSpan.FromSeconds(30));
        Assert.Equal("Paste your personal API key", second.Value!.Subtitle);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_forget_removes_the_key()
    {
        using var dir = LinearDirWithKey();

        var result = await RunLinear(dir, "action", action: "forget");

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.False(File.Exists(dir.File("key.txt")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_open_refuses_a_link_outside_Linear()
    {
        using var dir = LinearDirWithKey();

        var result = await RunLinear(dir, "action", action: "open:https://example.com/x");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Not a Linear link", result.StdErr);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_with_a_rejected_saved_key_offers_setup_again()
    {
        using var dir = LinearDirWithKey();

        var tile = HookResults.ForTile("interval", await RunLinear(dir, "interval", LinearRejected), TimeSpan.FromSeconds(30));
        Assert.False(tile.Failed, $"{tile.ErrorMessage}\n{tile.ErrorDetails}");
        Assert.Equal(("Linear", "Key rejected"), (tile.Value!.Title, tile.Value.Subtitle));

        var popup = HookResults.ForFlyout("flyout", await RunLinear(dir, "flyout", LinearRejected), TimeSpan.FromSeconds(30));
        Assert.Equal("Linear rejected the saved key", popup.Value!.Subtitle);
        Assert.Equal("setkey", popup.Value.Input!.Action);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Linear_error_without_a_cache_fails_with_the_message()
    {
        using var dir = LinearDirWithKey();
        const string limited = """{ "errors": [ { "message": "Rate limit exceeded", "extensions": { "code": "RATELIMITED" } } ] }""";

        var outcome = HookResults.ForTile("interval", await RunLinear(dir, "interval", limited), TimeSpan.FromSeconds(30));

        Assert.True(outcome.Failed);
        Assert.Contains("Linear: Rate limit exceeded", $"{outcome.ErrorMessage} {outcome.ErrorDetails}");
    }
}
