using System.IO;
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

    [Fact]
    public async Task A_newline_free_flood_is_bounded_and_marked_truncated()
    {
        var command = PowerShellScript("flood.ps1", """
            $s = 'x' * 1048576
            for ($i = 0; $i -lt 5; $i++) { [Console]::Out.Write($s) }
            """);
        var watch = Stopwatch.StartNew();

        var r = await _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(r.TimedOut);
        Assert.InRange(r.StdOut.Length, 1, 1024 * 1024 + 1024);
        Assert.Contains("(output truncated)", r.StdErr);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Stderr_keeps_its_tail_when_over_the_cap()
    {
        var command = PowerShellScript("noisy.ps1", """
            for ($i = 1; $i -le 2000; $i++) { [Console]::Error.WriteLine("error line $i padding padding padding padding") }
            [Console]::Error.WriteLine('THE-LAST-LINE')
            exit 3
            """);

        var run = await _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(30), CancellationToken.None);
        var lines = await _runner.RunLinesAsync(command, NoEnv, _ => { }, CancellationToken.None);

        foreach (var r in new[] { run, lines })
        {
            Assert.Contains("THE-LAST-LINE", r.StdErr);
            Assert.DoesNotContain("error line 1 ", r.StdErr);
            Assert.EndsWith("(output truncated)", r.StdErr);
            Assert.InRange(r.StdErr.Length, 1, 16 * 1024 + 100);
        }
    }

    [Fact]
    public async Task RunLines_caps_an_over_long_line_and_continues()
    {
        var command = PowerShellScript("long.ps1", """
            $s = 'y' * 1048576
            for ($i = 0; $i -lt 3; $i++) { [Console]::Out.Write($s) }
            [Console]::Out.WriteLine()
            [Console]::Out.WriteLine('after')
            """);
        var lines = new List<string>();

        var r = await _runner.RunLinesAsync(command, NoEnv, line => { lock (lines) lines.Add(line); }, CancellationToken.None);

        Assert.Equal(2, lines.Count);
        Assert.InRange(lines[0].Length, 1, 1024 * 1024 + 1024);
        Assert.Equal("after", lines[1]);
        Assert.Contains("(output truncated)", r.StdErr);
    }

    [Fact]
    public async Task RunLines_survives_a_throwing_callback_and_reports_it()
    {
        var command = PowerShellScript("many.ps1", """
            1..200 | ForEach-Object { Write-Output "line $_" }
            """);
        var calls = 0;

        var r = await _runner.RunLinesAsync(command, NoEnv, _ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("handler broke"); },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(200, calls);
        Assert.Contains("handler broke", r.StartError);
    }

    [Fact]
    public async Task RunLines_cancellation_wins_over_a_callback_error()
    {
        var command = PowerShellScript("one.ps1", """
            Write-Output 'go'
            Start-Sleep -Seconds 30
            """);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunLinesAsync(command, NoEnv, _ => { cts.CancelAfter(300); throw new InvalidOperationException("x"); }, cts.Token));
    }

    [Fact]
    public async Task An_invalid_timeout_does_not_leave_a_process_behind()
    {
        var command = PowerShellScript("slow.ps1", "Start-Sleep -Seconds 30");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _runner.RunAsync(command, NoEnv, TimeSpan.FromMilliseconds(-5), CancellationToken.None));
    }

    [Fact]
    public async Task A_timeout_that_cannot_be_scheduled_is_rejected_before_starting()
    {
        var marker = Path.Combine(Path.GetTempPath(), "tt-marker-" + Guid.NewGuid().ToString("N"));
        var command = PowerShellScript("slow2.ps1", $"Set-Content -Path '{marker}' -Value x");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _runner.RunAsync(command, NoEnv, TimeSpan.FromDays(60), CancellationToken.None));

        await Task.Delay(3000); // a process started anyway would have written the marker by now
        Assert.False(File.Exists(marker));
    }
}
