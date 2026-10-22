using System.Diagnostics;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

[Trait("Category", "Integration")]
public class ProcessScriptRunnerTests : IDisposable
{
    static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();
    readonly TempDir _dir = new();
    readonly ProcessScriptRunner _runner = new();

    ScriptCommand PowerShellScript(string name, string body) =>
        new CommandResolver(_dir.Path, ExecutableLocator.PowerShell()).Resolve(ScriptFiles.Write(_dir, name, body));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Captures_stdout_stderr_and_exit_code()
    {
        var command = PowerShellScript("a.ps1", """
            Write-Output '{"title":"hi"}'
            [Console]::Error.WriteLine('careful')
            exit 3
            """);

        var r = await _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(3, r.ExitCode);
        Assert.Equal("{\"title\":\"hi\"}", r.StdOut.Trim());
        Assert.Equal("careful", r.StdErr.Trim());
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Passes_the_environment()
    {
        var command = PowerShellScript("env.ps1", "Write-Output \"$($env:TILE_ID)|$($env:TILE_HOOK)\"");

        var r = await _runner.RunAsync(command, new Dictionary<string, string> { ["TILE_ID"] = "abc", ["TILE_HOOK"] = "interval" },
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("abc|interval", r.StdOut.Trim());
    }

    [Fact]
    public async Task Timeout_kills_the_script_and_says_so()
    {
        var command = PowerShellScript("slow.ps1", "Start-Sleep -Seconds 30");
        var watch = Stopwatch.StartNew();

        var r = await _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_kills_the_script_and_throws()
    {
        var command = PowerShellScript("slow.ps1", "Start-Sleep -Seconds 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(60), cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task RunLines_reports_each_line_and_the_exit()
    {
        var command = PowerShellScript("w.ps1", """
            Write-Output '{"title":"one"}'
            Start-Sleep -Milliseconds 300
            Write-Output '{"title":"two"}'
            [Console]::Error.WriteLine('bye')
            exit 4
            """);
        var lines = new List<string>();

        var r = await _runner.RunLinesAsync(command, NoEnv, line => { lock (lines) lines.Add(line); }, CancellationToken.None);

        Assert.Equal(new[] { "{\"title\":\"one\"}", "{\"title\":\"two\"}" }, lines);
        Assert.Equal(4, r.ExitCode);
        Assert.Contains("bye", r.StdErr);
    }

    [Fact]
    public async Task Missing_executable_comes_back_as_a_start_error_not_an_exception()
    {
        var command = new ScriptCommand("tt-does-not-exist-4711.exe", "", _dir.Path);

        var run = await _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(5), CancellationToken.None);
        var lines = await _runner.RunLinesAsync(command, NoEnv, _ => { }, CancellationToken.None);

        foreach (var r in new[] { run, lines })
        {
            Assert.False(string.IsNullOrWhiteSpace(r.StartError));
            Assert.Contains("tt-does-not-exist-4711.exe", r.StartError);
            Assert.Equal(-1, r.ExitCode);
            Assert.False(r.TimedOut);
        }
    }
}
