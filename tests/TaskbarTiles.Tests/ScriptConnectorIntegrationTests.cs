using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

[Trait("Category", "Integration")]
public class ScriptConnectorIntegrationTests : IDisposable
{
    const int Slow = 20_000; // PowerShell can take a few seconds to start on a busy machine
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    TestTileContext Context(string settingsJson) => new("t1", settingsJson, _dir.Path);

    [Fact]
    public async Task Interval_hook_updates_the_tile_repeatedly()
    {
        ScriptFiles.Write(_dir, "scripts/tick.ps1", "@{ title = 'tick'; subtitle = [string](Get-Date).Ticks } | ConvertTo-Json -Compress");
        var ctx = Context("""{ "hooks": { "interval": { "every": "1s", "run": "scripts/tick.ps1" } } }""");
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count >= 2, Slow);
        Assert.All(ctx.Updates, u => Assert.Equal("tick", u.Title));
        Assert.NotEqual(ctx.Updates[0].Subtitle, ctx.Updates[1].Subtitle);
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Action_hook_gets_its_id_and_updates_the_tile()
    {
        ScriptFiles.Write(_dir, "scripts/hello.ps1",
            "@{ title = \"$($env:TILE_ACTION) from $($env:TILE_ID)\"; subtitle = $env:TILE_HOOK } | ConvertTo-Json -Compress");
        var ctx = Context("""{ "hooks": { "action:hello": { "run": "scripts/hello.ps1" } } }""");
        await using var c = new ScriptConnector();
        await c.StartAsync(ctx, CancellationToken.None);

        await c.OnActionAsync("hello", CancellationToken.None);

        var last = ctx.Updates[^1];
        Assert.Equal("hello from t1", last.Title);
        Assert.Equal("action", last.Subtitle);
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Scripts_run_with_the_tile_folder_as_working_directory()
    {
        using var data = new TempDir();
        ScriptFiles.Write(_dir, "where.ps1", "@{ title = (Get-Location).Path } | ConvertTo-Json -Compress");
        var ctx = new TestTileContext("t1", """{ "hooks": { "startup": "where.ps1" } }""", data.Path, _dir.Path);
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count == 1, Slow);
        Assert.Equal(_dir.Path, ctx.Updates[0].Title, ignoreCase: true);
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Settings_reach_the_script()
    {
        ScriptFiles.Write(_dir, "scripts/label.ps1",
            "$s = $env:TILE_SETTINGS_JSON | ConvertFrom-Json\n@{ title = $s.label } | ConvertTo-Json -Compress");
        var ctx = Context("""{ "label": "from settings", "hooks": { "startup": "scripts/label.ps1" } }""");
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count == 1, Slow);
        Assert.Equal("from settings", ctx.Updates[0].Title);
    }

    [Fact]
    public async Task Watch_hook_streams_lines_and_restarts_after_exit()
    {
        ScriptFiles.Write(_dir, "scripts/watch.ps1", """
            Write-Output '{"title":"w1"}'
            Start-Sleep -Milliseconds 200
            Write-Output '{"title":"w2"}'
            """);
        var ctx = Context("""{ "hooks": { "watch": { "run": "scripts/watch.ps1" } } }""");
        await using var c = new ScriptConnector(new ProcessScriptRunner(), ExecutableLocator.PowerShell(),
            new BackoffSchedule([TimeSpan.FromMilliseconds(200)]));

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count >= 3, Slow); // w1, w2, then w1 again from the restarted script
        Assert.Equal(new[] { "w1", "w2", "w1" }, ctx.Updates.Take(3).Select(u => u.Title));
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Hung_script_is_killed_at_its_timeout_and_reported()
    {
        ScriptFiles.Write(_dir, "scripts/hang.ps1", "Start-Sleep -Seconds 60");
        var ctx = Context("""{ "hooks": { "interval": { "every": "1h", "run": "scripts/hang.ps1", "timeout": "1s" } } }""");
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Errors.Count == 1, Slow);
        Assert.Equal("interval: timed out after 1 s", ctx.Errors[0].Message);
        Assert.Empty(ctx.Updates);
    }

    [Fact]
    public async Task Non_zero_exit_shows_stderr()
    {
        ScriptFiles.Write(_dir, "scripts/fail.ps1", """
            [Console]::Error.WriteLine('it broke')
            exit 3
            """);
        var ctx = Context("""{ "hooks": { "interval": { "every": "1h", "run": "scripts/fail.ps1" } } }""");
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Errors.Count == 1, Slow);
        Assert.Equal("interval: exited with code 3", ctx.Errors[0].Message);
        Assert.Contains("it broke", ctx.Errors[0].Details);
    }
}
