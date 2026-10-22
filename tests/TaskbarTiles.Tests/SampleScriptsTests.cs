using System.IO;
using TaskbarTiles.Host;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class SampleScriptsTests
{
    [Fact]
    public void Copies_the_samples_when_the_scripts_folder_is_missing()
    {
        using var source = new TempDir();
        using var data = new TempDir();
        File.WriteAllText(source.File("a.ps1"), "1");
        File.WriteAllText(source.File("b.ps1"), "2");
        var target = Path.Combine(data.Path, "scripts");

        Assert.True(SampleScripts.InstallIfAbsent(source.Path, target, new CollectingLog()));

        Assert.Equal("1", File.ReadAllText(Path.Combine(target, "a.ps1")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(target, "b.ps1")));
    }

    [Fact]
    public void Leaves_an_existing_scripts_folder_alone()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        File.WriteAllText(source.File("a.ps1"), "1");

        Assert.False(SampleScripts.InstallIfAbsent(source.Path, target.Path, new CollectingLog()));

        Assert.Empty(Directory.GetFiles(target.Path));
    }

    [Fact]
    public void Existing_scripts_are_untouched_and_no_temp_folder_is_left()
    {
        using var source = new TempDir();
        using var data = new TempDir();
        File.WriteAllText(source.File("a.ps1"), "new");
        var target = Path.Combine(data.Path, "scripts");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "mine.ps1"), "mine");

        Assert.False(SampleScripts.InstallIfAbsent(source.Path, target, new CollectingLog()));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "mine.ps1")));
        Assert.Single(Directory.GetFileSystemEntries(target));
        Assert.Single(Directory.GetFileSystemEntries(data.Path));
    }

    [Fact]
    public void A_failed_copy_leaves_no_scripts_folder_and_a_later_install_works()
    {
        using var source = new TempDir();
        using var data = new TempDir();
        File.WriteAllText(source.File("a.ps1"), "1");
        File.WriteAllText(source.File("b.ps1"), "2");
        var target = Path.Combine(data.Path, "scripts");
        var log = new CollectingLog();

        bool installed;
        using (new FileStream(source.File("b.ps1"), FileMode.Open, FileAccess.Read, FileShare.None)) // unreadable: the copy fails halfway
            installed = SampleScripts.InstallIfAbsent(source.Path, target, log);

        Assert.False(installed);
        Assert.Empty(Directory.GetFileSystemEntries(data.Path));
        Assert.True(SampleScripts.InstallIfAbsent(source.Path, target, new CollectingLog()));
        Assert.Equal("2", File.ReadAllText(Path.Combine(target, "b.ps1")));
    }

    [Fact]
    public void Missing_samples_are_not_an_error()
    {
        using var data = new TempDir();
        var target = Path.Combine(data.Path, "scripts");

        Assert.False(SampleScripts.InstallIfAbsent(Path.Combine(data.Path, "nope"), target, new CollectingLog()));

        Assert.False(Directory.Exists(target));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TaskbarTiles.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TaskbarTiles.sln not found above the test output folder.");
    }

    static async Task<ScriptResult> RunSample(string script, string hook, string settingsJson = "{}")
    {
        var command = new CommandResolver(RepoRoot(), ExecutableLocator.PowerShell()).Resolve("scripts/" + script);
        var env = new Dictionary<string, string>
        {
            ["TILE_ID"] = "sample", ["TILE_HOOK"] = hook, ["TILE_ACTION"] = "", ["TILE_SETTINGS_JSON"] = settingsJson,
        };
        return await new ProcessScriptRunner().RunAsync(command, env, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_sample_prints_a_clock_tile()
    {
        var outcome = HookResults.ForTile("interval", await RunSample("uptime.ps1", "interval"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Matches(@"^\d{2}:\d{2}$", outcome.Value!.Title);
        Assert.StartsWith("up ", outcome.Value.Subtitle);
        Assert.Equal("clock", outcome.Value.Icon!.Glyph);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_sample_prints_a_popup()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunSample("uptime.ps1", "flyout"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(new[] { "Time", "Booted", "Uptime" }, outcome.Value!.Rows.Select(r => r.Text));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_sample_prints_a_status_tile_with_a_button()
    {
        var outcome = HookResults.ForTile("interval", await RunSample("ping.ps1", "interval", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("127.0.0.1", outcome.Value!.Title);
        Assert.EndsWith(" ms", outcome.Value.Subtitle);
        Assert.Equal("ping", Assert.Single(outcome.Value.Buttons).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_sample_prints_a_popup_with_an_action()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunSample("ping.ps1", "flyout", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(4, outcome.Value!.Rows.Count);
        Assert.Equal("ping", Assert.Single(outcome.Value.Actions).Id);
    }
}
