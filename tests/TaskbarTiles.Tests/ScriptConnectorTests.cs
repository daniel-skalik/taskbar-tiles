using System.ComponentModel;
using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ScriptConnectorTests
{
    sealed class FakeRunner : IScriptRunner
    {
        public sealed record Call(ScriptCommand Command, IReadOnlyDictionary<string, string> Env, TimeSpan? Limit);

        readonly List<Call> _calls = [];
        public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }
        public IReadOnlyList<string> Hooks => Calls.Select(c => c.Env["TILE_HOOK"]).ToList();

        public Func<Call, CancellationToken, Task<ScriptResult>> OnRun { get; set; } = (_, _) => Task.FromResult(Ok(""));

        public Func<Call, Action<string>, CancellationToken, Task<ScriptResult>> OnRunLines { get; set; } = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("");
        };

        public static ScriptResult Ok(string stdout) => new(0, stdout, "");

        public Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
        {
            var call = new Call(command, environment, timeout);
            lock (_calls) _calls.Add(call);
            return OnRun(call, ct);
        }

        public Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct)
        {
            var call = new Call(command, environment, null);
            lock (_calls) _calls.Add(call);
            return OnRunLines(call, onLine, ct);
        }
    }

    static ScriptConnector Create(FakeRunner runner) => new(runner, "pwsh.exe", new BackoffSchedule([TimeSpan.FromMilliseconds(20)]));

    [Fact]
    public async Task Invalid_settings_report_an_error_and_run_nothing()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "run": "a.ps1" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);
        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        var error = Assert.Single(ctx.Errors);
        Assert.Equal("Invalid script settings", error.Message);
        Assert.Contains("every", error.Details);
        Assert.Null(await c.GetFlyoutAsync(CancellationToken.None));
        await Task.Delay(100);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Interval_runs_at_start_and_updates_the_tile()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("""{ "title": "42" }""")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "scripts/a.ps1" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count == 1);
        Assert.Equal("42", ctx.Updates[0].Title);
        var call = runner.Calls[0];
        Assert.Equal("pwsh.exe", call.Command.FileName);
        Assert.Contains(Path.GetFullPath(Path.Combine(ctx.ConfigDirectory, "scripts", "a.ps1")), call.Command.Arguments);
        Assert.Equal("interval", call.Env["TILE_HOOK"]);
        Assert.Equal("t1", call.Env["TILE_ID"]);
        Assert.Equal("", call.Env["TILE_ACTION"]);
        Assert.Equal(ctx.Settings.GetRawText(), call.Env["TILE_SETTINGS_JSON"]);
        Assert.Equal(ScriptSettings.DefaultTimeout, call.Limit);
    }

    [Fact]
    public async Task Run_paths_and_the_working_directory_resolve_against_the_tile_folder()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "ping.ps1" } }""",
            configDirectory: @"C:\data", tileDirectory: @"C:\data\tiles\ping");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Contains("\"C:\\data\\tiles\\ping\\ping.ps1\"", call.Command.Arguments);
        Assert.Equal(@"C:\data\tiles\ping", call.Command.WorkingDirectory);
    }

    [Fact]
    public async Task Startup_runs_before_the_first_interval_run()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "startup": "s.ps1", "interval": { "every": "1h", "run": "i.ps1", "timeout": "5s" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => runner.Calls.Count == 2);
        Assert.Equal(new[] { "startup", "interval" }, runner.Hooks);
        Assert.Equal(TimeSpan.FromSeconds(5), runner.Calls[1].Limit);
    }

    [Fact]
    public async Task Non_zero_exit_reports_the_error_with_stderr()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(new ScriptResult(3, "", "it broke")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Errors.Count == 1);
        Assert.Equal(("interval: exited with code 3", "it broke"), ctx.Errors[0]);
        Assert.Empty(ctx.Updates);
        Assert.Contains(ctx.Logged.Snapshot(), m => m.StartsWith("ERROR interval: exited with code 3"));
    }

    [Fact]
    public async Task Invalid_json_reports_an_error()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("not json")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Errors.Count == 1);
        Assert.Equal("interval: invalid output", ctx.Errors[0].Message);
        Assert.EndsWith("not json", ctx.Errors[0].Details);
    }

    [Fact]
    public async Task Script_that_cannot_start_is_reported()
    {
        var runner = new FakeRunner { OnRun = (_, _) => throw new Win32Exception(2, "The system cannot find the file specified.") };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Errors.Count == 1);
        Assert.Equal("interval: could not start the script", ctx.Errors[0].Message);
        Assert.Contains("cannot find the file", ctx.Errors[0].Details);
    }

    [Fact]
    public async Task Click_only_tile_shows_its_id_and_runs_the_click_hook()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("""{ "title": "clicked" }""")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "c.ps1" } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);
        Assert.Equal("t1", Assert.Single(ctx.Updates).Title);

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        Assert.Equal("click", Assert.Single(runner.Calls).Env["TILE_HOOK"]);
        Assert.Equal("clicked", ctx.Updates[^1].Title);
    }

    [Fact]
    public async Task Action_runs_its_hook_with_TILE_ACTION()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("""{ "title": "done" }""")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "action:rerun": "scripts/rerun.ps1 --fast" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        await c.OnActionAsync("rerun", CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("action", call.Env["TILE_HOOK"]);
        Assert.Equal("rerun", call.Env["TILE_ACTION"]);
        Assert.EndsWith("rerun.ps1\" --fast", call.Command.Arguments);
        Assert.Equal("done", ctx.Updates[^1].Title);
    }

    [Fact]
    public async Task Action_without_output_refreshes_the_interval_hook()
    {
        var firstRunDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner
        {
            OnRun = (call, _) =>
            {
                var result = FakeRunner.Ok(call.Env["TILE_HOOK"] == "action" ? "" : """{ "title": "status" }""");
                firstRunDone.TrySetResult();
                return Task.FromResult(result);
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "i.ps1" }, "action:rerun": "r.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await firstRunDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.Holds(() => ctx.Updates.Count == 1 && c.IsIntervalIdle); // or the trigger would be skipped

        await c.OnActionAsync("rerun", CancellationToken.None);

        await Eventually.Holds(() => runner.Calls.Count == 3);
        Assert.Equal(new[] { "interval", "action", "interval" }, runner.Hooks);
    }

    [Fact]
    public async Task Unknown_action_is_ignored_with_a_warning()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "c.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        await c.OnActionAsync("nope", CancellationToken.None);

        Assert.Empty(runner.Calls);
        Assert.Contains(ctx.Logged.Snapshot(), m => m.StartsWith("WARN") && m.Contains("nope"));
    }

    [Fact]
    public async Task Refresh_reruns_the_interval_hook()
    {
        var firstRunDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner
        {
            OnRun = (_, _) =>
            {
                var result = FakeRunner.Ok("""{ "title": "x" }""");
                firstRunDone.TrySetResult();
                return Task.FromResult(result);
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "i.ps1" } } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await firstRunDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.Holds(() => ctx.Updates.Count == 1 && c.IsIntervalIdle); // or the trigger would be skipped

        await c.OnActionAsync(TileActions.Refresh, CancellationToken.None);

        await Eventually.Holds(() => runner.Calls.Count == 2);
        Assert.Equal(new[] { "interval", "interval" }, runner.Hooks);
    }

    [Fact]
    public async Task Refresh_reruns_startup_when_there_is_no_interval()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "startup": "s.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => runner.Calls.Count == 1);

        await c.OnActionAsync(TileActions.Refresh, CancellationToken.None);

        await Eventually.Holds(() => runner.Calls.Count == 2);
        Assert.Equal(new[] { "startup", "startup" }, runner.Hooks);
    }

    [Fact]
    public async Task Flyout_hook_output_becomes_the_popup()
    {
        var runner = new FakeRunner
        {
            OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("""{ "title": "Builds", "rows": ["a", { "text": "b", "value": "ok" }] }""")),
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "flyout": "f.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        var flyout = await c.GetFlyoutAsync(CancellationToken.None);

        Assert.Equal("Builds", flyout!.Title);
        Assert.Equal(new[] { "a", "b" }, flyout.Rows.Select(r => r.Text));
        Assert.Equal("flyout", Assert.Single(runner.Calls).Env["TILE_HOOK"]);
    }

    [Fact]
    public async Task No_flyout_hook_means_no_popup()
    {
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "c.ps1" } }""");
        await using var c = Create(new FakeRunner());
        await c.StartAsync(ctx, CancellationToken.None);

        Assert.Null(await c.GetFlyoutAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failing_flyout_hook_reports_and_shows_the_error()
    {
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(new ScriptResult(1, "", "boom")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "flyout": "f.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        var flyout = await c.GetFlyoutAsync(CancellationToken.None);

        Assert.Equal("Script error", flyout!.Title);
        Assert.Equal("flyout: exited with code 1", flyout.Subtitle);
        Assert.Equal("boom", Assert.Single(flyout.Rows).Text);
        Assert.Empty(ctx.Errors); // not a tile error: the host would stop asking the connector for popups
        Assert.Contains(ctx.Logged.Snapshot(), m => m.StartsWith("ERROR flyout: exited with code 1"));
        Assert.Equal(new[] { (TileActions.Restart, "Restart tile", true), (TileActions.ShowLog, "Open log", false) },
            flyout.Actions.Select(a => (a.Id, a.Label, a.Primary)));
    }

    [Fact]
    public async Task Flyout_that_failed_once_returns_the_real_popup_next_time()
    {
        var runs = 0;
        var runner = new FakeRunner
        {
            OnRun = (_, _) => Task.FromResult(Interlocked.Increment(ref runs) == 1
                ? new ScriptResult(1, "", "boom")
                : FakeRunner.Ok("""{ "title": "Builds" }""")),
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "flyout": "f.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        Assert.Equal("Script error", (await c.GetFlyoutAsync(CancellationToken.None))!.Title);
        Assert.Equal("Builds", (await c.GetFlyoutAsync(CancellationToken.None))!.Title);

        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Successful_click_without_output_clears_a_failed_click_error()
    {
        var runs = 0;
        var runner = new FakeRunner
        {
            OnRun = (_, _) => Task.FromResult(Interlocked.Increment(ref runs) == 1 ? new ScriptResult(1, "", "boom") : FakeRunner.Ok("")),
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "c.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        Assert.Single(ctx.Updates); // the idle state

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);
        Assert.Single(ctx.Errors);
        Assert.Single(ctx.Updates);

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        Assert.Equal(2, ctx.Updates.Count);
        Assert.Equal("t1", ctx.Updates[^1].Title);
        await c.OnActionAsync(TileActions.Click, CancellationToken.None); // no error shown any more: nothing to clear
        Assert.Equal(2, ctx.Updates.Count);
    }

    [Fact]
    public async Task Clearing_an_error_resends_the_last_good_state()
    {
        var runs = 0;
        var runner = new FakeRunner
        {
            OnRun = (_, _) => Task.FromResult(Interlocked.Increment(ref runs) switch
            {
                1 => FakeRunner.Ok("""{ "title": "good" }"""),
                2 => new ScriptResult(1, "", "boom"),
                _ => FakeRunner.Ok(""),
            }),
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "startup": "s.ps1", "click": "c.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates.Count == 1);

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);
        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        Assert.Equal(new[] { "good", "good" }, ctx.Updates.Select(u => u.Title));
    }

    [Fact]
    public async Task Nothing_is_published_after_dispose_started()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner { OnRun = (_, _) => { started.TrySetResult(); return gate.Task; } };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = c.DisposeAsync().AsTask();
        gate.SetResult(FakeRunner.Ok("""{ "title": "late" }"""));
        await disposing;
        await Task.Delay(200); // a late publish would land within moments; this can only miss a regression, never fail spuriously

        Assert.Empty(ctx.Updates);
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Dispose_waits_for_interval_and_click_runs_to_observe_cancellation()
    {
        var started = 0;
        var finished = 0;
        var runner = new FakeRunner
        {
            OnRun = async (_, ct) =>
            {
                Interlocked.Increment(ref started);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                {
                    await Task.Delay(300); // unwinding (killing the process tree) takes a moment
                    Interlocked.Increment(ref finished);
                    throw;
                }
                return FakeRunner.Ok("");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" }, "click": "c.ps1" } }""");
        var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        var click = c.OnActionAsync(TileActions.Click, CancellationToken.None);
        await Eventually.Holds(() => Volatile.Read(ref started) == 2);

        await c.DisposeAsync();

        Assert.Equal(2, Volatile.Read(ref finished));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => click);
    }

    [Fact]
    public async Task No_error_is_reported_after_dispose_started()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner { OnRun = (_, _) => { started.TrySetResult(); return gate.Task; } };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = c.DisposeAsync().AsTask();
        gate.SetResult(new ScriptResult(1, "", "killed"));
        await disposing;
        await Task.Delay(200); // a late publish would land within moments; this can only miss a regression, never fail spuriously

        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Action_cancelled_by_the_host_cancels_the_script_without_an_error()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner
        {
            OnRun = async (_, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { observed.TrySetResult(); throw; }
                return FakeRunner.Ok("");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "action:go": "g.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        using var host = new CancellationTokenSource();

        var call = c.OnActionAsync("go", host.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Errors);
        Assert.Contains(ctx.Logged.Snapshot(), m => m.Contains("action: cancelled by the host"));
    }

    [Fact]
    public async Task Resume_runs_the_resume_hook()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "resume": "r.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        ctx.RaiseResumed();

        await Eventually.Holds(() => runner.Calls.Count == 1);
        Assert.Equal("resume", runner.Hooks[0]);
    }

    [Fact]
    public async Task Resume_hook_runs_before_the_interval_refresh()
    {
        var resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner
        {
            OnRun = async (call, _) =>
            {
                if (call.Env["TILE_HOOK"] == "resume") await resumeGate.Task;
                return FakeRunner.Ok("");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "resume": "r.ps1", "interval": { "every": "1h", "run": "i.ps1" } } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => runner.Calls.Count == 1 && c.IsIntervalIdle);

        ctx.RaiseResumed();
        await Eventually.Holds(() => runner.Calls.Count == 2);
        Assert.Equal(new[] { "interval", "resume" }, runner.Hooks); // the refresh waits for the resume hook
        resumeGate.SetResult();

        await Eventually.Holds(() => runner.Calls.Count == 3);
        Assert.Equal(new[] { "interval", "resume", "interval" }, runner.Hooks);
    }

    [Fact]
    public async Task Watch_lines_update_the_tile_and_an_exit_restarts_it()
    {
        var runs = 0;
        var runner = new FakeRunner
        {
            OnRunLines = async (_, onLine, ct) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    onLine("""{ "title": "w1" }""");
                    onLine("");
                    onLine("""{ "title": "w2" }""");
                    return new ScriptResult(0, "", "");
                }
                await Task.Delay(Timeout.Infinite, ct);
                return new ScriptResult(0, "", "");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "watch": "w.ps1" } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => Volatile.Read(ref runs) == 2);
        Assert.Equal(new[] { "w1", "w2" }, ctx.Updates.Select(u => u.Title));
        Assert.Empty(ctx.Errors);
        Assert.All(runner.Calls, call => Assert.Equal("watch", call.Env["TILE_HOOK"]));
    }

    [Fact]
    public async Task Watch_non_zero_exit_is_reported_and_restarted()
    {
        var runs = 0;
        var runner = new FakeRunner
        {
            OnRunLines = async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref runs) == 1) return new ScriptResult(2, "", "watch died");
                await Task.Delay(Timeout.Infinite, ct);
                return new ScriptResult(0, "", "");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "watch": "w.ps1" } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => Volatile.Read(ref runs) == 2);
        Assert.Equal(("watch: exited with code 2", "watch died"), Assert.Single(ctx.Errors));
    }

    [Fact]
    public async Task Invalid_watch_line_is_reported_and_reading_goes_on()
    {
        var runner = new FakeRunner
        {
            OnRunLines = async (_, onLine, ct) =>
            {
                onLine("garbage");
                onLine("""{ "title": "fine" }""");
                await Task.Delay(Timeout.Infinite, ct);
                return new ScriptResult(0, "", "");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "watch": "w.ps1" } }""");
        await using var c = Create(runner);

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count == 1);
        Assert.Equal("fine", ctx.Updates[0].Title);
        Assert.Equal("watch: invalid output line", Assert.Single(ctx.Errors).Message);
    }

    [Fact]
    public async Task Dispose_cancels_running_scripts()
    {
        var cancelled = new TaskCompletionSource();
        var runner = new FakeRunner
        {
            OnRun = async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return FakeRunner.Ok("");
            },
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "a.ps1" } } }""");
        var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => runner.Calls.Count == 1);

        await c.DisposeAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(ctx.Errors);
    }
}
