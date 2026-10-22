# Taskbar Tiles — Milestone 2: Script Plugin, Error States and Context Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A built-in `script` connector that drives tiles from hook scripts (interval, watch, click, flyout, action:<id>, startup, resume), plus the host's error look, restart-with-backoff policy, per-tile context menu and a notification for a rejected `config.json`.

**Architecture:** The host's `TileRuntime` gets a connector *factory* instead of an instance, so it can replace a failing connector with a fresh one on the backoff schedule; each connector instance gets its own generation-bound `TileContext`, so late calls from a replaced instance are ignored. Connectors report non-fatal problems with the new `ITileContext.ReportError`. The script plugin is split into pure, unit-tested parts (`ScriptSettings`, `CommandResolver`, `ScriptOutput`, `HookResults`, `IntervalLoop`) and one process boundary (`IScriptRunner` / `ProcessScriptRunner`) that integration tests exercise with real PowerShell scripts. Context-menu commands go through a small, testable `TileCommands` class.

**Tech Stack:** .NET 9, WPF, C# latest, xUnit 2.9, System.Text.Json, System.Diagnostics.Process, Windows PowerShell 5.1 / PowerShell 7.

Spec: `docs/superpowers/specs/2026-10-22-taskbar-tiles-design.md` (milestone 2 = "Script plugin. All hooks, popups, context menu, error states and backoff."). Milestone 1 plan: `docs/superpowers/plans/2026-10-22-milestone-1-host-and-media.md`. Where the M1 plan and the code differ, the code on `main` is the truth; every "Modify" below is written against the current code.

## Global Constraints

- Target frameworks: host, tests and media plugin `net9.0-windows10.0.19041.0`; contracts, script plugin and the sample test plugin `net9.0`.
- Plugins never run on the UI thread. This includes connector construction (moves into `TileRuntime`, on the thread pool), `StartAsync`, actions, popups, `Resumed` handlers, cancellation callbacks and `DisposeAsync`. `ITileContext.Update` and `ITileContext.ReportError` are thread-safe and never touch WPF.
- Connector calls (`OnActionAsync`, `GetFlyoutAsync`) time out after 10 s; the tile shows "Not responding" for 3 s.
- Error look: title "Error", the message as subtitle and tooltip, `warning` glyph in the `Warn` color. Error popup: the details, a primary "Restart tile" action and an "Open log" action.
- Restart backoff for a failing connector and for an exited `watch` script: 2 s, 10 s, 1 min, then every 5 min. Reset after a successful update.
- Script commands: `.ps1` via `pwsh` (falling back to `powershell.exe`) with `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File`, `.py` via `python -u`, an existing other file or any command line via `cmd.exe /d /s /c`. `run` paths are relative to the config folder.
- Script environment: `TILE_ID`, `TILE_HOOK`, `TILE_ACTION`, `TILE_SETTINGS_JSON`.
- Script stdout is camelCase JSON matching `TileState` / `FlyoutState`; icons by glyph name, colors by name (`neutral`, `accent`, `ok`, `warn`, `bad`).
- Data folder: `%LOCALAPPDATA%\TaskbarTiles\` (`config.json`, `scripts\`, `plugins\`, `logs\`). A tile's log is `logs\tile-{id}.log`.
- WPF projects (Host, Tests) do not get `System.IO` from implicit usings: add `using System.IO;` explicitly in every file that needs it.
- Plugins ship through `build/CopyPlugins.targets` (`<PluginProject>` items) into `plugins\<AssemblyName>\`, never as a normal assembly reference of the host.
- Tests are xUnit in `tests/TaskbarTiles.Tests`. Tests that start real processes carry `[Trait("Category", "Integration")]`; run everything with `dotnet test TaskbarTiles.sln`, or skip them with `--filter "Category!=Integration"`.
- No Windows toast / AppUserModelID machinery: notifications are our own small WPF window.
- Deferred to milestone 3 (they need config writing, which comes with edit mode): context-menu items Move left/right, Add to stack / Pause rotation, Edit layout, Remove tile.
- Every commit message ends with a trailer naming the model that wrote the commit. The commands below show `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; use your own model name if it differs.

## File Structure

```
build/CopyPlugins.targets                  unchanged; the script plugin is one more @(PluginProject)
docs/script-connectors.md                  reference for script authors (new)
scripts/uptime.ps1, scripts/ping.ps1       sample scripts, copied to %LOCALAPPDATA%\TaskbarTiles\scripts on first run (new)
src/TaskbarTiles.Contracts/
  Duration.cs                              moved here from Host/Config so plugins parse "30s" the same way
  BackoffSchedule.cs                       restart delays (new)
  TileActions.cs                           host action ids "click", "refresh" (new)
  ITileConnector.cs                        doc comment points at TileActions
  ITileContext.cs                          + ConfigDirectory, + ReportError
src/TaskbarTiles.Host/
  TaskbarTiles.Host.csproj                 + script plugin (build order + PluginProject), + sample scripts as content
  AppPaths.cs                              + ScriptsDir, SampleScriptsDir
  App.xaml.cs                              TileCommands, config notice, sample install
  HostController.cs                        connector factory, config directory
  SampleScripts.cs                         first-run copy of the samples (new)
  Config/ConfigParser.cs                   uses Contracts.Duration
  Config/ConfigStore.cs                    default config also places the uptime sample
  Runtime/TileContext.cs                   generation-bound, ReportError, per-handler Resumed isolation
  Runtime/TileRuntime.cs                   TileError, TileRuntimeOptions, restart + backoff, reported errors, in-flight guard
  Taskbar/NativeMethods.cs                 + MONITOR_DEFAULTTOPRIMARY
  UI/TileCommands.cs                       TileCommand, BarCommand, MenuEntry, TileMenu, TileCommands (new)
  UI/TileBar.cs                            per-tile context menu
  UI/TileView.xaml.cs                      logs UI errors to the host log
  UI/FlyoutWindow.cs                       routes host action ids, logs UI errors
  UI/ConfigNotice.cs                       text for a rejected config.json (new)
  UI/NotificationWindow.cs                 auto-dismissing notice above the primary taskbar (new)
src/TaskbarTiles.Plugins.Script/           (new project)
  TaskbarTiles.Plugins.Script.csproj
  ScriptSettings.cs                        settings.hooks -> HookSpec per hook, or errors
  ExecutableLocator.cs                     PATH lookup; pwsh, else powershell.exe
  CommandResolver.cs                       ScriptCommand; run -> file name, arguments, working directory
  ScriptResult.cs                          exit code, stdout, stderr, timed out, start error
  ScriptOutput.cs                          ParseResult<T>; stdout JSON -> TileState / FlyoutState (lenient)
  HookResults.cs                           HookOutcome<T>; ScriptResult -> value / error / nothing
  IntervalLoop.cs                          run now and every period, skip while the previous run is going
  IScriptRunner.cs                         process abstraction
  ProcessScriptRunner.cs                   real processes, UTF-8, kill tree on timeout / cancel
  ScriptConnector.cs                       [TileConnector("script")], hook orchestration
tests/TaskbarTiles.Tests/
  TaskbarTiles.Tests.csproj                + script plugin reference and PluginProject
  TestDoubles.cs                           + Eventually, CollectingLog.Snapshot, TestTileContext, ScriptFiles
  BackoffScheduleTests.cs, TileRuntimeRestartTests.cs, TileCommandsTests.cs, ConfigNoticeTests.cs,
  ScriptSettingsTests.cs, CommandResolverTests.cs, ExecutableLocatorTests.cs, ScriptOutputTests.cs,
  HookResultsTests.cs, IntervalLoopTests.cs, ProcessScriptRunnerTests.cs, ScriptConnectorTests.cs,
  ScriptConnectorIntegrationTests.cs, SampleScriptsTests.cs (new)
  ContractsTests.cs, DurationTests.cs, TileRuntimeTests.cs, HostControllerTests.cs,
  PluginLoaderTests.cs, ConfigStoreTests.cs (modified)
```

### Contract changes in this milestone (all additive for plugin authors)

| Change | Why |
|---|---|
| `string ITileContext.ConfigDirectory { get; }` | Script `run` paths are relative to the config folder. The host owns where that folder is; plugins must not hard-code `%LOCALAPPDATA%`, and tests point it at a temp folder. |
| `void ITileContext.ReportError(string message, string? details = null)` | A script that exits non-zero is not a crashed connector. The connector stays alive and retries on its own; the tile shows the error look and the popup shows stderr. Throwing stays the signal for a fatal failure that needs a restart. |
| `static class TileActions { const string Click = "click"; const string Refresh = "refresh"; }` | Names the action ids the host itself sends (plain click, "Refresh now"). |
| `sealed class BackoffSchedule` | The spec's backoff is needed by the host (connector restarts) and by the script plugin (`watch` restarts); plugins can only share code through Contracts. |
| `static class Duration` (moved from `TaskbarTiles.Host.Config`) | The script plugin parses `every` / `timeout` exactly like `config.json` parses `rotate`. |

---

### Task 1: Shared contract helpers (Duration, BackoffSchedule, TileActions)

**Files:**
- Move: `src/TaskbarTiles.Host/Config/Duration.cs` → `src/TaskbarTiles.Contracts/Duration.cs`
- Create: `src/TaskbarTiles.Contracts/BackoffSchedule.cs`, `src/TaskbarTiles.Contracts/TileActions.cs`
- Modify: `src/TaskbarTiles.Contracts/ITileConnector.cs`, `src/TaskbarTiles.Host/Config/ConfigParser.cs:1`
- Test: `tests/TaskbarTiles.Tests/BackoffScheduleTests.cs` (new), `tests/TaskbarTiles.Tests/DurationTests.cs:1`, `tests/TaskbarTiles.Tests/ContractsTests.cs`

**Interfaces:**
- Consumes: nothing new
- Produces:
  - `TaskbarTiles.Contracts.Duration`: `static bool TryParse(string? text, out TimeSpan value)`, `static TimeSpan Parse(string text)` (unchanged behavior, new namespace)
  - `TaskbarTiles.Contracts.BackoffSchedule`: `BackoffSchedule(IReadOnlyList<TimeSpan> steps)`, `static BackoffSchedule Default` (2 s, 10 s, 1 min, 5 min), `IReadOnlyList<TimeSpan> Steps`, `TimeSpan Delay(int previousFailures)` (0 = first failure; the last step repeats; negative counts as 0)
  - `TaskbarTiles.Contracts.TileActions`: `const string Click = "click"`, `const string Refresh = "refresh"`

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/BackoffScheduleTests.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public class BackoffScheduleTests
{
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 10)]
    [InlineData(2, 60)]
    [InlineData(3, 300)]
    [InlineData(4, 300)]
    [InlineData(int.MaxValue, 300)]
    public void Default_is_2s_10s_1min_then_every_5min(int previousFailures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffSchedule.Default.Delay(previousFailures));

    [Fact]
    public void Last_step_repeats()
    {
        var schedule = new BackoffSchedule([TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(7)]);

        Assert.Equal(TimeSpan.FromMilliseconds(5), schedule.Delay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(7), schedule.Delay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(7), schedule.Delay(50));
    }

    [Fact]
    public void Empty_schedule_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new BackoffSchedule([]));

    [Fact]
    public void Negative_step_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffSchedule([TimeSpan.FromSeconds(-1)]));
}
```

In `tests/TaskbarTiles.Tests/DurationTests.cs`, replace line 1:
```csharp
using TaskbarTiles.Host.Config;
```
with:
```csharp
using TaskbarTiles.Contracts;
```

In `tests/TaskbarTiles.Tests/ContractsTests.cs`, add inside the class after `TileButton_is_enabled_by_default`:
```csharp
    [Fact]
    public void Host_action_ids_are_stable()
    {
        Assert.Equal("click", TileActions.Click);
        Assert.Equal("refresh", TileActions.Refresh);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "BackoffScheduleTests|DurationTests|ContractsTests"`
Expected: build FAILS with `The type or namespace name 'BackoffSchedule' could not be found` and `The name 'Duration' does not exist in the current context`.

- [ ] **Step 3: Move Duration into Contracts**

```bash
git mv src/TaskbarTiles.Host/Config/Duration.cs src/TaskbarTiles.Contracts/Duration.cs
```

Replace the whole content of `src/TaskbarTiles.Contracts/Duration.cs` with (only the namespace and summary change):
```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskbarTiles.Contracts;

/// <summary>Parses "500ms", "30s", "10m", "1h". Shared by config.json and plugin settings.</summary>
public static partial class Duration
{
    // [0-9] rather than \d: \d also matches non-ASCII digits, which double.Parse rejects.
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*(ms|s|m|h)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (text is null) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        try
        {
            value = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                _ => TimeSpan.FromHours(n),
            };
        }
        catch (OverflowException)
        {
            value = default;
            return false;
        }
        return true;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid duration '{text}'. Use e.g. 500ms, 30s, 10m, 1h.");
}
```

In `src/TaskbarTiles.Host/Config/ConfigParser.cs`, replace line 1:
```csharp
using System.Text.Json;
```
with:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;
```

- [ ] **Step 4: Add BackoffSchedule and TileActions**

`src/TaskbarTiles.Contracts/BackoffSchedule.cs`:
```csharp
namespace TaskbarTiles.Contracts;

/// <summary>
/// Delays before restarting something that keeps failing: 2 s, 10 s, 1 min, then every 5 min.
/// The host uses it for failing connectors; plugins can use it for what they restart themselves.
/// Callers reset their failure count after a success.
/// </summary>
public sealed class BackoffSchedule
{
    readonly TimeSpan[] _steps;

    public BackoffSchedule(IReadOnlyList<TimeSpan> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) throw new ArgumentException("A backoff schedule needs at least one step.", nameof(steps));
        if (steps.Any(s => s < TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(steps), "Backoff steps cannot be negative.");
        _steps = steps.ToArray();
    }

    public static BackoffSchedule Default { get; } = new(
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)]);

    public IReadOnlyList<TimeSpan> Steps => _steps;

    /// <summary>The delay after a failure, given how many failures came right before it (0 for the first one). The last step repeats.</summary>
    public TimeSpan Delay(int previousFailures) => _steps[Math.Clamp(previousFailures, 0, _steps.Length - 1)];
}
```

`src/TaskbarTiles.Contracts/TileActions.cs`:
```csharp
namespace TaskbarTiles.Contracts;

/// <summary>Action ids the host itself sends to <see cref="ITileConnector.OnActionAsync"/>, besides button and popup action ids.</summary>
public static class TileActions
{
    /// <summary>A plain click on a tile whose GetFlyoutAsync returned null.</summary>
    public const string Click = "click";

    /// <summary>"Refresh now" from the tile's context menu: fetch fresh data now. Connectors may ignore it.</summary>
    public const string Refresh = "refresh";
}
```

Replace the whole content of `src/TaskbarTiles.Contracts/ITileConnector.cs` with:
```csharp
namespace TaskbarTiles.Contracts;

/// <summary>Drives one tile. The host creates one instance per tile in config.json, and a fresh one when it restarts the tile.</summary>
public interface ITileConnector : IAsyncDisposable
{
    /// <summary>Called once per instance. Keep the context and call ctx.Update whenever the tile should change.</summary>
    Task StartAsync(ITileContext ctx, CancellationToken ct);

    /// <summary>A button or popup action id, or one of <see cref="TileActions"/> ("click", "refresh").</summary>
    Task OnActionAsync(string actionId, CancellationToken ct);

    /// <summary>Content of the popup shown when the tile is clicked; null means no popup.</summary>
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "BackoffScheduleTests|DurationTests|ContractsTests"`
Expected: `Failed: 0`; the 10 `BackoffScheduleTests` cases and `Host_action_ids_are_stable` are among the passed tests.

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/TaskbarTiles.Contracts src/TaskbarTiles.Host/Config tests/TaskbarTiles.Tests/BackoffScheduleTests.cs tests/TaskbarTiles.Tests/DurationTests.cs tests/TaskbarTiles.Tests/ContractsTests.cs
git commit -m "feat(contracts): share Duration, add BackoffSchedule and TileActions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Tile runtime error policy (reported errors, restart with backoff)

**Files:**
- Modify: `src/TaskbarTiles.Contracts/ITileContext.cs` (whole file)
- Modify: `src/TaskbarTiles.Host/Runtime/TileContext.cs` (whole file), `src/TaskbarTiles.Host/Runtime/TileRuntime.cs` (whole file), `src/TaskbarTiles.Host/HostController.cs` (whole file), `src/TaskbarTiles.Host/Plugins/PluginLoader.cs:15-16`
- Test: `tests/TaskbarTiles.Tests/TileRuntimeRestartTests.cs` (new), `tests/TaskbarTiles.Tests/TestDoubles.cs`, `tests/TaskbarTiles.Tests/TileRuntimeTests.cs:11-12,148`, `tests/TaskbarTiles.Tests/HostControllerTests.cs`

**Interfaces:**
- Consumes: `BackoffSchedule` (Task 1)
- Produces:
  - `ITileContext.ConfigDirectory : string`, `ITileContext.ReportError(string message, string? details = null) : void`
  - `public sealed record TileError(string Message, string Details, bool Fatal)` with `static TileError From(Exception ex)` (namespace `TaskbarTiles.Host.Runtime`)
  - `public sealed record TileRuntimeOptions { TimeSpan CallTimeout = 10 s; TimeSpan NotRespondingFor = 3 s; BackoffSchedule Backoff = BackoffSchedule.Default; string ConfigDirectory = "" }` (init-only properties)
  - `TileRuntime(string tileId, string connectorId, double width, Func<ITileConnector> createConnector, JsonElement settings, IUiDispatcher ui, ITileLog log, TileRuntimeOptions? options = null)`
  - `TileRuntime.RestartActionId = "host:restart"`, `TileRuntime.ShowLogActionId = "host:show-log"` (ids of the error popup's actions)
  - `TileRuntime.LastError : TileError?` (was `Exception?`), `Task TileRuntime.RestartAsync()` (manual restart, resets the backoff count)
  - Unchanged: `StartAsync()`, `InvokeActionAsync(string)`, `GetFlyoutAsync() : Task<FlyoutResult>`, `RaiseResumed()`, `State`, `StateChanged`, `TileId`, `ConnectorId`, `Width`, `Initial`, `Normalize`, `DisposeAsync()`
  - `HostController(..., Func<string, ITileBarView> createBar, string? configDirectory = null)` (new optional last parameter; defaults to `AppPaths.DataDir`)
  - Test helpers: `Eventually.Holds(Func<bool> condition, int timeoutMs = 5000) : Task`, `CollectingLog.Snapshot() : string[]`

Design notes for the implementer:
- Every `StartAsync` / restart bumps a generation number. A `TileContext` carries the generation of its connector instance; `Update`, `ReportError` and failures from an older generation are dropped, so a replaced connector cannot overwrite the new one.
- `Update`, `ReportError` and fatal failures all go through one coalescing slot (latest wins), applied on the UI thread. Applying a fatal error schedules a restart after `Backoff.Delay(failures++)`; applying a successful update resets `failures` to 0. A reported error does neither.
- An action id that is already running is dropped (in-flight guard), so repeated clicks on an inline button do not pile up connector calls.
- `Resumed` handlers are invoked one by one (`GetInvocationList`), each in its own try/catch.

- [ ] **Step 1: Add test helpers**

In `tests/TaskbarTiles.Tests/TestDoubles.cs`, replace the `CollectingLog` class with:
```csharp
public sealed class CollectingLog : ITileLog
{
    public List<string> Messages { get; } = [];
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Messages) Messages.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message)); }
    public string[] Snapshot() { lock (Messages) return Messages.ToArray(); }
}

public static class Eventually
{
    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task Holds(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Condition not met within {timeoutMs} ms.");
            await Task.Delay(20);
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/TileRuntimeRestartTests.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Tests;

public class TileRuntimeRestartTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();
    static readonly BackoffSchedule Never = new([TimeSpan.FromMinutes(10)]);
    static readonly BackoffSchedule Fast = new([TimeSpan.FromMilliseconds(20)]);

    /// <summary>Hands out a new FakeConnector per call; Setup sees how many were created before.</summary>
    sealed class Factory
    {
        readonly List<FakeConnector> _created = [];
        public Action<FakeConnector, int> Setup { get; set; } = (_, _) => { };
        public int Count { get { lock (_created) return _created.Count; } }
        public FakeConnector this[int i] { get { lock (_created) return _created[i]; } }

        public ITileConnector Create()
        {
            var c = new FakeConnector();
            int before;
            lock (_created) before = _created.Count;
            Setup(c, before);
            lock (_created) _created.Add(c);
            return c;
        }
    }

    static TileRuntime Create(Func<ITileConnector> factory, IUiDispatcher? ui = null, BackoffSchedule? backoff = null,
        CollectingLog? log = null, string configDirectory = "") =>
        new("tile1", "fake", 132, factory, NoSettings, ui ?? new ImmediateDispatcher(), log ?? new CollectingLog(),
            new TileRuntimeOptions { Backoff = backoff ?? Never, ConfigDirectory = configDirectory });

    [Fact]
    public async Task Reported_error_shows_error_look_and_details_without_restart()
    {
        var f = new Factory
        {
            Setup = (c, _) => c.OnStart = (ctx, _) => { ctx.ReportError("exit 3", "line one\nline two"); return Task.CompletedTask; },
        };
        var rt = Create(f.Create, backoff: Fast);

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.Equal("exit 3", rt.State.Subtitle);
        Assert.Equal("exit 3", rt.State.Tooltip);
        Assert.Equal(TileColor.Warn, rt.State.Icon!.Color);
        Assert.False(rt.LastError!.Fatal);
        var flyout = (await rt.GetFlyoutAsync()).Flyout!;
        Assert.Equal("exit 3", flyout.Subtitle);
        Assert.Equal(new[] { "line one", "line two" }, flyout.Rows.Select(r => r.Text));
        Assert.Equal(new[] { TileRuntime.RestartActionId, TileRuntime.ShowLogActionId }, flyout.Actions.Select(a => a.Id));
        Assert.True(flyout.Actions[0].Primary);
        await Task.Delay(200);
        Assert.Equal(1, f.Count);
    }

    [Fact]
    public async Task Update_clears_a_reported_error()
    {
        var c = new FakeConnector();
        var rt = Create(() => c);
        await rt.StartAsync();
        c.Context!.ReportError("bad");

        c.Context.Update(new TileState { Title = "good" });

        Assert.Null(rt.LastError);
        Assert.Equal("good", rt.State.Title);
    }

    [Fact]
    public async Task Update_and_report_are_coalesced_latest_wins()
    {
        var c = new FakeConnector();
        var ui = new ManualDispatcher();
        var rt = Create(() => c, ui);
        await rt.StartAsync();

        c.Context!.Update(new TileState { Title = "a" });
        c.Context.ReportError("broken");
        ui.Pump();

        Assert.Equal(1, ui.Posted);
        Assert.Equal("Error", rt.State.Title);
        Assert.NotNull(rt.LastError);

        c.Context.Update(new TileState { Title = "b" });
        ui.Pump();
        Assert.Equal("b", rt.State.Title);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Fatal_error_restarts_with_a_fresh_connector()
    {
        var f = new Factory();
        f.Setup = (c, before) => c.OnStart = before == 0
            ? (_, _) => throw new InvalidOperationException("first start fails")
            : (ctx, _) => { ctx.Update(new TileState { Title = "back" }); return Task.CompletedTask; };
        var rt = Create(f.Create, backoff: Fast);

        await rt.StartAsync();

        await Eventually.Holds(() => rt.State.Title == "back");
        Assert.Equal(2, f.Count);
        Assert.True(f[0].Disposed);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Repeated_failures_follow_the_backoff_schedule()
    {
        var f = new Factory { Setup = (c, _) => c.OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var log = new CollectingLog();
        var rt = Create(f.Create, backoff: new BackoffSchedule(
            [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60), TimeSpan.FromHours(1)]), log: log);

        await rt.StartAsync();

        await Eventually.Holds(() => f.Count == 3);
        await Task.Delay(300);
        Assert.Equal(3, f.Count); // the third failure waits an hour
        Assert.Contains(log.Snapshot(), m => m.Contains("restarts in"));
        await rt.DisposeAsync();
    }

    [Fact]
    public async Task Successful_update_resets_the_backoff()
    {
        var f = new Factory();
        f.Setup = (c, before) =>
        {
            if (before == 0)
            {
                c.OnStart = (_, _) => throw new InvalidOperationException("boom");
                return;
            }
            c.OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "ok" }); return Task.CompletedTask; };
            c.OnAction = (_, _) => throw new InvalidOperationException("action boom");
        };
        var rt = Create(f.Create, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1)]));
        await rt.StartAsync();
        await Eventually.Holds(() => rt.State.Title == "ok");

        await rt.InvokeActionAsync("x"); // fails again; without the reset this would wait an hour

        await Eventually.Holds(() => f.Count == 3);
        await rt.DisposeAsync();
    }

    [Fact]
    public async Task Manual_restart_disposes_the_old_connector_and_starts_a_new_one()
    {
        var f = new Factory
        {
            Setup = (c, before) => c.OnStart = (ctx, _) => { ctx.Update(new TileState { Title = $"instance {before}" }); return Task.CompletedTask; },
        };
        var rt = Create(f.Create);
        await rt.StartAsync();
        Assert.Equal("instance 0", rt.State.Title);

        await rt.RestartAsync();

        Assert.Equal(2, f.Count);
        Assert.True(f[0].Disposed);
        Assert.Equal("instance 1", rt.State.Title);
    }

    [Fact]
    public async Task Restart_shows_restarting_until_the_new_connector_updates()
    {
        var c = new FakeConnector();
        var rt = Create(() => c);
        await rt.StartAsync();

        await rt.RestartAsync();

        Assert.Equal("Restarting…", rt.State.Title);
    }

    [Fact]
    public async Task Late_update_from_a_replaced_connector_is_ignored()
    {
        var f = new Factory();
        var rt = Create(f.Create);
        await rt.StartAsync();
        var oldContext = f[0].Context!;

        await rt.RestartAsync();
        oldContext.Update(new TileState { Title = "stale" });
        oldContext.ReportError("stale error");

        Assert.NotEqual("stale", rt.State.Title);
        Assert.Null(rt.LastError);
    }

    [Fact]
    public async Task Throwing_factory_shows_the_error_and_retries()
    {
        var calls = 0;
        var rt = Create(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("ctor boom");
            return new FakeConnector { OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "ok" }); return Task.CompletedTask; } };
        }, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(100)]));

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.True(rt.LastError!.Fatal);
        Assert.Equal("ctor boom", rt.LastError.Message);
        await Eventually.Holds(() => rt.State.Title == "ok");
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_restart()
    {
        var f = new Factory { Setup = (c, _) => c.OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(f.Create, backoff: new BackoffSchedule([TimeSpan.FromMilliseconds(50)]));
        await rt.StartAsync();

        await rt.DisposeAsync();
        await Task.Delay(250);

        Assert.Equal(1, f.Count);
    }

    [Fact]
    public async Task Context_exposes_the_config_directory()
    {
        var c = new FakeConnector();
        var rt = Create(() => c, configDirectory: @"C:\cfg");

        await rt.StartAsync();

        Assert.Equal(@"C:\cfg", c.Context!.ConfigDirectory);
    }

    [Fact]
    public async Task Throwing_resumed_handler_does_not_skip_the_others()
    {
        var log = new CollectingLog();
        var c = new FakeConnector();
        var rt = Create(() => c, log: log);
        await rt.StartAsync();
        var second = new TaskCompletionSource();
        c.Context!.Resumed += (_, _) => throw new InvalidOperationException("first handler");
        c.Context.Resumed += (_, _) => second.TrySetResult();

        rt.RaiseResumed();

        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Eventually.Holds(() => log.Snapshot().Any(m => m.StartsWith("WARN") && m.Contains("first handler")));
    }

    [Fact]
    public async Task Repeated_action_while_in_flight_is_dropped()
    {
        var gate = new TaskCompletionSource();
        var c = new FakeConnector { OnAction = (id, _) => id == "next" ? gate.Task : Task.CompletedTask };
        var rt = Create(() => c);
        await rt.StartAsync();

        var first = rt.InvokeActionAsync("next");
        await rt.InvokeActionAsync("next"); // returns at once: "next" is still running
        await rt.InvokeActionAsync("prev"); // other ids are not blocked

        await Eventually.Holds(() => c.Actions.Count == 2);
        Assert.Equal(1, c.Actions.Count(a => a == "next"));
        gate.SetResult();
        await first;
        await rt.InvokeActionAsync("next");
        Assert.Equal(2, c.Actions.Count(a => a == "next"));
    }
}
```

In `tests/TaskbarTiles.Tests/TileRuntimeTests.cs`, replace lines 11-12:
```csharp
    static TileRuntime Create(FakeConnector c, IUiDispatcher ui, TimeSpan? timeout = null, TimeSpan? notResponding = null) =>
        new("tile1", "fake", 132, c, NoSettings, ui, new CollectingLog(), timeout, notResponding);
```
with:
```csharp
    // A ten-minute backoff keeps the automatic restart out of these tests; TileRuntimeRestartTests covers it.
    static TileRuntime Create(FakeConnector c, IUiDispatcher ui, TimeSpan? timeout = null, TimeSpan? notResponding = null) =>
        new("tile1", "fake", 132, () => c, NoSettings, ui, new CollectingLog(), new TileRuntimeOptions
        {
            CallTimeout = timeout ?? TimeSpan.FromSeconds(10),
            NotRespondingFor = notResponding ?? TimeSpan.FromSeconds(3),
            Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]),
        });
```
and replace line 148:
```csharp
        var rt = new TileRuntime("t", "ghost", 132, new UnknownConnector("ghost"), NoSettings, new ImmediateDispatcher(), new CollectingLog());
```
with:
```csharp
        var rt = new TileRuntime("t", "ghost", 132, () => new UnknownConnector("ghost"), NoSettings, new ImmediateDispatcher(), new CollectingLog());
```

In `tests/TaskbarTiles.Tests/HostControllerTests.cs`:

Add these nested types right after `NeverPumpedContext`:
```csharp
    sealed class CapturingConnector : ITileConnector
    {
        [ThreadStatic] public static bool InsideStart;
        public static bool ConstructedInsideStart { get; private set; }
        public static TaskCompletionSource<ITileContext> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturingConnector() => ConstructedInsideStart = InsideStart;
        public Task StartAsync(ITileContext ctx, CancellationToken ct) { Started.TrySetResult(ctx); return Task.CompletedTask; }
        public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
        public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
```

Replace the whole `Throwing_plugin_constructor_falls_back_to_placeholder` test with:
```csharp
    [Fact]
    public async Task Throwing_plugin_constructor_shows_an_error_tile()
    {
        var catalog = new ConnectorCatalog(new Dictionary<string, ConnectorDescriptor>
        {
            ["bad"] = new("bad", typeof(ThrowingConnector), 100, "bad.dll"),
        });
        var cfg = Parse("""{ "tiles": { "a": { "connector": "bad" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var bars = new Dictionary<string, FakeBar>();
        var log = new CollectingLog();
        var host = new HostController(catalog, () => cfg, source, new ImmediateDispatcher(),
            _ => log, id => bars[id] = new FakeBar(id));

        host.Start();

        Assert.Equal("bad", host.Runtimes["a"].ConnectorId);
        Assert.Equal(new[] { "a" }, bars["MAIN"].Tiles.Select(t => t.TileId));
        await Eventually.Holds(() => host.Runtimes["a"].State.Title == "Error");
        Assert.Equal("boom", host.Runtimes["a"].LastError!.Message); // not "Exception has been thrown by the target of an invocation."
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Connectors_are_built_off_the_calling_thread_and_get_the_config_directory()
    {
        var catalog = new ConnectorCatalog(new Dictionary<string, ConnectorDescriptor>
        {
            ["cap"] = new("cap", typeof(CapturingConnector), 100, "cap.dll"),
        });
        var cfg = Parse("""{ "tiles": { "a": { "connector": "cap" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var host = new HostController(catalog, () => cfg, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), configDirectory: @"C:\cfg");

        CapturingConnector.InsideStart = true;
        try { host.Start(); }
        finally { CapturingConnector.InsideStart = false; }

        var ctx = await CapturingConnector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"C:\cfg", ctx.ConfigDirectory);
        Assert.False(CapturingConnector.ConstructedInsideStart);
        await host.DisposeAsync();
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "TileRuntimeRestartTests|TileRuntimeTests|HostControllerTests"`
Expected: build FAILS with `The type or namespace name 'TileRuntimeOptions' could not be found` and `'ITileContext' does not contain a definition for 'ReportError'`.

- [ ] **Step 4: Extend the contract**

Replace the whole content of `src/TaskbarTiles.Contracts/ITileContext.cs` with:
```csharp
using System.Text.Json;

namespace TaskbarTiles.Contracts;

public interface ITileContext
{
    string TileId { get; }

    /// <summary>The tile's "settings" object from config.json (an empty object if absent).</summary>
    JsonElement Settings { get; }

    /// <summary>
    /// The folder that holds config.json (normally %LOCALAPPDATA%\TaskbarTiles). Resolve relative paths from
    /// settings against it, so plugins never hard-code the data folder.
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>Thread-safe. Only the latest state is rendered if several arrive quickly.</summary>
    void Update(TileState state);

    /// <summary>
    /// Thread-safe. Shows the error look (message as subtitle and tooltip) and puts the details, e.g. a script's
    /// stderr, in the tile's popup, without restarting the connector. The next Update clears it.
    /// Throw from a connector method only for fatal failures: the host then restarts the connector with backoff.
    /// </summary>
    void ReportError(string message, string? details = null);

    /// <summary>Raised after the system resumes from sleep, on a thread-pool thread.</summary>
    event EventHandler? Resumed;

    ITileLog Log { get; }
}

public interface ITileLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
```

- [ ] **Step 5: Rewrite TileContext**

Replace the whole content of `src/TaskbarTiles.Host/Runtime/TileContext.cs` with:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>What one connector instance sees. Bound to that instance's generation, so a replaced connector's calls are dropped.</summary>
sealed class TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public string ConfigDirectory => configDirectory;
    public ITileLog Log => log;
    public event EventHandler? Resumed;

    public void Update(TileState state) => runtime.Enqueue(generation, state);

    public void ReportError(string message, string? details = null) => runtime.Report(generation, message, details);

    /// <summary>Each handler runs in its own try/catch so one throwing plugin handler cannot skip the others.</summary>
    internal void RaiseResumed(Action<Exception> onError)
    {
        if (Resumed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { onError(ex); }
        }
    }
}
```

- [ ] **Step 6: Rewrite TileRuntime**

Replace the whole content of `src/TaskbarTiles.Host/Runtime/TileRuntime.cs` with:
```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

public enum FlyoutOutcome
{
    /// <summary>The connector answered: it has no popup (the tile click should become a "click" action).</summary>
    NoPopup,
    /// <summary>A popup (or an error description) is available in <see cref="FlyoutResult.Flyout"/>.</summary>
    Popup,
    /// <summary>The call timed out, was cancelled or the runtime is disposed; do nothing.</summary>
    Unavailable,
}

public readonly record struct FlyoutResult(FlyoutOutcome Outcome, FlyoutState? Flyout = null)
{
    public static FlyoutResult NoPopup { get; } = new(FlyoutOutcome.NoPopup);
    public static FlyoutResult Unavailable { get; } = new(FlyoutOutcome.Unavailable);
    public static FlyoutResult Of(FlyoutState popup) => new(FlyoutOutcome.Popup, popup);
}

/// <summary>
/// What is wrong with a tile. Fatal errors (the connector threw) restart the connector with backoff;
/// reported errors (ITileContext.ReportError) only change the look until the next successful update.
/// </summary>
public sealed record TileError(string Message, string Details, bool Fatal)
{
    public static TileError From(Exception ex) => new(ex.Message, ex.ToString(), Fatal: true);
}

public sealed record TileRuntimeOptions
{
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan NotRespondingFor { get; init; } = TimeSpan.FromSeconds(3);
    public BackoffSchedule Backoff { get; init; } = BackoffSchedule.Default;

    /// <summary>Handed to connectors as ITileContext.ConfigDirectory.</summary>
    public string ConfigDirectory { get; init; } = "";
}

/// <summary>
/// One tile's connector and its latest state. Connector calls run on the thread pool with a timeout;
/// state changes are applied and announced on the UI thread. A connector that throws is replaced by a
/// fresh instance from the factory after a backoff delay.
/// </summary>
public sealed class TileRuntime : IAsyncDisposable
{
    public const string RestartActionId = "host:restart";
    public const string ShowLogActionId = "host:show-log";

    public static TileState Initial { get; } = new() { Title = "…" };

    static readonly TileState NotResponding = new()
    {
        Title = "Not responding",
        Icon = new TileIcon("clock", TileColor.Warn),
    };

    static readonly TileState Restarting = new()
    {
        Title = "Restarting…",
        Icon = new TileIcon("sync"),
    };

    /// <summary>One connector instance and everything tied to its life.</summary>
    sealed class Instance(int generation, ITileConnector connector, TileContext context, CancellationTokenSource cts)
    {
        public int Generation { get; } = generation;
        public ITileConnector Connector { get; } = connector;
        public TileContext Context { get; } = context;
        public CancellationTokenSource Cts { get; } = cts;
    }

    /// <summary>The next state to show. Success resets the backoff; a fatal Error schedules a restart.</summary>
    sealed record Pending(int Generation, TileState State, TileError? Error, bool Success);

    readonly Func<ITileConnector> _createConnector;
    readonly JsonElement _settings;
    readonly IUiDispatcher _ui;
    readonly ITileLog _log;
    readonly TileRuntimeOptions _options;
    readonly CancellationTokenSource _lifetime = new();
    readonly object _instanceLock = new();
    readonly ConcurrentDictionary<string, byte> _actionsInFlight = new(StringComparer.Ordinal);

    Instance? _current;   // null while (re)starting or after a failed construction
    int _generation;      // written under _instanceLock; bumped on every start and restart
    Pending? _pending;
    int _postQueued;
    int _disposed;
    int _notRespondingGeneration;
    int _failures;        // consecutive fatal errors; UI thread only
    TileState _beforeNotResponding = Initial;

    public TileRuntime(string tileId, string connectorId, double width, Func<ITileConnector> createConnector,
        JsonElement settings, IUiDispatcher ui, ITileLog log, TileRuntimeOptions? options = null)
    {
        TileId = tileId;
        ConnectorId = connectorId;
        Width = width;
        _createConnector = createConnector;
        _settings = settings;
        _ui = ui;
        _log = log;
        _options = options ?? new TileRuntimeOptions();
    }

    public string TileId { get; }
    public string ConnectorId { get; }
    public double Width { get; }

    /// <summary>Read on the UI thread.</summary>
    public TileState State { get; private set; } = Initial;

    /// <summary>Read on the UI thread. Set while the tile shows the error look.</summary>
    public TileError? LastError { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Creates the connector and starts it. Call once; RestartAsync replaces it later.</summary>
    public Task StartAsync()
    {
        int generation;
        lock (_instanceLock) generation = ++_generation;
        return LaunchAsync(generation);
    }

    /// <summary>"Restart tile": replaces the connector now and resets the backoff count.</summary>
    public Task RestartAsync()
    {
        if (IsDisposed) return Task.CompletedTask;
        _ui.Post(() => _failures = 0);
        return RestartCoreAsync();
    }

    async Task RestartCoreAsync()
    {
        Instance? old;
        int generation;
        lock (_instanceLock)
        {
            if (IsDisposed) return;
            old = _current;
            _current = null;
            generation = ++_generation;
        }
        _log.Info($"Restarting tile '{TileId}' ({ConnectorId})");
        Post(new Pending(generation, Restarting, null, Success: false));
        if (old is not null) await DisposeInstanceAsync(old).ConfigureAwait(false);
        await LaunchAsync(generation).ConfigureAwait(false);
    }

    async Task LaunchAsync(int generation)
    {
        Instance instance;
        try
        {
            // The connector's constructor is plugin code: keep it off the caller (UI) thread.
            var connector = await Task.Run(_createConnector).ConfigureAwait(false);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var context = new TileContext(this, generation, _settings, _log, _options.ConfigDirectory);
            instance = new Instance(generation, connector, context, cts);
        }
        catch (Exception ex)
        {
            Fail(ex, generation);
            return;
        }

        bool stale;
        lock (_instanceLock)
        {
            stale = IsDisposed || generation != _generation;
            if (!stale) _current = instance;
        }
        if (stale)
        {
            await DisposeInstanceAsync(instance).ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.Run(() => instance.Connector.StartAsync(instance.Context, instance.Cts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex, generation);
        }
    }

    public async Task InvokeActionAsync(string actionId)
    {
        if (IsDisposed || Volatile.Read(ref _current) is not { } instance) return;
        // Repeated clicks on the same button must not pile up connector calls.
        if (!_actionsInFlight.TryAdd(actionId, 0)) return;
        try
        {
            await RunWithTimeout(instance, async ct => { await instance.Connector.OnActionAsync(actionId, ct); return true; });
        }
        catch (TimeoutException) { ShowNotResponding(); }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex, instance.Generation); }
        finally { _actionsInFlight.TryRemove(actionId, out _); }
    }

    public async Task<FlyoutResult> GetFlyoutAsync()
    {
        if (IsDisposed) return FlyoutResult.Unavailable;
        if (LastError is { } error) return FlyoutResult.Of(ErrorFlyout(error));
        if (Volatile.Read(ref _current) is not { } instance) return FlyoutResult.Unavailable;
        try
        {
            var flyout = await RunWithTimeout(instance, ct => instance.Connector.GetFlyoutAsync(ct));
            return flyout is null ? FlyoutResult.NoPopup : FlyoutResult.Of(flyout);
        }
        catch (TimeoutException) { ShowNotResponding(); return FlyoutResult.Unavailable; }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { return FlyoutResult.Unavailable; }
        catch (Exception ex)
        {
            if (IsDisposed) return FlyoutResult.Unavailable;
            Fail(ex, instance.Generation);
            return FlyoutResult.Of(ErrorFlyout(TileError.From(ex)));
        }
    }

    /// <summary>Plugins never run on the UI thread, so handlers are raised on the thread pool.</summary>
    public void RaiseResumed()
    {
        if (IsDisposed || Volatile.Read(ref _current) is not { } instance) return;
        _ = Task.Run(() => instance.Context.RaiseResumed(ex =>
            _log.Warn($"Tile '{TileId}' ({ConnectorId}) failed in a Resumed handler: {ex.Message}")));
    }

    /// <summary>Called by TileContext.Update from any thread.</summary>
    internal void Enqueue(int generation, TileState state) =>
        Post(new Pending(generation, Normalize(state), null, Success: true));

    /// <summary>Called by TileContext.ReportError from any thread.</summary>
    internal void Report(int generation, string? message, string? details)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "The connector reported an error." : message.Trim();
        Post(new Pending(generation, ErrorState(text), new TileError(text, details ?? "", Fatal: false), Success: false));
    }

    /// <summary>Plugin output is untrusted: make a malformed state renderable instead of letting it blank the bar.</summary>
    internal static TileState Normalize(TileState? state)
    {
        if (state is null) return Initial;

        static string Glyph(string? g) => string.IsNullOrEmpty(g) ? "info" : g;

        var buttons = (state.Buttons ?? [])
            .Where(b => b is not null)
            .Select(b => string.IsNullOrEmpty(b.Glyph) ? b with { Glyph = Glyph(b.Glyph) } : b)
            .ToList();
        var icon = state.Icon is { } i && string.IsNullOrEmpty(i.Glyph) ? i with { Glyph = Glyph(i.Glyph) } : state.Icon;
        double? progress = state.Progress is { } p && double.IsFinite(p) ? p : null;
        List<double>? spark = null;
        if (state.Sparkline is { } raw)
        {
            var finite = raw.Where(double.IsFinite).ToList();
            if (finite.Count >= 2 && double.IsFinite(finite.Max() - finite.Min())) spark = finite;
        }

        return state with { Buttons = buttons, Icon = icon, Progress = progress, Sparkline = spark };
    }

    /// <summary>Latest wins: only the newest pending state of the current generation is applied.</summary>
    void Post(Pending pending)
    {
        if (IsDisposed || pending.Generation != Volatile.Read(ref _generation)) return;
        Volatile.Write(ref _pending, pending);
        if (Interlocked.Exchange(ref _postQueued, 1) != 0) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _postQueued, 0);
            var latest = Interlocked.Exchange(ref _pending, null);
            if (latest is null || IsDisposed || latest.Generation != Volatile.Read(ref _generation)) return;
            Apply(latest);
        });
    }

    // UI thread.
    void Apply(Pending pending)
    {
        LastError = pending.Error;
        if (pending.Success) _failures = 0;
        if (pending.Error is { Fatal: true }) ScheduleRestart(pending.Generation);
        SetState(pending.State);
    }

    // UI thread.
    void ScheduleRestart(int generation)
    {
        var delay = _options.Backoff.Delay(_failures++);
        _log.Info($"Tile '{TileId}' ({ConnectorId}) restarts in {delay.TotalSeconds:0.##} s");
        _ = Task.Delay(delay, _lifetime.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || IsDisposed || generation != Volatile.Read(ref _generation)) return;
            _ = RestartCoreAsync();
        }, TaskScheduler.Default);
    }

    async Task<T> RunWithTimeout<T>(Instance instance, Func<CancellationToken, Task<T>> call)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(instance.Cts.Token);
        var cancelOffThread = false;
        try
        {
            return await Task.Run(() => call(cts.Token)).WaitAsync(_options.CallTimeout);
        }
        catch (TimeoutException)
        {
            // Cancel callbacks are plugin code and run inline, so never on the awaiting (UI) thread.
            // The task also owns disposing the source, after the cancel has run.
            cancelOffThread = true;
            _ = Task.Run(() =>
            {
                try { cts.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                finally { cts.Dispose(); }
            });
            throw;
        }
        finally
        {
            if (!cancelOffThread) cts.Dispose();
        }
    }

    void Fail(Exception ex, int generation)
    {
        if (IsDisposed) return;
        _log.Error($"Tile '{TileId}' ({ConnectorId}) failed", ex);
        Post(new Pending(generation, ErrorState(ex.Message), TileError.From(ex), Success: false));
    }

    void ShowNotResponding()
    {
        if (IsDisposed) return;
        _log.Warn($"Tile '{TileId}' ({ConnectorId}) did not answer within {_options.CallTimeout.TotalSeconds:0} s");
        var generation = Interlocked.Increment(ref _notRespondingGeneration);
        _ui.Post(() =>
        {
            if (IsDisposed) return;
            // Remember what was shown before (error state included); overlapping timeouts keep the first.
            if (!ReferenceEquals(State, NotResponding)) _beforeNotResponding = State;
            SetState(NotResponding);
        });
        _ = Task.Delay(_options.NotRespondingFor).ContinueWith(_ => _ui.Post(() =>
        {
            if (IsDisposed) return;
            if (generation == Volatile.Read(ref _notRespondingGeneration) && ReferenceEquals(State, NotResponding))
                SetState(_beforeNotResponding);
        }), TaskScheduler.Default);
    }

    void SetState(TileState state)
    {
        State = state;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error($"Tile '{TileId}' ({ConnectorId}) has a failing StateChanged subscriber", ex);
        }
    }

    static TileState ErrorState(string message) => new()
    {
        Title = "Error",
        Subtitle = message,
        Icon = new TileIcon("warning", TileColor.Warn),
        Tooltip = message,
    };

    static FlyoutState ErrorFlyout(TileError error)
    {
        var text = string.IsNullOrWhiteSpace(error.Details) ? error.Message : error.Details;
        var rows = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .Select(line => new FlyoutRow(line))
            .ToList();
        return new FlyoutState("Tile error", error.Message, rows,
            [new FlyoutAction(RestartActionId, "Restart tile", Primary: true), new FlyoutAction(ShowLogActionId, "Open log")]);
    }

    async Task DisposeInstanceAsync(Instance instance)
    {
        try
        {
            // Cancellation callbacks and disposal are plugin code: keep them off the caller (UI) thread.
            await Task.Run(async () =>
            {
                try { instance.Cts.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                await instance.Connector.DisposeAsync().ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
        // instance.Cts is deliberately not disposed: calls may still be in flight and read its token.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Instance? instance;
        lock (_instanceLock)
        {
            instance = _current;
            _current = null;
        }
        try
        {
            // Cancellation callbacks and the connector's disposal are plugin code: keep them off the caller (UI) thread.
            await Task.Run(async () =>
            {
                try { _lifetime.Cancel(); }
                catch (Exception ex) { _log.Warn($"Tile '{TileId}' cancellation handler failed: {ex.Message}"); }
                if (instance is not null) await instance.Connector.DisposeAsync().ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
        // _lifetime is deliberately not disposed: calls may still be in flight and read its token.
    }
}
```

- [ ] **Step 7: Give HostController a connector factory**

Replace the whole content of `src/TaskbarTiles.Host/HostController.cs` with:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetTiles(IReadOnlyList<TileRuntime> runtimes);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display; one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar,
    string? configDirectory = null) : IAsyncDisposable
{
    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    void Sync()
    {
        if (_disposed) return;
        var cfg = config();
        var displays = taskbars.Current.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        var layouts = displays.ToDictionary(d => d.DisplayId,
            d => DisplayResolver.VisibleTileIds(DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary)));

        SyncRuntimes(cfg, layouts.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            try { _bars[gone].Close(); }
            catch (Exception ex) { logFor("host").Error($"Closing bar for display '{gone}' failed", ex); }
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            try
            {
                if (!_bars.TryGetValue(info.DisplayId, out var bar))
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                bar.SetTiles(layouts[info.DisplayId].Select(id => _runtimes[id].Runtime).ToList());
                bar.Apply(info);
            }
            catch (Exception ex)
            {
                logFor("host").Error($"Updating bar for display '{info.DisplayId}' failed", ex);
            }
        }
    }

    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        foreach (var (id, entry) in _runtimes.ToList())
        {
            var keep = wanted.Contains(id) && cfg.Tiles.TryGetValue(id, out var now) && SameConfig(now, entry.Config);
            if (keep) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var id in wanted)
        {
            if (_runtimes.ContainsKey(id) || !cfg.Tiles.TryGetValue(id, out var tileCfg)) continue;

            var connectorId = tileCfg.Connector;
            // The runtime calls this on the thread pool (plugin constructors are plugin code) and again for every restart.
            // A throwing constructor shows the error look and is retried with backoff.
            Func<ITileConnector> create = () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId);
            var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
            var runtime = new TileRuntime(id, connectorId, width, create, tileCfg.Settings, ui, logFor($"tile-{id}"), _runtimeOptions);
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    /// <summary>
    /// Bars (WPF windows) are closed synchronously before the first await, so call this on the UI thread.
    /// Everything after the first await is off-thread safe (ConfigureAwait(false)).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var bar in _bars.Values)
        {
            try { bar.Close(); }
            catch (Exception ex) { logFor("host").Error("Closing a bar failed", ex); }
        }
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
```

- [ ] **Step 8: Stop wrapping plugin constructor exceptions**

`Activator.CreateInstance` wraps a throwing constructor in `TargetInvocationException`, whose message would end up on the tile. In `src/TaskbarTiles.Host/Plugins/PluginLoader.cs`, replace lines 15-16:
```csharp
    public ITileConnector? Create(string id) =>
        connectors.TryGetValue(id, out var d) ? (ITileConnector)Activator.CreateInstance(d.Type)! : null;
```
with:
```csharp
    /// <summary>Runs the plugin's constructor; its exception propagates unwrapped.</summary>
    public ITileConnector? Create(string id) =>
        connectors.TryGetValue(id, out var d)
            ? (ITileConnector)Activator.CreateInstance(d.Type,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions, binder: null, args: null, culture: null)!
            : null;
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "TileRuntimeRestartTests|TileRuntimeTests|HostControllerTests"`
Expected: `Failed: 0`; the 14 `TileRuntimeRestartTests` and both new `HostControllerTests` pass.

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0` (the media plugin compiles unchanged: it only consumes `ITileContext`).

- [ ] **Step 10: Commit**

```bash
git add src/TaskbarTiles.Contracts/ITileContext.cs src/TaskbarTiles.Host/Runtime src/TaskbarTiles.Host/HostController.cs src/TaskbarTiles.Host/Plugins/PluginLoader.cs tests/TaskbarTiles.Tests
git commit -m "feat(host): restart failing connectors with backoff, add ReportError and ConfigDirectory" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 3: Tile commands and the per-tile context menu

**Files:**
- Create: `src/TaskbarTiles.Host/UI/TileCommands.cs`
- Modify (whole files): `src/TaskbarTiles.Host/UI/TileBar.cs`, `src/TaskbarTiles.Host/UI/TileView.xaml.cs`, `src/TaskbarTiles.Host/UI/FlyoutWindow.cs`, `src/TaskbarTiles.Host/App.xaml.cs`
- Test: `tests/TaskbarTiles.Tests/TileCommandsTests.cs` (new)

**Interfaces:**
- Consumes: `TileRuntime.RestartAsync()`, `TileRuntime.InvokeActionAsync(string)`, `TileRuntime.LastError : TileError?`, `TileRuntime.RestartActionId`, `TileRuntime.ShowLogActionId` (Task 2); `TileActions.Refresh` (Task 1); `FileLog.Sanitize(string)` (internal, `Runtime/Logs.cs`)
- Produces (namespace `TaskbarTiles.Host.UI`):
  - `enum TileCommand { Configure, Refresh, ShowLog, Restart }`, `enum BarCommand { OpenConfig, Exit }`
  - `sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null)` with `static MenuEntry Separator`, `bool IsSeparator`
  - `static class TileMenu { static IReadOnlyList<MenuEntry> Entries(bool onTile) }`
  - `sealed class TileCommands(string configFile, string logsDir, Action<string> openFile, ITileLog log)` with `string LogPathFor(string tileId)`, `static bool TryFromActionId(string actionId, out TileCommand command)`, `Task InvokeAsync(TileRuntime runtime, string actionId)`, `Task ExecuteAsync(TileRuntime runtime, TileCommand command)`, `void OpenConfig()`
  - `TileBar(string displayId, ITileLog log, TileCommands commands)` (the `OpenConfigRequested` event is gone; `ExitRequested` stays)
  - `TileView(TileRuntime runtime, Func<bool> isLightTheme, TileCommands commands, ITileLog log)`
  - `FlyoutWindow.ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light, TileCommands commands, ITileLog log)`
  - `App` field `TileCommands? _commands`, helper `static void OpenWithShell(string path)` (Task 4 and Task 11 edit this file again)

Behavior:
- Right-click on a tile: Configure connector… (opens `config.json`), Refresh now (sends `"refresh"`; on a tile whose connector crashed it restarts the tile instead), Show connector log (opens `logs\tile-{id}.log`, creating it if missing), Restart tile, separator, Open config file, separator, Exit Taskbar Tiles. Right-click on the bar outside a tile: only the last three.
- Not in this milestone: Move left/right, Add to stack / Pause rotation, Edit layout, Remove tile (milestone 3, with edit mode).
- The error popup's actions (`host:restart`, `host:show-log`) are handled by `TileCommands`, never sent to the connector.
- UI exceptions that M1 sent to `Debug.WriteLine` now go to `logs\host.log`.

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/TileCommandsTests.cs`:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileCommandsTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    sealed class Fixture : IDisposable
    {
        readonly List<FakeConnector> _connectors = [];

        public Fixture(Action<FakeConnector, int>? setup = null)
        {
            Commands = new TileCommands(Dir.File("config.json"), Path.Combine(Dir.Path, "logs"), p => Opened.Add(p), Log);
            Runtime = new TileRuntime("tile1", "fake", 132, () =>
            {
                var c = new FakeConnector();
                lock (_connectors)
                {
                    setup?.Invoke(c, _connectors.Count);
                    _connectors.Add(c);
                }
                return c;
            }, NoSettings, new ImmediateDispatcher(), Log, new TileRuntimeOptions { Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]) });
        }

        public TempDir Dir { get; } = new();
        public List<string> Opened { get; } = [];
        public CollectingLog Log { get; } = new();
        public TileCommands Commands { get; }
        public TileRuntime Runtime { get; }
        public int Created { get { lock (_connectors) return _connectors.Count; } }
        public FakeConnector First { get { lock (_connectors) return _connectors[0]; } }
        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void Tile_menu_lists_tile_commands_then_bar_commands() =>
        Assert.Equal(
            new[] { "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "", "Open config file", "", "Exit Taskbar Tiles" },
            TileMenu.Entries(onTile: true).Select(e => e.Header));

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(onTile: false);

        Assert.Equal(new[] { "Open config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.OpenConfig, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.Exit, entries[2].Bar);
    }

    [Fact]
    public async Task Configure_opens_the_config_file()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Configure);

        Assert.Equal(new[] { f.Dir.File("config.json") }, f.Opened);
    }

    [Fact]
    public async Task Show_log_creates_and_opens_the_tile_log()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowLog);

        var expected = Path.Combine(f.Dir.Path, "logs", "tile-tile1.log");
        Assert.Equal(new[] { expected }, f.Opened);
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, f.Commands.LogPathFor("tile1"));
    }

    [Fact]
    public async Task Error_popup_actions_become_commands()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.InvokeAsync(f.Runtime, TileRuntime.ShowLogActionId);
        await f.Commands.InvokeAsync(f.Runtime, TileRuntime.RestartActionId);

        Assert.Single(f.Opened);
        Assert.Equal(2, f.Created);
        Assert.Empty(f.First.Actions);
    }

    [Fact]
    public async Task Other_action_ids_reach_the_connector()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.InvokeAsync(f.Runtime, "next");

        Assert.Equal(new[] { "next" }, f.First.Actions);
    }

    [Fact]
    public async Task Refresh_sends_refresh_to_the_connector()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Refresh);

        Assert.Equal(new[] { TileActions.Refresh }, f.First.Actions);
    }

    [Fact]
    public async Task Refresh_on_a_crashed_tile_restarts_it()
    {
        using var f = new Fixture((c, before) =>
        {
            if (before == 0) c.OnStart = (_, _) => throw new InvalidOperationException("boom");
        });
        await f.Runtime.StartAsync();
        Assert.True(f.Runtime.LastError!.Fatal);

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Refresh);

        Assert.Equal(2, f.Created);
        Assert.Empty(f.First.Actions);
    }

    [Fact]
    public async Task Failing_open_is_logged_not_thrown()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();
        var commands = new TileCommands(f.Dir.File("config.json"), f.Dir.Path, _ => throw new InvalidOperationException("no editor"), f.Log);

        await commands.ExecuteAsync(f.Runtime, TileCommand.Configure);
        commands.OpenConfig();

        Assert.Equal(2, f.Log.Snapshot().Count(m => m.StartsWith("ERROR") && m.Contains("no editor")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter TileCommandsTests`
Expected: build FAILS with `The type or namespace name 'TileCommands' could not be found`.

- [ ] **Step 3: Implement TileCommands and the menu model**

`src/TaskbarTiles.Host/UI/TileCommands.cs`:
```csharp
using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Commands on one tile, from its context menu or its error popup.</summary>
public enum TileCommand { Configure, Refresh, ShowLog, Restart }

/// <summary>Commands on the whole app.</summary>
public enum BarCommand { OpenConfig, Exit }

/// <summary>One context-menu line; an entry with neither command is a separator.</summary>
public sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null)
{
    public static MenuEntry Separator { get; } = new("");
    public bool IsSeparator => Tile is null && Bar is null;
}

public static class TileMenu
{
    /// <summary>
    /// The right-click menu. Move left/right, Add to stack / Pause rotation, Edit layout and Remove tile
    /// need config writing and come with edit mode in milestone 3.
    /// </summary>
    public static IReadOnlyList<MenuEntry> Entries(bool onTile)
    {
        var entries = new List<MenuEntry>();
        if (onTile)
        {
            entries.Add(new("Configure connector…", TileCommand.Configure));
            entries.Add(new("Refresh now", TileCommand.Refresh));
            entries.Add(new("Show connector log", TileCommand.ShowLog));
            entries.Add(new("Restart tile", TileCommand.Restart));
            entries.Add(MenuEntry.Separator);
        }
        entries.Add(new("Open config file", Bar: BarCommand.OpenConfig));
        entries.Add(MenuEntry.Separator);
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }
}

/// <summary>Executes tile commands. Called on the UI thread; never throws.</summary>
public sealed class TileCommands(string configFile, string logsDir, Action<string> openFile, ITileLog log)
{
    /// <summary>Same file FileLog.For(logsDir, $"tile-{id}") writes.</summary>
    public string LogPathFor(string tileId) => Path.Combine(logsDir, FileLog.Sanitize($"tile-{tileId}") + ".log");

    public static bool TryFromActionId(string actionId, out TileCommand command)
    {
        switch (actionId)
        {
            case TileRuntime.RestartActionId: command = TileCommand.Restart; return true;
            case TileRuntime.ShowLogActionId: command = TileCommand.ShowLog; return true;
            default: command = default; return false;
        }
    }

    /// <summary>Popup and button actions: the host's own action ids become commands, everything else goes to the connector.</summary>
    public Task InvokeAsync(TileRuntime runtime, string actionId) =>
        TryFromActionId(actionId, out var command) ? ExecuteAsync(runtime, command) : runtime.InvokeActionAsync(actionId);

    public async Task ExecuteAsync(TileRuntime runtime, TileCommand command)
    {
        try
        {
            switch (command)
            {
                case TileCommand.Configure:
                    Open(configFile);
                    break;
                case TileCommand.ShowLog:
                    Open(EnsureExists(LogPathFor(runtime.TileId)));
                    break;
                case TileCommand.Restart:
                    await runtime.RestartAsync();
                    break;
                case TileCommand.Refresh:
                    // A crashed connector cannot refresh; a restart is what the user wants.
                    if (runtime.LastError is { Fatal: true }) await runtime.RestartAsync();
                    else await runtime.InvokeActionAsync(TileActions.Refresh);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"Command {command} on tile '{runtime.TileId}' failed", ex);
        }
    }

    public void OpenConfig() => Open(configFile);

    void Open(string path)
    {
        try { openFile(path); }
        catch (Exception ex) { log.Error($"Could not open {path}", ex); }
    }

    static string EnsureExists(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter TileCommandsTests`
Expected: `Passed: 9`.

- [ ] **Step 5: Wire the commands into the UI**

Replace the whole content of `src/TaskbarTiles.Host/UI/TileView.xaml.cs` with:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Renders one tile's TileState and turns clicks into flyouts or actions.</summary>
public partial class TileView : UserControl
{
    readonly Func<bool> _isLightTheme;
    readonly TileCommands _commands;
    readonly ITileLog _log;
    bool _urgent;
    bool _clickInFlight;
    SolidColorBrush? _pulse;

    public TileView(TileRuntime runtime, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        InitializeComponent();
        Runtime = runtime;
        _isLightTheme = isLightTheme;
        _commands = commands;
        _log = log;
        Width = runtime.Width;
        MouseLeftButtonUp += OnClick;
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        KeyDown += (_, e) => { if (e.Key == Key.Enter) OnClick(this, null); };
        Render(runtime.State);
        // Subscribe last: a constructor that throws must not leave a subscription behind.
        Runtime.StateChanged += OnStateChanged;
    }

    public TileRuntime Runtime { get; }

    public void Detach()
    {
        Runtime.StateChanged -= OnStateChanged;
        StopPulse();
    }

    void OnStateChanged(object? sender, EventArgs e) => Render(Runtime.State);

    void Render(TileState s)
    {
        TitleText.Text = s.Title ?? "";
        SubtitleText.Text = s.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(s.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = string.IsNullOrEmpty(s.Tooltip) ? null : s.Tooltip;

        var picture = Theme.Decode(s.Image);
        Picture.Source = picture;
        IconGlyph.Text = picture is null && s.Icon is { } icon ? Theme.Glyph(icon.Glyph) : "";
        IconBackground.Background = picture is null && s.Icon is { } i2 ? Theme.ColorFor(i2.Color) : Brushes.Transparent;
        IconHost.Visibility = picture is null && s.Icon is null ? Visibility.Collapsed : Visibility.Visible;

        ButtonPanel.Children.Clear();
        foreach (var b in s.Buttons)
        {
            var button = new Button
            {
                Content = Theme.Glyph(b.Glyph),
                ToolTip = b.Tooltip,
                IsEnabled = b.Enabled,
                Style = (Style)FindResource("IconButton"),
            };
            var id = b.Id;
            // TileRuntime drops a click on an action id that is still running.
            button.Click += async (_, _) =>
            {
                try { await Runtime.InvokeActionAsync(id); }
                catch (Exception ex) { _log.Error($"Tile '{Runtime.TileId}' button '{id}' failed", ex); }
            };
            ButtonPanel.Children.Add(button);
        }

        RenderSparkline(s.Sparkline);

        if (s.Progress is { } p)
        {
            var done = Math.Clamp(p, 0, 1);
            ProgressDone.Width = new GridLength(done, GridUnitType.Star);
            ProgressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
            ProgressTrack.Visibility = Visibility.Visible;
        }
        else
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
        }

        var wasUrgent = _urgent;
        _urgent = s.Urgent;
        if (_urgent && !wasUrgent) StartPulse();
        else if (!_urgent && wasUrgent) StopPulse();
        UpdateBackground();
    }

    void RenderSparkline(IReadOnlyList<double>? values)
    {
        if (values is not { Count: > 1 })
        {
            SparkHost.Visibility = Visibility.Collapsed;
            return;
        }

        double min = values.Min(), max = values.Max(), range = max - min;
        double w = SparkHost.Width, h = SparkHost.Height;
        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var y = range <= 0 ? h / 2 : h - 2 - (values[i] - min) / range * (h - 4);
            points.Add(new Point(i * w / (values.Count - 1), y));
        }
        Spark.Points = points;
        SparkHost.Visibility = Visibility.Visible;
    }

    void StartPulse()
    {
        var bad = ((SolidColorBrush)Theme.ColorFor(TileColor.Bad)).Color;
        _pulse ??= new SolidColorBrush(Color.FromArgb(0x10, bad.R, bad.G, bad.B));
        Root.Background = _pulse;
        Root.BorderBrush = Theme.ColorFor(TileColor.Bad);
        if (SystemParameters.ClientAreaAnimation)
            _pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x30, bad.R, bad.G, bad.B),
                TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    void StopPulse() => _pulse?.BeginAnimation(SolidColorBrush.ColorProperty, null);

    void UpdateBackground()
    {
        if (_urgent) return; // pulse owns the background while urgent
        Root.SetResourceReference(Border.BackgroundProperty, IsMouseOver ? "TileHover" : "TileFill");
        Root.SetResourceReference(Border.BorderBrushProperty, "TileOutline");
    }

    async void OnClick(object sender, MouseButtonEventArgs? e)
    {
        if (e?.Handled == true || _clickInFlight) return;
        _clickInFlight = true;
        try
        {
            var result = await Runtime.GetFlyoutAsync();
            switch (result.Outcome)
            {
                case FlyoutOutcome.NoPopup:
                    await Runtime.InvokeActionAsync(TileActions.Click);
                    break;
                case FlyoutOutcome.Popup:
                    FlyoutWindow.ShowAbove(this, result.Flyout!, Runtime, _isLightTheme(), _commands, _log);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Tile '{Runtime.TileId}' click failed", ex);
        }
        finally
        {
            _clickInFlight = false;
        }
    }
}
```

Replace the whole content of `src/TaskbarTiles.Host/UI/FlyoutWindow.cs` with:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>The popup above a tile. Only one is open at a time; it closes when it loses focus.</summary>
public static class FlyoutWindow
{
    const double WidthDip = 320;
    static Window? _open;

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log)
    {
        CloseOpen();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = WidthDip,
            UseLayoutRounding = true,
            Opacity = 0, // hidden until positioned on the target monitor at the right DPI
        };
        Theme.ApplyTo(window.Resources, light);
        window.Content = Build(state, runtime, commands, log);
        window.Deactivated += (_, _) => { if (_open == window) CloseOpen(); };
        window.Loaded += (_, _) =>
        {
            try { Position(window, anchor); }
            catch (Exception ex) { log.Error("Flyout positioning failed", ex); }
            finally { window.Opacity = 1; }
        };
        _open = window;
        window.Show();
        window.Activate();
    }

    static UIElement Build(FlyoutState state, TileRuntime runtime, TileCommands commands, ITileLog log)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (Theme.Decode(state.Image, 144) is { } img)
            header.Children.Add(new Border
            {
                Width = 72, Height = 72, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 12, 0),
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(state.Title, 14, FontWeights.SemiBold, "TileForeground"));
        if (!string.IsNullOrEmpty(state.Subtitle)) titles.Children.Add(Text(state.Subtitle, 12, FontWeights.Normal, "TileDim"));
        header.Children.Add(titles);
        panel.Children.Add(header);

        foreach (var row in state.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (row.Icon is { } icon)
                grid.Children.Add(new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = Theme.ColorFor(icon.Color), VerticalAlignment = VerticalAlignment.Center,
                });
            var text = Text(row.Text, 13, FontWeights.Normal, "TileForeground");
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (row.Value is not null)
            {
                var value = Text(row.Value, 11.5, FontWeights.Normal, "TileDim");
                value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                value.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(value, 2);
                grid.Children.Add(value);
            }
            panel.Children.Add(grid);
        }

        if (state.Actions.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var a in state.Actions)
            {
                var button = new Button
                {
                    Content = a.Label,
                    Style = (Style)Application.Current.FindResource(a.Primary ? "FlyoutPrimaryButton" : "FlyoutButton"),
                };
                var id = a.Id;
                // Host ids (Restart tile, Open log) become commands; the rest go to the connector.
                button.Click += async (_, _) =>
                {
                    try { CloseOpen(); await commands.InvokeAsync(runtime, id); }
                    catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
                };
                actions.Children.Add(button);
            }
            panel.Children.Add(actions);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return border;
    }

    static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>
    /// Centers the window above the anchor in physical pixels, 8 DIP above the taskbar, clamped to the work area
    /// of the monitor holding the anchor. The window starts at Opacity 0; moving it onto the target monitor may
    /// change its DPI (WPF rescales synchronously), so we re-measure and re-apply until the DPI is stable (max 3 passes).
    /// </summary>
    static void Position(Window window, FrameworkElement anchor)
    {
        var anchorSource = PresentationSource.FromVisual(anchor) as HwndSource;
        if (anchorSource?.CompositionTarget is null) return;
        var anchorScale = anchorSource.CompositionTarget.TransformToDevice.M11;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var anchorWidthPx = anchor.ActualWidth * anchorScale;

        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(anchorSource.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var haveWork = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info);

        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            int x = (int)(topLeft.X + anchorWidthPx / 2 - w / 2.0);
            int y = (int)(topLeft.Y - 4 * anchorScale - 8 * anchorScale - h); // anchor sits 4 DIP below the taskbar top
            if (haveWork)
            {
                x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - w));
                y = Math.Max(y, info.rcWork.Top);
            }
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);

            var after = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(after - dpi) < 0.001) break;
            dpi = after;
        }
    }
}
```

Replace the whole content of `src/TaskbarTiles.Host/UI/TileBar.cs` with:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>One per display: a borderless topmost strip sized to its visible tiles, placed in the free zone.</summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    readonly List<TileView> _views = [];
    readonly ITileLog _log;
    readonly TileCommands _commands;
    IReadOnlyList<TileRuntime> _requested = [];
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId, ITileLog log, TileCommands commands)
    {
        _log = log;
        _commands = commands;
        DisplayId = displayId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        // Bar-wide menu for the gaps between tiles; each tile gets its own menu in SetTiles.
        ContextMenu = BuildMenu(null);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the tiles' menus.
        ContextMenuOpening += (_, _) => { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public string DisplayId { get; }
    public event EventHandler? ExitRequested;

    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    public void SetTiles(IReadOnlyList<TileRuntime> runtimes)
    {
        if (_requested.SequenceEqual(runtimes)) return;
        _requested = runtimes.ToList();

        foreach (var v in _views) v.Detach();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var rt in runtimes)
        {
            TileView view;
            try
            {
                view = new TileView(rt, () => _light ?? false, _commands, _log) { Margin = new Thickness(0, 0, GapDip, 0) };
                view.ContextMenu = BuildMenu(rt);
            }
            catch (Exception ex)
            {
                _log.Error($"Tile '{rt.TileId}' could not be shown", ex);
                continue;
            }
            _views.Add(view);
            _panel.Children.Add(view);
        }
        Reflow();
    }

    public void Apply(TaskbarInfo info)
    {
        _info = info;
        if (_light != info.IsLightTheme)
        {
            _light = info.IsLightTheme;
            Theme.ApplyTo(Resources, info.IsLightTheme);
        }
        Reflow();
    }

    ContextMenu BuildMenu(TileRuntime? tile)
    {
        var menu = new ContextMenu();
        foreach (var entry in TileMenu.Entries(onTile: tile is not null))
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Header };
            if (entry.Tile is { } command && tile is not null)
                item.Click += async (_, _) => await _commands.ExecuteAsync(tile, command); // never throws
            else if (entry.Bar == BarCommand.OpenConfig)
                item.Click += (_, _) => _commands.OpenConfig();
            else if (entry.Bar == BarCommand.Exit)
                item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(item);
        }
        return menu;
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var widths = _views.Select(v => v.Width).ToList();
        var count = _info.Hidden || zone.IsEmpty ? 0 : FitTiles.Count(widths, zone.Width / scale, GapDip);
        for (var i = 0; i < _views.Count; i++)
        {
            _views[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            _views[i].Margin = new Thickness(0, 0, i < count - 1 ? GapDip : 0, 0);
        }

        if (count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        var totalDip = widths.Take(count).Sum() + GapDip * (count - 1);
        var w = (int)Math.Ceiling(totalDip * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
```

Replace the whole content of `src/TaskbarTiles.Host/App.xaml.cs` with:
```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    Mutex? _singleInstance;
    ConfigStore? _config;
    TaskbarTracker? _tracker;
    HostController? _host;
    TileCommands? _commands;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump-taskbars"))
        {
            DumpTaskbars();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\TaskbarTiles.Host", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        var log = FileLog.For(AppPaths.LogsDir, "host");
        try
        {
            DispatcherUnhandledException += (_, a) => { log.Error("Unhandled UI exception", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, a) => log.Error("Unhandled exception", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, a) => { log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

            var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
            _config.Load();
            _commands = new TileCommands(AppPaths.ConfigFile, AppPaths.LogsDir, OpenWithShell, log);

            var ui = new WpfUiDispatcher(Dispatcher);
            _tracker = new TaskbarTracker(Dispatcher);
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir);

            _config.Changed += (_, _) => ui.Post(() => _host.ConfigChanged());
            _config.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
        }
        catch (Exception ex)
        {
            log.Error("Startup failed", ex);
            Shutdown();
        }
    }

    static void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    ITileBarView CreateBar(string displayId)
    {
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!);
        bar.ExitRequested += (_, _) => Shutdown();
        return bar;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _host?.NotifyResumed());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        FlyoutWindow.CloseOpen();
        if (_host is not null)
        {
            // Bars close synchronously on this (UI) thread inside DisposeAsync before its first await;
            // the rest does not need the dispatcher (ConfigureAwait(false)), so wait off-thread with a bound.
            var disposal = _host.DisposeAsync().AsTask();
            try { Task.Run(() => disposal).Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
        _tracker?.Dispose();
        _config?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Diagnostics: writes what the tracker sees to %TEMP%\taskbartiles-dump.json and exits.</summary>
    void DumpTaskbars()
    {
        var tracker = new TaskbarTracker(Dispatcher);
        tracker.Changed += (_, _) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "taskbartiles-dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(tracker.Current, new JsonSerializerOptions { WriteIndented = true }));
            tracker.Dispose();
            Shutdown();
        };
        tracker.Start();
    }
}
```

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build TaskbarTiles.sln`
Expected: `Build succeeded`, no errors (no remaining reference to `OpenConfigRequested` or `Debug.WriteLine` in `src/TaskbarTiles.Host/UI`).

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/TaskbarTiles.Host/UI src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/TileCommandsTests.cs
git commit -m "feat(ui): per-tile context menu, error popup actions, UI errors to the host log" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Notification for a rejected config.json

**Files:**
- Create: `src/TaskbarTiles.Host/UI/ConfigNotice.cs`, `src/TaskbarTiles.Host/UI/NotificationWindow.cs`
- Modify: `src/TaskbarTiles.Host/Taskbar/NativeMethods.cs:28`, `src/TaskbarTiles.Host/App.xaml.cs`
- Test: `tests/TaskbarTiles.Tests/ConfigNoticeTests.cs` (new)

**Interfaces:**
- Consumes: `ConfigError` (`Config/ConfigParser.cs`; `ToString()` appends "(line L, column C)"), `ConfigStore.ReloadFailed`, `ConfigStore.LastError`, `ConfigStore.Changed`, `TileCommands.OpenConfig()` (Task 3), `Theme.ApplyTo`, `Theme.Glyph`, `Theme.ColorFor`
- Produces:
  - `static class ConfigNotice { const string Title = "config.json was not applied"; static string Body(ConfigError error, bool atStartup) }`
  - `static class NotificationWindow { static TimeSpan Lifetime (8 s); static void Show(string title, string body, bool light, Action onClick, ITileLog log); static void CloseOpen() }`
  - `NativeMethods.MONITOR_DEFAULTTOPRIMARY = 1`

Behavior: the spec's "toast with line and column" is a small window styled like the flyout, at the bottom-right of the primary monitor's work area (just above the taskbar), never activated, auto-dismissed after 8 s; clicking it opens `config.json`. It appears when an edit is rejected (`ReloadFailed`) and at startup when `LastError` is set; a later successful reload closes it.

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/ConfigNoticeTests.cs`:
```csharp
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class ConfigNoticeTests
{
    [Fact]
    public void Rejected_edit_names_line_and_column_and_keeps_the_last_config()
    {
        var body = ConfigNotice.Body(new ConfigError("Invalid JSON: unexpected end", 3, 5), atStartup: false);

        Assert.Contains("line 3, column 5", body);
        Assert.Contains("The last good config stays active.", body);
        Assert.EndsWith("Click to open config.json.", body);
    }

    [Fact]
    public void Startup_failure_says_no_tiles_are_shown()
    {
        var body = ConfigNotice.Body(new ConfigError("Invalid JSON: bad", 1, 2), atStartup: true);

        Assert.Contains("No tiles are shown until it is fixed.", body);
        Assert.Contains("line 1, column 2", body);
    }

    [Fact]
    public void Message_ending_with_a_period_is_not_doubled()
    {
        var body = ConfigNotice.Body(new ConfigError("Tile 'a' needs a \"connector\" name."), atStartup: false);

        Assert.DoesNotContain("..", body);
        Assert.StartsWith("Tile 'a' needs a \"connector\" name. ", body);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter ConfigNoticeTests`
Expected: build FAILS with `The name 'ConfigNotice' does not exist in the current context`.

- [ ] **Step 3: Implement ConfigNotice**

`src/TaskbarTiles.Host/UI/ConfigNotice.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.UI;

/// <summary>Text of the notification shown when config.json cannot be applied.</summary>
public static class ConfigNotice
{
    public const string Title = "config.json was not applied";

    public static string Body(ConfigError error, bool atStartup)
    {
        var what = error.ToString().TrimEnd().TrimEnd('.');
        var consequence = atStartup
            ? "No tiles are shown until it is fixed."
            : "The last good config stays active.";
        return $"{what}. {consequence} Click to open config.json.";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter ConfigNoticeTests`
Expected: `Passed: 3`.

- [ ] **Step 5: Add the notification window**

In `src/TaskbarTiles.Host/Taskbar/NativeMethods.cs`, replace line 28:
```csharp
    public const uint MONITOR_DEFAULTTONEAREST = 2;
```
with:
```csharp
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
```

`src/TaskbarTiles.Host/UI/NotificationWindow.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// A small notice at the bottom-right of the primary monitor's work area, styled like the flyout.
/// It never takes focus, closes itself after <see cref="Lifetime"/>, and runs onClick when clicked. One at a time.
/// </summary>
public static class NotificationWindow
{
    const double WidthDip = 340;
    const double MarginDip = 12;
    static Window? _open;

    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(8);

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    public static void Show(string title, string body, bool light, Action onClick, ITileLog log)
    {
        CloseOpen();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = WidthDip,
            UseLayoutRounding = true,
            Cursor = Cursors.Hand,
            Opacity = 0, // hidden until positioned on the primary monitor at its DPI
        };
        Theme.ApplyTo(window.Resources, light);
        window.Content = Build(title, body);

        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_open == window) CloseOpen();
        };
        window.Closed += (_, _) => timer.Stop();
        window.MouseLeftButtonUp += (_, _) =>
        {
            if (_open == window) CloseOpen();
            try { onClick(); }
            catch (Exception ex) { log.Error("Notification click failed", ex); }
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
        };
        window.Loaded += (_, _) =>
        {
            try { Position(window); }
            catch (Exception ex) { log.Error("Notification positioning failed", ex); }
            finally { window.Opacity = 1; }
        };

        _open = window;
        window.Show();
        timer.Start();
    }

    static UIElement Build(string title, string body)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = Theme.Glyph("warning"),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Theme.ColorFor(TileColor.Warn),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TileForeground");
        header.Children.Add(titleText);

        var bodyText = new TextBlock { Text = body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(bodyText);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return border;
    }

    /// <summary>Bottom-right of the primary work area in physical pixels; re-applied until the window's DPI is stable (max 3 passes).</summary>
    static void Position(Window window)
    {
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;

        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            var margin = (int)Math.Round(MarginDip * dpi);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, work.Right - w - margin, work.Bottom - h - margin, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            var after = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(after - dpi) < 0.001) break;
            dpi = after;
        }
    }
}
```

- [ ] **Step 6: Show it from App**

In `src/TaskbarTiles.Host/App.xaml.cs` (as written in Task 3), replace:
```csharp
            _config.Changed += (_, _) => ui.Post(() => _host.ConfigChanged());
            _config.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
```
with:
```csharp
            _config.Changed += (_, _) => ui.Post(() =>
            {
                NotificationWindow.CloseOpen(); // a notice about an earlier bad edit is stale now
                _host.ConfigChanged();
            });
            _config.ReloadFailed += (_, error) => ui.Post(() => ShowConfigNotice(error, atStartup: false));
            _config.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            if (_config.LastError is { } startupError) ShowConfigNotice(startupError, atStartup: true);
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
```

Replace:
```csharp
    static void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
```
with:
```csharp
    static void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    void ShowConfigNotice(ConfigError error, bool atStartup)
    {
        var light = _tracker?.Current.FirstOrDefault(t => t.IsPrimary)?.IsLightTheme ?? false;
        NotificationWindow.Show(ConfigNotice.Title, ConfigNotice.Body(error, atStartup), light,
            () => _commands?.OpenConfig(), FileLog.For(AppPaths.LogsDir, "host"));
    }
```

Replace:
```csharp
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        FlyoutWindow.CloseOpen();
```
with:
```csharp
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        FlyoutWindow.CloseOpen();
        NotificationWindow.CloseOpen();
```

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build TaskbarTiles.sln`
Expected: `Build succeeded`.

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

The window itself is checked by the user in Task 12.

- [ ] **Step 8: Commit**

```bash
git add src/TaskbarTiles.Host/UI/ConfigNotice.cs src/TaskbarTiles.Host/UI/NotificationWindow.cs src/TaskbarTiles.Host/Taskbar/NativeMethods.cs src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/ConfigNoticeTests.cs
git commit -m "feat(ui): notify when config.json is rejected, with line and column" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 5: Script plugin project and hook settings

**Files:**
- Create: `src/TaskbarTiles.Plugins.Script/TaskbarTiles.Plugins.Script.csproj`, `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`
- Modify: `TaskbarTiles.sln` (via `dotnet sln add`), `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj:16-20`, `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj:15-18`
- Test: `tests/TaskbarTiles.Tests/ScriptSettingsTests.cs` (new)

**Interfaces:**
- Consumes: `Duration.TryParse` (Task 1)
- Produces (namespace `TaskbarTiles.Plugins.Script`, assembly internals visible to `TaskbarTiles.Tests`):
  - `sealed record HookSpec(string Run, TimeSpan? Every = null, TimeSpan? Timeout = null)`
  - `sealed class ScriptSettings` with `static readonly TimeSpan DefaultTimeout` (60 s), `IReadOnlyDictionary<string, HookSpec> Hooks`, `IReadOnlyList<string> Errors`, `HookSpec? Hook(string name)`, `HookSpec? Action(string actionId)`, `static ScriptSettings Parse(JsonElement settings)`
  - Hook names: `interval` (needs `every` > 0), `watch`, `click`, `flyout`, `startup`, `resume`, `action:<id>`. A hook is `{ "run": "...", "timeout": "20s"? }` (`interval` also `every`) or, except `interval`, a plain string meaning `run`.
  - Host build output contains `plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll`.

- [ ] **Step 1: Create the project**

Run from the repo root:
```bash
dotnet new classlib -n TaskbarTiles.Plugins.Script -o src/TaskbarTiles.Plugins.Script -f net9.0
rm src/TaskbarTiles.Plugins.Script/Class1.cs
dotnet sln TaskbarTiles.sln add src/TaskbarTiles.Plugins.Script/TaskbarTiles.Plugins.Script.csproj
```
Expected: the project appears under the `src` solution folder (same as `TaskbarTiles.Plugins.Media`).

Replace the whole content of `src/TaskbarTiles.Plugins.Script/TaskbarTiles.Plugins.Script.csproj` with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides Contracts at runtime; don't ship a copy with the plugin. -->
    <ProjectReference Include="..\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" Private="false" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TaskbarTiles.Tests" />
  </ItemGroup>
</Project>
```

In `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, replace lines 16-20:
```xml
  <ItemGroup>
    <!-- Build order only; copied to plugins\TaskbarTiles.Plugins.Media\ by CopyPlugins.targets. -->
    <ProjectReference Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" />
  </ItemGroup>
```
with:
```xml
  <ItemGroup>
    <!-- Build order only; each is copied to plugins\<AssemblyName>\ by CopyPlugins.targets. -->
    <ProjectReference Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" />
    <ProjectReference Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
```

In `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`, replace lines 15-18:
```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" />
    <ProjectReference Include="..\..\src\TaskbarTiles.Host\TaskbarTiles.Host.csproj" />
  </ItemGroup>
```
with:
```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" />
    <ProjectReference Include="..\..\src\TaskbarTiles.Host\TaskbarTiles.Host.csproj" />
    <!-- Referenced directly so its pure parts can be unit-tested. -->
    <ProjectReference Include="..\..\src\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/ScriptSettingsTests.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ScriptSettingsTests
{
    static ScriptSettings Parse(string json) => ScriptSettings.Parse(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Parses_every_hook_kind()
    {
        var s = Parse("""
            { "hooks": {
                "interval": { "every": "30s", "run": "scripts/a.ps1" },
                "watch": { "run": "scripts/w.ps1" },
                "click": { "run": "scripts/c.ps1" },
                "flyout": { "run": "scripts/f.ps1", "timeout": "5s" },
                "startup": { "run": "scripts/s.ps1" },
                "resume": { "run": "scripts/r.ps1" },
                "action:rerun": { "run": "scripts/rerun.ps1 --fast" } } }
            """);

        Assert.Empty(s.Errors);
        Assert.Equal(7, s.Hooks.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), s.Hook("interval")!.Every);
        Assert.Equal("scripts/a.ps1", s.Hook("interval")!.Run);
        Assert.Equal("scripts/w.ps1", s.Hook("watch")!.Run);
        Assert.Null(s.Hook("watch")!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Hook("flyout")!.Timeout);
        Assert.Equal("scripts/rerun.ps1 --fast", s.Action("rerun")!.Run);
        Assert.Null(s.Action("other"));
        Assert.Null(s.Hook("nope"));
    }

    [Fact]
    public void A_string_is_shorthand_for_run()
    {
        var s = Parse("""{ "hooks": { "click": "scripts/c.ps1", "action:go": " scripts/go.ps1 " } }""");

        Assert.Empty(s.Errors);
        Assert.Equal(new HookSpec("scripts/c.ps1"), s.Hook("click"));
        Assert.Equal("scripts/go.ps1", s.Action("go")!.Run);
    }

    [Theory]
    [InlineData("""{ }""", "settings.hooks")]
    [InlineData("""{ "hooks": [] }""", "settings.hooks")]
    [InlineData("""{ "hooks": { } }""", "empty")]
    [InlineData("""{ "hooks": { "interval": { "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": { "every": "soon", "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": { "every": "0s", "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": "a.ps1" } }""", "every")]
    [InlineData("""{ "hooks": { "click": { } } }""", "\"run\"")]
    [InlineData("""{ "hooks": { "click": "  " } }""", "\"run\"")]
    [InlineData("""{ "hooks": { "click": 5 } }""", "must be an object")]
    [InlineData("""{ "hooks": { "hover": { "run": "a.ps1" } } }""", "Unknown hook")]
    [InlineData("""{ "hooks": { "action:": { "run": "a.ps1" } } }""", "needs an id")]
    [InlineData("""{ "hooks": { "click": { "run": "a.ps1", "timeout": "x" } } }""", "timeout")]
    public void Invalid_settings_explain_themselves(string json, string expected)
    {
        var s = Parse(json);

        Assert.Contains(s.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Valid_hooks_are_kept_next_to_errors()
    {
        var s = Parse("""{ "hooks": { "click": "c.ps1", "hover": "h.ps1" } }""");

        Assert.Single(s.Errors);
        Assert.NotNull(s.Hook("click"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter ScriptSettingsTests`
Expected: build FAILS with `The type or namespace name 'ScriptSettings' could not be found`.

- [ ] **Step 4: Implement ScriptSettings**

`src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>One entry of settings.hooks. Every is set for "interval" only; Timeout overrides ScriptSettings.DefaultTimeout.</summary>
public sealed record HookSpec(string Run, TimeSpan? Every = null, TimeSpan? Timeout = null);

/// <summary>The script connector's settings: { "hooks": { "interval": { "every": "30s", "run": "scripts/x.ps1" }, ... } }.</summary>
public sealed class ScriptSettings
{
    /// <summary>Longest a hook script may run unless its "timeout" says otherwise. Click, action and popup hooks are also bounded by the host's 10 s.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    const string ActionPrefix = "action:";
    static readonly HashSet<string> PlainHooks = new(StringComparer.Ordinal) { "interval", "watch", "click", "flyout", "startup", "resume" };

    ScriptSettings(IReadOnlyDictionary<string, HookSpec> hooks, IReadOnlyList<string> errors)
    {
        Hooks = hooks;
        Errors = errors;
    }

    public IReadOnlyDictionary<string, HookSpec> Hooks { get; }

    /// <summary>Problems found while parsing. The connector refuses to run anything while there are any.</summary>
    public IReadOnlyList<string> Errors { get; }

    public HookSpec? Hook(string name) => Hooks.GetValueOrDefault(name);

    public HookSpec? Action(string actionId) => Hooks.GetValueOrDefault(ActionPrefix + actionId);

    public static ScriptSettings Parse(JsonElement settings)
    {
        var hooks = new Dictionary<string, HookSpec>(StringComparer.Ordinal);
        var errors = new List<string>();

        if (settings.ValueKind != JsonValueKind.Object
            || !settings.TryGetProperty("hooks", out var hooksEl)
            || hooksEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add("settings.hooks must be an object, e.g. { \"interval\": { \"every\": \"30s\", \"run\": \"scripts/status.ps1\" } }.");
            return new(hooks, errors);
        }

        foreach (var hook in hooksEl.EnumerateObject())
        {
            var name = hook.Name;
            var isAction = name.StartsWith(ActionPrefix, StringComparison.Ordinal);
            if (!isAction && !PlainHooks.Contains(name))
            {
                errors.Add($"Unknown hook \"{name}\". Use interval, watch, click, flyout, startup, resume or action:<id>.");
                continue;
            }
            if (isAction && name.Length == ActionPrefix.Length)
            {
                errors.Add("Hook \"action:\" needs an id, e.g. \"action:rerun\".");
                continue;
            }
            if (ReadHook(name, hook.Value, errors) is { } spec) hooks[name] = spec;
        }

        if (hooks.Count == 0 && errors.Count == 0) errors.Add("settings.hooks is empty; add at least one hook.");
        return new(hooks, errors);
    }

    static HookSpec? ReadHook(string name, JsonElement el, List<string> errors)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            if (name == "interval")
            {
                errors.Add("Hook \"interval\" needs { \"every\": \"30s\", \"run\": \"...\" }.");
                return null;
            }
            var text = el.GetString();
            return string.IsNullOrWhiteSpace(text) ? MissingRun(name, errors) : new HookSpec(text.Trim());
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Hook \"{name}\" must be an object with \"run\".");
            return null;
        }

        if (!el.TryGetProperty("run", out var run) || run.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(run.GetString()))
            return MissingRun(name, errors);

        TimeSpan? every = null;
        if (name == "interval")
        {
            if (!el.TryGetProperty("every", out var e) || !TryPositiveDuration(e, out var period))
            {
                errors.Add("Hook \"interval\" needs \"every\" as a duration, e.g. \"30s\".");
                return null;
            }
            every = period;
        }

        TimeSpan? timeout = null;
        if (el.TryGetProperty("timeout", out var t))
        {
            if (!TryPositiveDuration(t, out var limit))
            {
                errors.Add($"Hook \"{name}\" has an invalid \"timeout\"; use e.g. \"20s\".");
                return null;
            }
            timeout = limit;
        }

        return new HookSpec(run.GetString()!.Trim(), every, timeout);
    }

    static bool TryPositiveDuration(JsonElement el, out TimeSpan value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.String && Duration.TryParse(el.GetString(), out value) && value > TimeSpan.Zero;
    }

    static HookSpec? MissingRun(string name, List<string> errors)
    {
        errors.Add($"Hook \"{name}\" needs a \"run\" script path or command line.");
        return null;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter ScriptSettingsTests`
Expected: `Passed: 16`.

Run: `dotnet build TaskbarTiles.sln` and then `ls src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/plugins/TaskbarTiles.Plugins.Script/`
Expected: `TaskbarTiles.Plugins.Script.dll` is listed and `TaskbarTiles.Contracts.dll` is not.

- [ ] **Step 6: Commit**

```bash
git add TaskbarTiles.sln src/TaskbarTiles.Plugins.Script src/TaskbarTiles.Host/TaskbarTiles.Host.csproj tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj tests/TaskbarTiles.Tests/ScriptSettingsTests.cs
git commit -m "feat(script): add the script plugin project and hook settings" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Command resolution

**Files:**
- Create: `src/TaskbarTiles.Plugins.Script/CommandResolver.cs`, `src/TaskbarTiles.Plugins.Script/ExecutableLocator.cs`
- Test: `tests/TaskbarTiles.Tests/CommandResolverTests.cs`, `tests/TaskbarTiles.Tests/ExecutableLocatorTests.cs` (new)

**Interfaces:**
- Consumes: nothing new
- Produces (namespace `TaskbarTiles.Plugins.Script`):
  - `sealed record ScriptCommand(string FileName, string Arguments, string WorkingDirectory)`
  - `sealed class CommandResolver(string configDirectory, string powerShell, string python = "python")` with `ScriptCommand Resolve(string run)` and `internal static (string Script, string Remainder) SplitFirst(string run)`
  - `static class ExecutableLocator` with `string? FindOnPath(string fileName, string? pathVariable = null)` and `string PowerShell(string? pathVariable = null)` (full path of `pwsh.exe` when on PATH, else `"powershell.exe"`)

Rules: the first token of `run` (double-quoted if it contains spaces) is the script; the rest are arguments passed on unchanged. A relative script path is resolved against the config folder. `.ps1` → `<powerShell> -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<path>" <rest>`; `.py` → `python -u "<path>" <rest>` (`-u` so `watch` lines are not buffered); an existing file with any other extension → `cmd.exe /d /s /c ""<path>" <rest>"`; anything else is a command line → `cmd.exe /d /s /c "<run>"`. The working directory is always the config folder.

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/CommandResolverTests.cs`:
```csharp
using System.IO;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class CommandResolverTests
{
    const string ConfigDir = @"C:\cfg";
    static readonly CommandResolver Resolver = new(ConfigDir, "pwsh.exe");

    [Fact]
    public void Relative_ps1_runs_with_powershell_from_the_config_folder()
    {
        var c = Resolver.Resolve("scripts/ci.ps1");

        Assert.Equal("pwsh.exe", c.FileName);
        Assert.Equal(@"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\cfg\scripts\ci.ps1""", c.Arguments);
        Assert.Equal(ConfigDir, c.WorkingDirectory);
    }

    [Fact]
    public void Arguments_after_the_script_are_passed_on() =>
        Assert.EndsWith(@"-File ""C:\cfg\scripts\ci.ps1"" -Branch main", Resolver.Resolve("scripts/ci.ps1 -Branch main").Arguments);

    [Fact]
    public void Quoted_path_may_contain_spaces_and_extension_case_does_not_matter() =>
        Assert.EndsWith(@"-File ""C:\cfg\my scripts\x.PS1"" a b", Resolver.Resolve("\"my scripts/x.PS1\" a b").Arguments);

    [Fact]
    public void Unclosed_quote_takes_the_rest_as_the_path() =>
        Assert.EndsWith(@"-File ""C:\cfg\scripts\x.ps1""", Resolver.Resolve("\"scripts/x.ps1").Arguments);

    [Fact]
    public void Absolute_path_is_kept() =>
        Assert.EndsWith(@"-File ""D:\tools\x.ps1""", Resolver.Resolve(@"D:\tools\x.ps1").Arguments);

    [Fact]
    public void Python_runs_unbuffered()
    {
        var c = Resolver.Resolve("scripts/w.py --once");

        Assert.Equal("python", c.FileName);
        Assert.Equal(@"-u ""C:\cfg\scripts\w.py"" --once", c.Arguments);
    }

    [Fact]
    public void Existing_other_file_runs_through_cmd()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("x.cmd"), "@echo {}");

        var c = new CommandResolver(dir.Path, "pwsh.exe").Resolve("x.cmd arg");

        Assert.Equal("cmd.exe", c.FileName);
        Assert.Equal($"/d /s /c \"\"{dir.File("x.cmd")}\" arg\"", c.Arguments);
        Assert.Equal(dir.Path, c.WorkingDirectory);
    }

    [Fact]
    public void Anything_else_is_a_command_line_for_cmd()
    {
        var c = Resolver.Resolve("  echo {\"title\":\"hi\"}  ");

        Assert.Equal("cmd.exe", c.FileName);
        Assert.Equal("/d /s /c \"echo {\"title\":\"hi\"}\"", c.Arguments);
    }
}
```

`tests/TaskbarTiles.Tests/ExecutableLocatorTests.cs`:
```csharp
using System.IO;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ExecutableLocatorTests
{
    [Fact]
    public void Finds_a_file_on_the_given_path()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tool.exe"), "");

        Assert.Equal(dir.File("tool.exe"), ExecutableLocator.FindOnPath("tool.exe", @"C:\does-not-exist;" + dir.Path));
    }

    [Fact]
    public void Quoted_and_odd_entries_are_handled()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tool.exe"), "");

        Assert.Equal(dir.File("tool.exe"), ExecutableLocator.FindOnPath("tool.exe", "bad|<>dir;;\"" + dir.Path + "\""));
    }

    [Fact]
    public void PowerShell_prefers_pwsh()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("pwsh.exe"), "");

        Assert.Equal(dir.File("pwsh.exe"), ExecutableLocator.PowerShell(dir.Path));
    }

    [Fact]
    public void PowerShell_falls_back_to_windows_powershell()
    {
        using var dir = new TempDir();

        Assert.Equal("powershell.exe", ExecutableLocator.PowerShell(dir.Path));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "CommandResolverTests|ExecutableLocatorTests"`
Expected: build FAILS with `The type or namespace name 'CommandResolver' could not be found`.

- [ ] **Step 3: Implement**

`src/TaskbarTiles.Plugins.Script/CommandResolver.cs`:
```csharp
namespace TaskbarTiles.Plugins.Script;

/// <summary>A process to start: what ProcessStartInfo needs.</summary>
public sealed record ScriptCommand(string FileName, string Arguments, string WorkingDirectory);

/// <summary>
/// Turns a hook's "run" into a process. The first token is the script (quote it if it has spaces), resolved
/// against the config folder; .ps1 runs via PowerShell, .py via python, other existing files and plain
/// command lines via cmd.exe. The rest of the line is passed on unchanged.
/// </summary>
public sealed class CommandResolver(string configDirectory, string powerShell, string python = "python")
{
    const string Shell = "cmd.exe";

    public ScriptCommand Resolve(string run)
    {
        var line = run.Trim();
        var (first, rest) = SplitFirst(line);
        string path;
        try
        {
            path = Path.IsPathRooted(first) ? first : Path.GetFullPath(Path.Combine(configDirectory, first));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(Shell, $"/d /s /c \"{line}\"", configDirectory); // the first token is no path at all
        }
        var tail = rest.Length == 0 ? "" : " " + rest;

        return Path.GetExtension(first).ToLowerInvariant() switch
        {
            ".ps1" => new(powerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(path)}{tail}", configDirectory),
            ".py" => new(python, $"-u {Quote(path)}{tail}", configDirectory),
            _ when File.Exists(path) => new(Shell, $"/d /s /c \"{Quote(path)}{tail}\"", configDirectory),
            _ => new(Shell, $"/d /s /c \"{line}\"", configDirectory),
        };
    }

    internal static (string Script, string Remainder) SplitFirst(string run)
    {
        if (run.StartsWith('"'))
        {
            var end = run.IndexOf('"', 1);
            return end < 0 ? (run[1..], "") : (run[1..end], run[(end + 1)..].Trim());
        }
        var space = run.IndexOfAny([' ', '\t']);
        return space < 0 ? (run, "") : (run[..space], run[(space + 1)..].Trim());
    }

    static string Quote(string path) => "\"" + path + "\"";
}
```

`src/TaskbarTiles.Plugins.Script/ExecutableLocator.cs`:
```csharp
namespace TaskbarTiles.Plugins.Script;

public static class ExecutableLocator
{
    /// <summary>Full path of fileName in the first PATH folder that has it, or null.</summary>
    public static string? FindOnPath(string fileName, string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }
        return null;
    }

    /// <summary>PowerShell 7 (pwsh.exe) when it is on PATH, else Windows PowerShell.</summary>
    public static string PowerShell(string? pathVariable = null) => FindOnPath("pwsh.exe", pathVariable) ?? "powershell.exe";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "CommandResolverTests|ExecutableLocatorTests"`
Expected: `Passed: 12`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Plugins.Script/CommandResolver.cs src/TaskbarTiles.Plugins.Script/ExecutableLocator.cs tests/TaskbarTiles.Tests/CommandResolverTests.cs tests/TaskbarTiles.Tests/ExecutableLocatorTests.cs
git commit -m "feat(script): resolve run lines to PowerShell, python or cmd" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Script output mapping

**Files:**
- Create: `src/TaskbarTiles.Plugins.Script/ScriptResult.cs`, `src/TaskbarTiles.Plugins.Script/ScriptOutput.cs`, `src/TaskbarTiles.Plugins.Script/HookResults.cs`
- Test: `tests/TaskbarTiles.Tests/ScriptOutputTests.cs`, `tests/TaskbarTiles.Tests/HookResultsTests.cs` (new)

**Interfaces:**
- Consumes: `TileState`, `TileIcon`, `TileColor`, `TileButton`, `FlyoutState`, `FlyoutRow`, `FlyoutAction` (Contracts)
- Produces (namespace `TaskbarTiles.Plugins.Script`):
  - `sealed record ScriptResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false, string? StartError = null)`
  - `sealed record ParseResult<T>(T? Value, string? Error, IReadOnlyList<string> Warnings) where T : class` with `static ParseResult<T> Empty`
  - `static class ScriptOutput` with `ParseResult<TileState> ParseTile(string? text)` and `ParseResult<FlyoutState> ParseFlyout(string? text)`
  - `sealed record HookOutcome<T>(T? Value, string? ErrorMessage, string ErrorDetails, IReadOnlyList<string> Warnings) where T : class` with `bool Failed`
  - `static class HookResults` with `const int MaxDetails = 4000`, `HookOutcome<TileState> ForTile(string hook, ScriptResult result, TimeSpan timeout)`, `HookOutcome<FlyoutState> ForFlyout(string hook, ScriptResult result, TimeSpan timeout)`, `string Details(ScriptResult result)`

Mapping rules (documented for script authors in Task 11):
- Output is one JSON object (camelCase). Empty or whitespace output means "nothing" (no update; for a popup: no popup). A leading BOM is ignored.
- Tile fields: `title`, `subtitle`, `tooltip` (string; numbers become text), `icon` (glyph name string, or `{ "glyph", "color" }`), `image` (base64 PNG/JPEG, `data:` URL allowed), `buttons` (`[{ "id", "glyph"="info", "tooltip", "enabled"=true }]`), `progress` (number 0..1), `sparkline` (array of numbers), `urgent` (bool).
- Popup fields: `title`, `subtitle`, `rows` (`"text"` or `{ "text", "value", "icon" }`), `actions` (`[{ "id", "label"=id, "primary"=false }]`), `image`.
- Colors by name, case-insensitive: `neutral`, `accent`, `ok`, `warn`, `bad`; anything else is `neutral` with a warning.
- Unknown fields and values of the wrong type are dropped with a warning; they never fail the run. Unknown glyph names are passed through (the host shows `info`).
- A run fails when it could not start, timed out, exited non-zero, or printed something that is not a JSON object. The failure's details are stderr, else stdout, trimmed to the last 4000 characters.

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/ScriptOutputTests.cs`:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ScriptOutputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Empty_output_is_neither_a_state_nor_an_error(string? text)
    {
        var r = ScriptOutput.ParseTile(text);

        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Every_tile_field_is_mapped()
    {
        var png = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var r = ScriptOutput.ParseTile($$"""
            {
              "title": "CI", "subtitle": "main: passing", "tooltip": "Last run 2 min ago",
              "icon": { "glyph": "check", "color": "ok" },
              "image": "{{png}}",
              "buttons": [ { "id": "rerun", "glyph": "refresh", "tooltip": "Run again", "enabled": false } ],
              "progress": 0.25,
              "sparkline": [1, 2.5, 3],
              "urgent": true
            }
            """);

        Assert.Null(r.Error);
        Assert.Empty(r.Warnings);
        var s = r.Value!;
        Assert.Equal("CI", s.Title);
        Assert.Equal("main: passing", s.Subtitle);
        Assert.Equal("Last run 2 min ago", s.Tooltip);
        Assert.Equal(new TileIcon("check", TileColor.Ok), s.Icon);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Image);
        Assert.Equal(new TileButton("rerun", "refresh", "Run again", Enabled: false), Assert.Single(s.Buttons));
        Assert.Equal(0.25, s.Progress);
        Assert.Equal(new[] { 1, 2.5, 3 }, s.Sparkline);
        Assert.True(s.Urgent);
    }

    [Fact]
    public void Icon_as_a_string_is_a_neutral_glyph() =>
        Assert.Equal(new TileIcon("clock"), ScriptOutput.ParseTile("""{ "icon": "clock" }""").Value!.Icon);

    [Theory]
    [InlineData("OK", TileColor.Ok)]
    [InlineData("warn", TileColor.Warn)]
    [InlineData("Bad", TileColor.Bad)]
    [InlineData("accent", TileColor.Accent)]
    [InlineData("neutral", TileColor.Neutral)]
    public void Color_names_are_case_insensitive(string name, TileColor expected) =>
        Assert.Equal(expected, ScriptOutput.ParseTile($$"""{ "icon": { "glyph": "info", "color": "{{name}}" } }""").Value!.Icon!.Color);

    [Theory]
    [InlineData("purple")]
    [InlineData("2")]
    public void Unknown_color_falls_back_to_neutral_with_a_warning(string name)
    {
        var r = ScriptOutput.ParseTile($$"""{ "icon": { "glyph": "info", "color": "{{name}}" } }""");

        Assert.Equal(TileColor.Neutral, r.Value!.Icon!.Color);
        Assert.Contains(r.Warnings, w => w.Contains("unknown color"));
    }

    [Fact]
    public void Unknown_fields_are_ignored_with_a_warning()
    {
        var r = ScriptOutput.ParseTile("""{ "tittle": "x", "title": "y" }""");

        Assert.Equal("y", r.Value!.Title);
        Assert.Contains(r.Warnings, w => w.Contains("\"tittle\""));
    }

    [Fact]
    public void Wrong_types_are_dropped_with_warnings()
    {
        var r = ScriptOutput.ParseTile("""{ "title": [], "progress": "half", "urgent": "yes", "sparkline": [1, "a", 2], "icon": 7, "buttons": {} }""");

        var s = r.Value!;
        Assert.Null(s.Title);
        Assert.Null(s.Progress);
        Assert.False(s.Urgent);
        Assert.Equal(new[] { 1.0, 2.0 }, s.Sparkline);
        Assert.Null(s.Icon);
        Assert.Empty(s.Buttons);
        Assert.Equal(6, r.Warnings.Count);
    }

    [Fact]
    public void Numbers_become_text()
    {
        var s = ScriptOutput.ParseTile("""{ "title": 42, "subtitle": 1.5 }""").Value!;

        Assert.Equal("42", s.Title);
        Assert.Equal("1.5", s.Subtitle);
    }

    [Fact]
    public void Buttons_need_an_id_and_get_defaults()
    {
        var r = ScriptOutput.ParseTile("""{ "buttons": [ { "glyph": "play" }, "x", { "id": "go" } ] }""");

        Assert.Equal(new TileButton("go", "info"), Assert.Single(r.Value!.Buttons));
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void Image_accepts_base64_and_data_urls()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9, 8, 7 });

        Assert.Equal(new byte[] { 9, 8, 7 }, ScriptOutput.ParseTile($$"""{ "image": "{{b64}}" }""").Value!.Image);
        Assert.Equal(new byte[] { 9, 8, 7 }, ScriptOutput.ParseTile($$"""{ "image": "data:image/png;base64,{{b64}}" }""").Value!.Image);
        var bad = ScriptOutput.ParseTile("""{ "image": "not base64!" }""");
        Assert.Null(bad.Value!.Image);
        Assert.Single(bad.Warnings);
    }

    [Theory]
    [InlineData("[1, 2]", "an array")]
    [InlineData("42", "a number")]
    [InlineData("\"text\"", "a string")]
    public void Non_object_output_is_an_error(string text, string kind)
    {
        var r = ScriptOutput.ParseTile(text);

        Assert.Null(r.Value);
        Assert.Equal($"Expected a JSON object but got {kind}.", r.Error);
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var r = ScriptOutput.ParseTile("{ \"title\": ");

        Assert.Null(r.Value);
        Assert.StartsWith("Invalid JSON:", r.Error);
    }

    [Fact]
    public void Byte_order_mark_is_ignored() =>
        Assert.Equal("x", ScriptOutput.ParseTile("\uFEFF{ \"title\": \"x\" }").Value!.Title);

    [Fact]
    public void Flyout_maps_rows_and_actions()
    {
        var r = ScriptOutput.ParseFlyout("""
            {
              "title": "Builds", "subtitle": "last 3",
              "rows": [ "plain row", { "text": "#41", "value": "2m", "icon": { "glyph": "check", "color": "ok" } } ],
              "actions": [ { "id": "rerun", "label": "Run again", "primary": true }, { "id": "open" } ]
            }
            """);

        Assert.Empty(r.Warnings);
        var f = r.Value!;
        Assert.Equal("Builds", f.Title);
        Assert.Equal("last 3", f.Subtitle);
        Assert.Equal(new FlyoutRow("plain row"), f.Rows[0]);
        Assert.Equal(new FlyoutRow("#41", "2m", new TileIcon("check", TileColor.Ok)), f.Rows[1]);
        Assert.Equal(new FlyoutAction("rerun", "Run again", Primary: true), f.Actions[0]);
        Assert.Equal(new FlyoutAction("open", "open"), f.Actions[1]);
    }

    [Fact]
    public void Flyout_without_title_warns_and_uses_an_empty_title()
    {
        var r = ScriptOutput.ParseFlyout("""{ "rows": [ { "value": "x" } ], "actions": [ { "label": "no id" } ] }""");

        Assert.Equal("", r.Value!.Title);
        Assert.Empty(r.Value.Rows);
        Assert.Empty(r.Value.Actions);
        Assert.Equal(3, r.Warnings.Count);
    }
}
```

`tests/TaskbarTiles.Tests/HookResultsTests.cs`:
```csharp
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class HookResultsTests
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    [Fact]
    public void Json_output_becomes_the_state()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, """{ "title": "x" }""", ""), Limit);

        Assert.False(o.Failed);
        Assert.Equal("x", o.Value!.Title);
    }

    [Fact]
    public void Empty_output_is_nothing()
    {
        var o = HookResults.ForTile("click", new ScriptResult(0, "\n", "a log line on stderr"), Limit);

        Assert.False(o.Failed);
        Assert.Null(o.Value);
    }

    [Fact]
    public void Non_zero_exit_fails_with_stderr()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(3, """{ "title": "x" }""", "it broke\r\n"), Limit);

        Assert.True(o.Failed);
        Assert.Equal("interval: exited with code 3", o.ErrorMessage);
        Assert.Equal("it broke", o.ErrorDetails);
        Assert.Null(o.Value);
    }

    [Fact]
    public void Non_zero_exit_without_stderr_shows_stdout() =>
        Assert.Equal("plain text", HookResults.ForTile("interval", new ScriptResult(3, "plain text", ""), Limit).ErrorDetails);

    [Fact]
    public void Silent_failure_says_so() =>
        Assert.Equal("(no output)", HookResults.ForTile("interval", new ScriptResult(1, "", " "), Limit).ErrorDetails);

    [Fact]
    public void Timeout_fails()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(-1, "", "partial", TimedOut: true), Limit);

        Assert.Equal("interval: timed out after 2 s", o.ErrorMessage);
        Assert.Equal("partial", o.ErrorDetails);
    }

    [Fact]
    public void Start_error_fails()
    {
        var o = HookResults.ForTile("click", new ScriptResult(-1, "", "", StartError: "python: not found"), Limit);

        Assert.Equal("click: could not start the script", o.ErrorMessage);
        Assert.Equal("python: not found", o.ErrorDetails);
    }

    [Fact]
    public void Invalid_output_fails_with_the_parse_error_and_the_output()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, "oops", ""), Limit);

        Assert.Equal("interval: invalid output", o.ErrorMessage);
        Assert.StartsWith("Invalid JSON:", o.ErrorDetails);
        Assert.EndsWith("oops", o.ErrorDetails);
    }

    [Fact]
    public void Warnings_name_the_hook()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, """{ "tittle": "x" }""", ""), Limit);

        Assert.StartsWith("interval: Unknown field", Assert.Single(o.Warnings));
    }

    [Fact]
    public void Long_details_keep_the_end()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(1, "", new string('a', 5000) + "END"), Limit);

        Assert.Equal(HookResults.MaxDetails + 1, o.ErrorDetails.Length);
        Assert.StartsWith("…", o.ErrorDetails);
        Assert.EndsWith("END", o.ErrorDetails);
    }

    [Fact]
    public void Flyout_output_becomes_the_popup() =>
        Assert.Equal("t", HookResults.ForFlyout("flyout", new ScriptResult(0, """{ "title": "t" }""", ""), Limit).Value!.Title);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "ScriptOutputTests|HookResultsTests"`
Expected: build FAILS with `The name 'ScriptOutput' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/TaskbarTiles.Plugins.Script/ScriptResult.cs`:
```csharp
namespace TaskbarTiles.Plugins.Script;

/// <summary>How a script run ended. StartError is set when the process could not be started at all.</summary>
public sealed record ScriptResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false, string? StartError = null);
```

`src/TaskbarTiles.Plugins.Script/ScriptOutput.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>Result of reading a script's stdout. Empty output has neither a value nor an error.</summary>
public sealed record ParseResult<T>(T? Value, string? Error, IReadOnlyList<string> Warnings) where T : class
{
    public static ParseResult<T> Empty { get; } = new(null, null, []);
}

/// <summary>
/// Maps a script's stdout (one camelCase JSON object) to TileState or FlyoutState. Lenient: unknown fields
/// and badly typed values are dropped with a warning; only output that is not a JSON object is an error.
/// </summary>
public static class ScriptOutput
{
    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    static readonly HashSet<string> TileFields = new(StringComparer.Ordinal)
        { "title", "subtitle", "tooltip", "icon", "image", "buttons", "progress", "sparkline", "urgent" };

    static readonly HashSet<string> FlyoutFields = new(StringComparer.Ordinal)
        { "title", "subtitle", "rows", "actions", "image" };

    public static ParseResult<TileState> ParseTile(string? text) => Parse(text, ReadTile);

    public static ParseResult<FlyoutState> ParseFlyout(string? text) => Parse(text, ReadFlyout);

    static ParseResult<T> Parse<T>(string? text, Func<JsonElement, List<string>, T> read) where T : class
    {
        var trimmed = text?.Trim().TrimStart('\uFEFF').Trim();
        if (string.IsNullOrEmpty(trimmed)) return ParseResult<T>.Empty;
        try
        {
            using var doc = JsonDocument.Parse(trimmed, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(null, $"Expected a JSON object but got {Kind(doc.RootElement)}.", []);
            var warnings = new List<string>();
            return new(read(doc.RootElement, warnings), null, warnings);
        }
        catch (JsonException ex)
        {
            return new(null, "Invalid JSON: " + ex.Message, []);
        }
    }

    static TileState ReadTile(JsonElement o, List<string> w)
    {
        WarnUnknown(o, TileFields, w);
        return new TileState
        {
            Title = Text(o, "title", w),
            Subtitle = Text(o, "subtitle", w),
            Tooltip = Text(o, "tooltip", w),
            Icon = o.TryGetProperty("icon", out var icon) ? ReadIcon(icon, "icon", w) : null,
            Image = ReadImage(o, w),
            Buttons = ReadButtons(o, w),
            Progress = Number(o, "progress", w),
            Sparkline = Numbers(o, "sparkline", w),
            Urgent = Bool(o, "urgent", w) ?? false,
        };
    }

    static FlyoutState ReadFlyout(JsonElement o, List<string> w)
    {
        WarnUnknown(o, FlyoutFields, w);
        var title = Text(o, "title", w);
        if (title is null) w.Add("\"title\" is missing.");
        return new FlyoutState(title ?? "", Text(o, "subtitle", w), ReadRows(o, w), ReadActions(o, w), ReadImage(o, w));
    }

    static void WarnUnknown(JsonElement o, HashSet<string> known, List<string> w)
    {
        foreach (var p in o.EnumerateObject())
            if (!known.Contains(p.Name)) w.Add($"Unknown field \"{p.Name}\" ignored.");
    }

    static string? Text(JsonElement o, string name, List<string> w, string? where = null)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        w.Add($"\"{FieldPath(where, name)}\" must be a string; got {Kind(v)}.");
        return null;
    }

    static double? Number(JsonElement o, string name, List<string> w)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        w.Add($"\"{name}\" must be a number; got {Kind(v)}.");
        return null;
    }

    static bool? Bool(JsonElement o, string name, List<string> w, string? where = null)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        w.Add($"\"{FieldPath(where, name)}\" must be true or false; got {Kind(v)}.");
        return null;
    }

    static IReadOnlyList<double>? Numbers(JsonElement o, string name, List<string> w)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"{name}\" must be an array of numbers; got {Kind(v)}.");
            return null;
        }
        var list = new List<double>();
        var skipped = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d)) list.Add(d);
            else skipped++;
        }
        if (skipped > 0) w.Add($"\"{name}\": {skipped} value(s) that are not numbers were skipped.");
        return list;
    }

    static TileIcon? ReadIcon(JsonElement v, string where, List<string> w)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var name = v.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : new TileIcon(name.Trim());
            case JsonValueKind.Object:
                var glyph = Text(v, "glyph", w, where);
                if (string.IsNullOrWhiteSpace(glyph))
                {
                    w.Add($"\"{where}.glyph\" is missing.");
                    return null;
                }
                return new TileIcon(glyph.Trim(), ReadColor(v, where, w));
            default:
                w.Add($"\"{where}\" must be a glyph name or {{ \"glyph\", \"color\" }}; got {Kind(v)}.");
                return null;
        }
    }

    static TileColor ReadColor(JsonElement icon, string where, List<string> w)
    {
        var name = Text(icon, "color", w, where)?.Trim();
        if (string.IsNullOrEmpty(name)) return TileColor.Neutral;
        // Compare names only: Enum.TryParse would also accept numbers such as "2".
        foreach (var color in Enum.GetValues<TileColor>())
            if (string.Equals(color.ToString(), name, StringComparison.OrdinalIgnoreCase)) return color;
        w.Add($"\"{where}.color\": unknown color \"{name}\"; use neutral, accent, ok, warn or bad.");
        return TileColor.Neutral;
    }

    static byte[]? ReadImage(JsonElement o, List<string> w)
    {
        var text = Text(o, "image", w)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var comma = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? text.IndexOf(',') : -1;
        var base64 = (comma >= 0 ? text[(comma + 1)..] : text).Trim();
        var buffer = new byte[base64.Length]; // base64 decodes to at most 3/4 of its length
        if (Convert.TryFromBase64String(base64, buffer, out var written)) return buffer[..written];
        w.Add("\"image\" must be base64-encoded PNG or JPEG bytes.");
        return null;
    }

    static IReadOnlyList<TileButton> ReadButtons(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("buttons", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"buttons\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<TileButton>();
        var i = 0;
        foreach (var b in v.EnumerateArray())
        {
            var where = $"buttons[{i++}]";
            if (b.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be an object; skipped.");
                continue;
            }
            var id = Text(b, "id", w, where);
            if (string.IsNullOrWhiteSpace(id))
            {
                w.Add($"\"{where}.id\" is missing; skipped.");
                continue;
            }
            list.Add(new TileButton(id, Text(b, "glyph", w, where) ?? "info", Text(b, "tooltip", w, where), Bool(b, "enabled", w, where) ?? true));
        }
        return list;
    }

    static IReadOnlyList<FlyoutRow> ReadRows(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("rows", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"rows\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<FlyoutRow>();
        var i = 0;
        foreach (var r in v.EnumerateArray())
        {
            var where = $"rows[{i++}]";
            if (r.ValueKind == JsonValueKind.String)
            {
                list.Add(new FlyoutRow(r.GetString()!));
                continue;
            }
            if (r.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be a string or an object; skipped.");
                continue;
            }
            var text = Text(r, "text", w, where);
            if (text is null)
            {
                w.Add($"\"{where}.text\" is missing; skipped.");
                continue;
            }
            var icon = r.TryGetProperty("icon", out var iconEl) ? ReadIcon(iconEl, where + ".icon", w) : null;
            list.Add(new FlyoutRow(text, Text(r, "value", w, where), icon));
        }
        return list;
    }

    static IReadOnlyList<FlyoutAction> ReadActions(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("actions", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"actions\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<FlyoutAction>();
        var i = 0;
        foreach (var a in v.EnumerateArray())
        {
            var where = $"actions[{i++}]";
            if (a.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be an object; skipped.");
                continue;
            }
            var id = Text(a, "id", w, where);
            if (string.IsNullOrWhiteSpace(id))
            {
                w.Add($"\"{where}.id\" is missing; skipped.");
                continue;
            }
            list.Add(new FlyoutAction(id, Text(a, "label", w, where) ?? id, Bool(a, "primary", w, where) ?? false));
        }
        return list;
    }

    static string FieldPath(string? where, string name) => where is null ? name : $"{where}.{name}";

    static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };
}
```

`src/TaskbarTiles.Plugins.Script/HookResults.cs`:
```csharp
using System.Globalization;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>What a hook run means for the tile: a value, nothing (Value and ErrorMessage both null), or a failure.</summary>
public sealed record HookOutcome<T>(T? Value, string? ErrorMessage, string ErrorDetails, IReadOnlyList<string> Warnings) where T : class
{
    public bool Failed => ErrorMessage is not null;
}

public static class HookResults
{
    /// <summary>Longest details text kept for the popup (the end is kept: that is where errors are).</summary>
    public const int MaxDetails = 4000;

    public static HookOutcome<TileState> ForTile(string hook, ScriptResult result, TimeSpan timeout) =>
        Interpret(hook, result, timeout, ScriptOutput.ParseTile);

    public static HookOutcome<FlyoutState> ForFlyout(string hook, ScriptResult result, TimeSpan timeout) =>
        Interpret(hook, result, timeout, ScriptOutput.ParseFlyout);

    /// <summary>What to show for a failed run: stderr, else stdout, else "(no output)".</summary>
    public static string Details(ScriptResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr
            : !string.IsNullOrWhiteSpace(result.StdOut) ? result.StdOut
            : "(no output)";
        return Shorten(text.Trim());
    }

    static HookOutcome<T> Interpret<T>(string hook, ScriptResult result, TimeSpan timeout, Func<string?, ParseResult<T>> parse) where T : class
    {
        if (result.StartError is not null) return Fail<T>($"{hook}: could not start the script", result.StartError);
        if (result.TimedOut) return Fail<T>($"{hook}: timed out after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s", Details(result));
        if (result.ExitCode != 0) return Fail<T>($"{hook}: exited with code {result.ExitCode}", Details(result));

        var parsed = parse(result.StdOut);
        var warnings = parsed.Warnings.Select(w => $"{hook}: {w}").ToList();
        if (parsed.Error is not null)
            return new(null, $"{hook}: invalid output", Shorten(parsed.Error + "\n" + result.StdOut.Trim()), warnings);
        return new(parsed.Value, null, "", warnings);
    }

    static HookOutcome<T> Fail<T>(string message, string details) where T : class => new(null, message, Shorten(details), []);

    static string Shorten(string text) => text.Length <= MaxDetails ? text : "…" + text[^MaxDetails..];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "ScriptOutputTests|HookResultsTests"`
Expected: `Passed: 35` (24 `ScriptOutputTests` cases, 11 `HookResultsTests`).

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Plugins.Script/ScriptResult.cs src/TaskbarTiles.Plugins.Script/ScriptOutput.cs src/TaskbarTiles.Plugins.Script/HookResults.cs tests/TaskbarTiles.Tests/ScriptOutputTests.cs tests/TaskbarTiles.Tests/HookResultsTests.cs
git commit -m "feat(script): map script output to tile and popup state" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Interval scheduling and the process runner

**Files:**
- Create: `src/TaskbarTiles.Plugins.Script/IntervalLoop.cs`, `src/TaskbarTiles.Plugins.Script/IScriptRunner.cs`, `src/TaskbarTiles.Plugins.Script/ProcessScriptRunner.cs`
- Modify: `tests/TaskbarTiles.Tests/TestDoubles.cs` (append `ScriptFiles`)
- Test: `tests/TaskbarTiles.Tests/IntervalLoopTests.cs`, `tests/TaskbarTiles.Tests/ProcessScriptRunnerTests.cs` (new; the latter is an integration test)

**Interfaces:**
- Consumes: `ScriptCommand`, `CommandResolver`, `ExecutableLocator` (Task 6); `ScriptResult` (Task 7); `Eventually` (Task 2)
- Produces (namespace `TaskbarTiles.Plugins.Script`):
  - `sealed class IntervalLoop(TimeSpan every, Func<CancellationToken, Task> run, Action<Exception> onError, Action? onSkipped = null, Func<TimeSpan, CancellationToken, Task>? delay = null)` with `bool IsRunning`, `int Skipped`, `bool Trigger(CancellationToken ct)` (false = skipped because a run is going), `Task RunAsync(CancellationToken ct)` (trigger, wait one period, repeat until cancelled)
  - `interface IScriptRunner` with `Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)` and `Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct)`
  - `sealed class ProcessScriptRunner : IScriptRunner`: hidden process, UTF-8 stdout/stderr, stdin closed, `PYTHONIOENCODING=utf-8` and `PYTHONUTF8=1` set; on timeout kills the process tree and returns `TimedOut = true`, `ExitCode = -1`; on cancellation kills the tree and throws `OperationCanceledException`; a process that cannot start throws (`Win32Exception`)
  - Test helper `ScriptFiles.Write(TempDir dir, string relativePath, string body) : string` (writes UTF-8 without BOM, returns the relative path)

- [ ] **Step 1: Add the script-file helper**

Append to `tests/TaskbarTiles.Tests/TestDoubles.cs`:
```csharp
public static class ScriptFiles
{
    /// <summary>Writes a UTF-8 (no BOM) script under dir and returns its path relative to dir.</summary>
    public static string Write(TempDir dir, string relativePath, string body)
    {
        var full = System.IO.Path.Combine(dir.Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body, new System.Text.UTF8Encoding(false));
        return relativePath;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/IntervalLoopTests.cs`:
```csharp
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class IntervalLoopTests
{
    static readonly Action<Exception> Ignore = _ => { };

    [Fact]
    public async Task Trigger_while_a_run_is_going_is_skipped()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), async _ => { Interlocked.Increment(ref runs); await gate.Task; }, Ignore);

        Assert.True(loop.Trigger(CancellationToken.None));
        Assert.False(loop.Trigger(CancellationToken.None));
        Assert.Equal(1, loop.Skipped);

        gate.SetResult();
        await Eventually.Holds(() => !loop.IsRunning);
        Assert.True(loop.Trigger(CancellationToken.None));
        await Eventually.Holds(() => Volatile.Read(ref runs) == 2);
    }

    [Fact]
    public async Task Loop_runs_at_once_and_every_period_skipping_overlaps()
    {
        using var cts = new CancellationTokenSource();
        var delays = 0;
        var started = 0;
        var skippedCallbacks = 0;
        var loop = new IntervalLoop(TimeSpan.FromSeconds(30),
            async ct => { Interlocked.Increment(ref started); await Task.Delay(Timeout.Infinite, ct); },
            Ignore,
            onSkipped: () => Interlocked.Increment(ref skippedCallbacks),
            delay: (period, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(30), period);
                if (++delays == 3) cts.Cancel();
                return Task.CompletedTask;
            });

        await loop.RunAsync(cts.Token);

        Assert.Equal(3, delays);
        Assert.Equal(2, loop.Skipped);
        Assert.Equal(2, Volatile.Read(ref skippedCallbacks));
        await Eventually.Holds(() => Volatile.Read(ref started) == 1);
    }

    [Fact]
    public async Task A_failing_run_is_reported_and_does_not_block_the_next()
    {
        var errors = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), _ => throw new InvalidOperationException("boom"),
            _ => Interlocked.Increment(ref errors));

        loop.Trigger(CancellationToken.None);
        await Eventually.Holds(() => Volatile.Read(ref errors) == 1 && !loop.IsRunning);

        Assert.True(loop.Trigger(CancellationToken.None));
        await Eventually.Holds(() => Volatile.Read(ref errors) == 2);
    }

    [Fact]
    public async Task Cancelled_run_is_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var errors = 0;
        var loop = new IntervalLoop(TimeSpan.FromHours(1), ct => Task.Delay(Timeout.Infinite, ct), _ => Interlocked.Increment(ref errors));

        loop.Trigger(cts.Token);
        cts.Cancel();

        await Eventually.Holds(() => !loop.IsRunning);
        Assert.Equal(0, errors);
    }
}
```

`tests/TaskbarTiles.Tests/ProcessScriptRunnerTests.cs`:
```csharp
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
    public async Task Missing_executable_throws()
    {
        var command = new ScriptCommand("tt-does-not-exist-4711.exe", "", _dir.Path);

        await Assert.ThrowsAnyAsync<Exception>(() => _runner.RunAsync(command, NoEnv, TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "IntervalLoopTests|ProcessScriptRunnerTests"`
Expected: build FAILS with `The type or namespace name 'IntervalLoop' could not be found`.

- [ ] **Step 4: Implement**

`src/TaskbarTiles.Plugins.Script/IntervalLoop.cs`:
```csharp
namespace TaskbarTiles.Plugins.Script;

/// <summary>
/// Runs a job at once and then every period. A tick (or a manual Trigger) that finds the previous run
/// still going is skipped, never queued. Runs execute on the thread pool; their exceptions go to onError.
/// </summary>
public sealed class IntervalLoop(
    TimeSpan every,
    Func<CancellationToken, Task> run,
    Action<Exception> onError,
    Action? onSkipped = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((period, ct) => Task.Delay(period, ct));
    int _running;
    int _skipped;

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public int Skipped => Volatile.Read(ref _skipped);

    /// <summary>Starts a run unless one is going. Returns false when this trigger was skipped.</summary>
    public bool Trigger(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skipped);
            onSkipped?.Invoke();
            return false;
        }
        _ = RunOnceAsync(ct);
        return true;
    }

    /// <summary>Until cancelled: trigger, then wait one period.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Trigger(ct);
                await _delay(every, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(() => run(ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
```

`src/TaskbarTiles.Plugins.Script/IScriptRunner.cs`:
```csharp
namespace TaskbarTiles.Plugins.Script;

/// <summary>Starts script processes. The real one is ProcessScriptRunner; unit tests use a fake.</summary>
public interface IScriptRunner
{
    /// <summary>
    /// Runs to completion and captures the output. Past the timeout the process tree is killed and the result has
    /// TimedOut set. Cancellation kills the process tree and throws OperationCanceledException.
    /// Throws if the process cannot be started.
    /// </summary>
    Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs until the process exits, calling onLine for each stdout line as it arrives (StdOut in the result is empty).
    /// Cancellation kills the process tree and throws OperationCanceledException. Throws if the process cannot be started.
    /// </summary>
    Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct);
}
```

`src/TaskbarTiles.Plugins.Script/ProcessScriptRunner.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TaskbarTiles.Plugins.Script;

/// <summary>Runs scripts as hidden child processes with redirected, UTF-8 decoded output.</summary>
public sealed class ProcessScriptRunner : IScriptRunner
{
    const int MaxStdOut = 1024 * 1024;
    const int MaxStdErr = 16 * 1024;
    static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public async Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Start(command, environment);
        var stdout = new BoundedText(MaxStdOut);
        var stderr = new BoundedText(MaxStdErr);
        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout.AppendLine), Pump(process.StandardError, stderr.AppendLine));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Drain(pumps).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ScriptResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        await Drain(pumps).ConfigureAwait(false);
        return new ScriptResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct)
    {
        using var process = Start(command, environment);
        var stderr = new BoundedText(MaxStdErr);
        var pumps = Task.WhenAll(Pump(process.StandardOutput, onLine), Pump(process.StandardError, stderr.AppendLine));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Drain(pumps).ConfigureAwait(false);
            throw;
        }

        await Drain(pumps).ConfigureAwait(false);
        return new ScriptResult(process.ExitCode, "", stderr.ToString());
    }

    static Process Start(ScriptCommand command, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Python writes in the console code page when piped unless told otherwise.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {command.FileName}.");
        process.StandardInput.Close(); // scripts get no input; a script that reads stdin sees end-of-file
        return process;
    }

    static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was killed or disposed while we were reading.
        }
    }

    static async Task Drain(Task pumps)
    {
        try { await pumps.WaitAsync(DrainLimit).ConfigureAwait(false); }
        catch (TimeoutException) { /* a grandchild still holds the pipe open; give up on the rest of the output */ }
    }

    static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    /// <summary>Thread-safe line buffer that keeps the last <c>limit</c> characters.</summary>
    sealed class BoundedText(int limit)
    {
        readonly StringBuilder _text = new();

        public void AppendLine(string line)
        {
            lock (_text)
            {
                if (_text.Length > 0) _text.Append('\n');
                _text.Append(line);
                if (_text.Length > limit) _text.Remove(0, _text.Length - limit);
            }
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "IntervalLoopTests|ProcessScriptRunnerTests"`
Expected: `Passed: 10`. The runner tests start real PowerShell processes (Windows PowerShell 5.1 when `pwsh` is not installed) and take a few seconds.

- [ ] **Step 6: Commit**

```bash
git add src/TaskbarTiles.Plugins.Script/IntervalLoop.cs src/TaskbarTiles.Plugins.Script/IScriptRunner.cs src/TaskbarTiles.Plugins.Script/ProcessScriptRunner.cs tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/IntervalLoopTests.cs tests/TaskbarTiles.Tests/ProcessScriptRunnerTests.cs
git commit -m "feat(script): interval loop with skip-while-running and a process runner" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 9: The script connector

**Files:**
- Create: `src/TaskbarTiles.Plugins.Script/ScriptConnector.cs`
- Modify: `tests/TaskbarTiles.Tests/TestDoubles.cs` (append `TestTileContext`), `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj:21-25`, `tests/TaskbarTiles.Tests/PluginLoaderTests.cs`
- Test: `tests/TaskbarTiles.Tests/ScriptConnectorTests.cs` (new)

**Interfaces:**
- Consumes: `ITileContext.ConfigDirectory`, `ITileContext.ReportError` (Task 2); `TileActions`, `BackoffSchedule` (Task 1); `ScriptSettings`, `HookSpec` (Task 5); `CommandResolver`, `ExecutableLocator` (Task 6); `ScriptResult`, `ScriptOutput`, `HookResults` (Task 7); `IntervalLoop`, `IScriptRunner`, `ProcessScriptRunner` (Task 8)
- Produces:
  - `[TileConnector("script")] public sealed class ScriptConnector : ITileConnector` with a public parameterless constructor (used by the plugin loader) and `internal ScriptConnector(IScriptRunner runner, string powerShell, BackoffSchedule backoff)` (tests)
  - Test double `TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null) : ITileContext` with `IReadOnlyList<TileState> Updates`, `IReadOnlyList<(string Message, string Details)> Errors`, `CollectingLog Logged`, `void RaiseResumed()`

Hook behavior:

| Hook | `TILE_HOOK` | Runs | Output |
|---|---|---|---|
| `startup` | `startup` | once at start, before the first interval run; again on "Refresh now" if there is no `interval` | tile state |
| `interval` | `interval` | at start and every `every`; skipped while the previous run is going; on "Refresh now", after resume, and after an action hook that printed nothing | tile state |
| `watch` | `watch` | at start, long-running; each stdout line is a tile state; restarted with backoff (2 s, 10 s, 1 min, 5 min; reset by a valid line) whenever it exits | tile state per line |
| `click` | `click` | on a plain click (only when there is no popup) | tile state, if any |
| `flyout` | `flyout` | when the popup opens | popup state; empty output = no popup |
| `action:<id>` | `action` (`TILE_ACTION` = id) | when a button or popup action with that id is pressed | tile state, if any |
| `resume` | `resume` | after the system resumes from sleep | tile state |

Every run gets `TILE_ID`, `TILE_HOOK`, `TILE_ACTION` (empty except for action hooks) and `TILE_SETTINGS_JSON` (the tile's whole `settings` object). A failed run (cannot start, timeout, non-zero exit, invalid JSON) calls `ReportError` with the stderr as details and is logged to the tile log; it never throws out of the connector. Invalid settings are reported once and nothing runs. A tile with only click, popup or action hooks shows its tile id with an `info` icon.

- [ ] **Step 1: Add the context double**

Append to `tests/TaskbarTiles.Tests/TestDoubles.cs`:
```csharp
public sealed class TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null) : ITileContext
{
    readonly List<TileState> _updates = [];
    readonly List<(string Message, string Details)> _errors = [];

    public string TileId => tileId;
    public System.Text.Json.JsonElement Settings { get; } = System.Text.Json.JsonDocument.Parse(settingsJson).RootElement.Clone();
    public string ConfigDirectory { get; } = configDirectory ?? System.IO.Path.GetTempPath();
    public CollectingLog Logged { get; } = new();
    public ITileLog Log => Logged;
    public event EventHandler? Resumed;

    public IReadOnlyList<TileState> Updates { get { lock (_updates) return _updates.ToList(); } }
    public IReadOnlyList<(string Message, string Details)> Errors { get { lock (_errors) return _errors.ToList(); } }

    public void Update(TileState state) { lock (_updates) _updates.Add(state); }
    public void ReportError(string message, string? details = null) { lock (_errors) _errors.Add((message, details ?? "")); }
    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/ScriptConnectorTests.cs`:
```csharp
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
        var runner = new FakeRunner
        {
            OnRun = (call, _) => Task.FromResult(FakeRunner.Ok(call.Env["TILE_HOOK"] == "action" ? "" : """{ "title": "status" }""")),
        };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "i.ps1" }, "action:rerun": "r.ps1" } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates.Count == 1);
        await Task.Delay(100); // let the first interval run finish, or the refresh would be skipped

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
        var runner = new FakeRunner { OnRun = (_, _) => Task.FromResult(FakeRunner.Ok("""{ "title": "x" }""")) };
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "interval": { "every": "1h", "run": "i.ps1" } } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates.Count == 1);
        await Task.Delay(100); // let the first run finish, or the refresh would be skipped

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
        Assert.Equal("flyout: exited with code 1", Assert.Single(ctx.Errors).Message);
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
```

In `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`, replace lines 21-25 (the sample-plugin item group; it moved down two lines with Task 5's edit):
```xml
  <ItemGroup>
    <!-- Build order only; the plugin is copied to plugins\ by CopyPlugins.targets, not referenced. -->
    <ProjectReference Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" />
  </ItemGroup>
```
with:
```xml
  <ItemGroup>
    <!-- Build order only; the plugin is copied to plugins\ by CopyPlugins.targets, not referenced. -->
    <ProjectReference Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" />
    <!-- Also laid out like the host's plugins folder so the loader test can discover it. -->
    <PluginProject Include="..\..\src\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
```

In `tests/TaskbarTiles.Tests/PluginLoaderTests.cs`, add after `Created_connector_shares_contract_types_with_host`:
```csharp
    [Fact]
    public void Script_plugin_is_discovered()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        Assert.Equal(PluginLoader.DefaultTileWidth, catalog.Connectors["script"].Width);
        Assert.IsAssignableFrom<ITileConnector>(catalog.Create("script"));
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "ScriptConnectorTests|PluginLoaderTests"`
Expected: build FAILS with `The type or namespace name 'ScriptConnector' could not be found`.

- [ ] **Step 4: Implement the connector**

`src/TaskbarTiles.Plugins.Script/ScriptConnector.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>
/// Drives a tile from scripts wired to hooks in settings.hooks (see docs/script-connectors.md).
/// A failing script never throws out of the connector: it is reported with ITileContext.ReportError,
/// and the next successful run clears it.
/// </summary>
[TileConnector("script")]
public sealed class ScriptConnector : ITileConnector
{
    enum HookRun { Failed, Updated, NoOutput }

    readonly IScriptRunner _runner;
    readonly string _powerShell;
    readonly BackoffSchedule _backoff;
    readonly List<Task> _background = [];
    CancellationTokenSource _cts = new();
    ITileContext? _ctx;
    ScriptSettings? _settings;
    CommandResolver? _resolver;
    IntervalLoop? _interval;

    public ScriptConnector() : this(new ProcessScriptRunner(), ExecutableLocator.PowerShell(), BackoffSchedule.Default) { }

    internal ScriptConnector(IScriptRunner runner, string powerShell, BackoffSchedule backoff)
    {
        _runner = runner;
        _powerShell = powerShell;
        _backoff = backoff;
    }

    ITileContext Ctx => _ctx ?? throw new InvalidOperationException("StartAsync has not run.");

    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var settings = ScriptSettings.Parse(ctx.Settings);
        if (settings.Errors.Count > 0)
        {
            Report("Invalid script settings", string.Join("\n", settings.Errors));
            return Task.CompletedTask;
        }

        _settings = settings;
        _resolver = new CommandResolver(ctx.ConfigDirectory, _powerShell);
        var token = _cts.Token;
        var startup = settings.Hook("startup");
        var interval = settings.Hook("interval");
        var watch = settings.Hook("watch");

        // A tile driven only by click, popup or action hooks would otherwise sit at "…".
        if (startup is null && interval is null && watch is null)
            ctx.Update(new TileState { Title = ctx.TileId, Icon = new TileIcon("info") });

        if (interval is not null)
            _interval = new IntervalLoop(interval.Every!.Value,
                t => RunTileHookAsync("interval", interval, null, t),
                ex => ctx.Log.Error("interval hook failed", ex),
                onSkipped: () => ctx.Log.Warn("interval: the previous run is still going; skipped this one"));

        _background.Add(Guarded("startup/interval", async () =>
        {
            if (startup is not null) await RunTileHookAsync("startup", startup, null, token);
            if (_interval is not null) await _interval.RunAsync(token);
        }));
        if (watch is not null) _background.Add(Guarded("watch", () => WatchAsync(watch, token)));

        ctx.Resumed += OnResumed;
        return Task.CompletedTask;
    }

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        if (_settings is null) return; // invalid settings: nothing to run
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        switch (actionId)
        {
            case TileActions.Refresh:
                Refresh();
                return;
            case TileActions.Click:
                if (_settings.Hook("click") is { } click) await RunTileHookAsync("click", click, null, linked.Token);
                return;
        }

        if (_settings.Action(actionId) is not { } action)
        {
            Ctx.Log.Warn($"No hook for action '{actionId}'; add \"action:{actionId}\" to settings.hooks.");
            return;
        }
        // An action that prints nothing usually changed what the status shows: refresh it.
        if (await RunTileHookAsync("action", action, actionId, linked.Token) == HookRun.NoOutput)
            _interval?.Trigger(_cts.Token);
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        if (_settings?.Hook("flyout") is not { } flyout) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var timeout = flyout.Timeout ?? ScriptSettings.DefaultTimeout;
        var result = await RunScriptAsync("flyout", flyout, null, timeout, linked.Token);
        var outcome = HookResults.ForFlyout("flyout", result, timeout);
        foreach (var warning in outcome.Warnings) Ctx.Log.Warn(warning);
        if (!outcome.Failed) return outcome.Value; // empty output: no popup, the click goes to the click hook

        Report(outcome.ErrorMessage!, outcome.ErrorDetails);
        var rows = outcome.ErrorDetails.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .Select(line => new FlyoutRow(line))
            .ToList();
        return new FlyoutState("Script error", outcome.ErrorMessage, rows, []);
    }

    void Refresh()
    {
        if (_interval is not null) _interval.Trigger(_cts.Token);
        else if (_settings?.Hook("startup") is { } startup) _ = Guarded("startup", () => RunTileHookAsync("startup", startup, null, _cts.Token));
    }

    void OnResumed(object? sender, EventArgs e)
    {
        if (_settings is null || _cts.IsCancellationRequested) return;
        if (_settings.Hook("resume") is { } resume) _ = Guarded("resume", () => RunTileHookAsync("resume", resume, null, _cts.Token));
        _interval?.Trigger(_cts.Token); // the data is likely stale after sleep
    }

    async Task WatchAsync(HookSpec spec, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            ScriptResult result;
            try
            {
                result = await _runner.RunLinesAsync(_resolver!.Resolve(spec.Run), EnvFor("watch", null),
                    line => { if (OnWatchLine(line)) Volatile.Write(ref failures, 0); }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = new ScriptResult(-1, "", "", StartError: $"{spec.Run}: {ex.Message}");
            }

            var delay = _backoff.Delay(failures++);
            if (result.StartError is not null) Report("watch: could not start the script", result.StartError);
            else if (result.ExitCode != 0) Report($"watch: exited with code {result.ExitCode}", HookResults.Details(result));
            else Ctx.Log.Info($"watch: the script exited; restarting in {delay.TotalSeconds:0.#} s");
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>True when the line was a valid state (that resets the watch backoff).</summary>
    bool OnWatchLine(string line)
    {
        try
        {
            var parsed = ScriptOutput.ParseTile(line);
            foreach (var warning in parsed.Warnings) Ctx.Log.Warn("watch: " + warning);
            if (parsed.Error is not null)
            {
                Report("watch: invalid output line", parsed.Error + "\n" + line);
                return false;
            }
            if (parsed.Value is null) return false; // blank line
            Ctx.Update(parsed.Value);
            return true;
        }
        catch (Exception ex)
        {
            Ctx.Log.Error("watch: handling an output line failed", ex);
            return false;
        }
    }

    async Task<HookRun> RunTileHookAsync(string hook, HookSpec spec, string? actionId, CancellationToken ct)
    {
        var timeout = spec.Timeout ?? ScriptSettings.DefaultTimeout;
        var result = await RunScriptAsync(hook, spec, actionId, timeout, ct);
        var outcome = HookResults.ForTile(hook, result, timeout);
        foreach (var warning in outcome.Warnings) Ctx.Log.Warn(warning);
        if (outcome.Failed)
        {
            Report(outcome.ErrorMessage!, outcome.ErrorDetails);
            return HookRun.Failed;
        }
        if (outcome.Value is null) return HookRun.NoOutput;
        Ctx.Update(outcome.Value);
        return HookRun.Updated;
    }

    async Task<ScriptResult> RunScriptAsync(string hook, HookSpec spec, string? actionId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(_resolver!.Resolve(spec.Run), EnvFor(hook, actionId), timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScriptResult(-1, "", "", StartError: $"{spec.Run}: {ex.Message}");
        }
    }

    Dictionary<string, string> EnvFor(string hook, string? actionId) => new()
    {
        ["TILE_ID"] = Ctx.TileId,
        ["TILE_HOOK"] = hook,
        ["TILE_ACTION"] = actionId ?? "",
        ["TILE_SETTINGS_JSON"] = Ctx.Settings.GetRawText(),
    };

    void Report(string message, string details)
    {
        Ctx.Log.Error(details.Length == 0 ? message : $"{message}{Environment.NewLine}{details}");
        Ctx.ReportError(message, details);
    }

    /// <summary>Background work: cancellation ends it quietly, anything else is logged.</summary>
    Task Guarded(string what, Func<Task> work) => Task.Run(async () =>
    {
        try { await work(); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { _ctx?.Log.Error($"{what} hook failed", ex); }
    });

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null) _ctx.Resumed -= OnResumed;
        _cts.Cancel(); // the runner kills running scripts on cancellation
        try { await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { /* don't hold up the host; the loops end on their own */ }
        // _cts is deliberately not disposed: a run that is still unwinding may read its token.
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "ScriptConnectorTests|PluginLoaderTests"`
Expected: `Failed: 0`; the 20 `ScriptConnectorTests` and `Script_plugin_is_discovered` pass.

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/TaskbarTiles.Plugins.Script/ScriptConnector.cs tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj tests/TaskbarTiles.Tests/PluginLoaderTests.cs tests/TaskbarTiles.Tests/ScriptConnectorTests.cs
git commit -m "feat(script): script connector with all hooks" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Script connector integration tests with real PowerShell

**Files:**
- Test: `tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs` (new)
- Modify (only if a test exposes a bug): files under `src/TaskbarTiles.Plugins.Script/`

**Interfaces:**
- Consumes: `ScriptConnector` (public constructor, and the internal one for a fast watch backoff), `ProcessScriptRunner`, `ExecutableLocator` (Tasks 6-9); `TestTileContext`, `ScriptFiles`, `Eventually` (test helpers)
- Produces: the spec's required integration coverage: interval, action, watch, timeout, non-zero exit (plus settings reaching the script)

- [ ] **Step 1: Write the tests**

`tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs`:
```csharp
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
        var ctx = Context("""{ "hooks": { "interval": { "every": "500ms", "run": "scripts/tick.ps1" } } }""");
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
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test TaskbarTiles.sln --filter ScriptConnectorIntegrationTests`
Expected: `Passed: 6`.

If a test fails, find the cause in `src/TaskbarTiles.Plugins.Script/` (use superpowers:systematic-debugging), fix it there, add a focused unit test to the matching unit-test class for the bug, and rerun. Do not loosen the assertions or lengthen the timeouts beyond `Slow` to make a test pass.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs src/TaskbarTiles.Plugins.Script
git commit -m "test(script): integration tests with real PowerShell scripts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 11: Sample scripts, first-run install and the script author docs

**Files:**
- Create: `scripts/uptime.ps1`, `scripts/ping.ps1`, `src/TaskbarTiles.Host/SampleScripts.cs`, `docs/script-connectors.md`
- Modify: `src/TaskbarTiles.Host/AppPaths.cs` (whole file), `src/TaskbarTiles.Host/App.xaml.cs`, `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, `src/TaskbarTiles.Host/Config/ConfigStore.cs:9-20`, `tests/TaskbarTiles.Tests/ConfigStoreTests.cs:19`
- Test: `tests/TaskbarTiles.Tests/SampleScriptsTests.cs` (new; part integration)

**Interfaces:**
- Consumes: `CommandResolver`, `ExecutableLocator`, `ProcessScriptRunner`, `HookResults` (Tasks 6-8); `TempDir`, `CollectingLog`
- Produces:
  - `static class SampleScripts { static bool InstallIfAbsent(string sourceDir, string targetDir, ITileLog log) }` (namespace `TaskbarTiles.Host`): copies the files of `sourceDir` into `targetDir` only when `targetDir` does not exist; true when it copied
  - `AppPaths.ScriptsDir` (`%LOCALAPPDATA%\TaskbarTiles\scripts`), `AppPaths.SampleScriptsDir` (`<exe dir>\scripts`)
  - Build output and publish output contain `scripts\uptime.ps1` and `scripts\ping.ps1`
  - The default `config.json` (written only when none exists) also places an `uptime` script tile

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/SampleScriptsTests.cs`:
```csharp
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
```

In `tests/TaskbarTiles.Tests/ConfigStoreTests.cs`, after line 19:
```csharp
        Assert.Equal("media", store.Current.Tiles["spotify"].Connector);
```
add:
```csharp
        Assert.Equal("script", store.Current.Tiles["uptime"].Connector);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "SampleScriptsTests|ConfigStoreTests"`
Expected: build FAILS with `The name 'SampleScripts' does not exist in the current context`.

- [ ] **Step 3: Write the sample scripts**

`scripts/uptime.ps1`:
```powershell
# Taskbar Tiles sample: clock and uptime. Hooks: interval, flyout. See docs/script-connectors.md.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$now = Get-Date
$boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime
$up = $now - $boot
$upText = if ($up.Days -gt 0) { '{0}d {1}h' -f $up.Days, $up.Hours } else { '{0}h {1}m' -f $up.Hours, $up.Minutes }

if ($env:TILE_HOOK -eq 'flyout') {
    @{
        title    = $now.ToString('dddd d MMMM yyyy')
        subtitle = "Up $upText"
        rows     = @(
            @{ text = 'Time'; value = $now.ToString('HH\:mm\:ss') },
            @{ text = 'Booted'; value = $boot.ToString('yyyy-MM-dd HH\:mm') },
            @{ text = 'Uptime'; value = ('{0}d {1:hh\:mm}' -f $up.Days, $up) }
        )
    } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}

@{
    title    = $now.ToString('HH\:mm')
    subtitle = "up $upText"
    icon     = @{ glyph = 'clock'; color = 'accent' }
    tooltip  = "Booted $($boot.ToString('g'))"
} | ConvertTo-Json -Depth 4 -Compress
```

`scripts/ping.ps1`:
```powershell
# Taskbar Tiles sample: ping a host. Hooks: interval, flyout, action:ping. See docs/script-connectors.md.
# Settings: "host" (default 1.1.1.1).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$target = if ($settings -and $settings.host) { [string]$settings.host } else { '1.1.1.1' }

function Measure-Ping([string]$name) {
    try {
        $reply = (New-Object System.Net.NetworkInformation.Ping).Send($name, 2000)
        if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) { return [int]$reply.RoundtripTime }
    } catch { }
    return $null
}

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    foreach ($i in 1..4) {
        $ms = Measure-Ping $target
        if ($null -eq $ms) {
            $rows += @{ text = "Ping $i"; value = 'timeout'; icon = @{ glyph = 'cancel'; color = 'bad' } }
        } else {
            $rows += @{ text = "Ping $i"; value = "$ms ms"; icon = @{ glyph = 'check'; color = 'ok' } }
        }
    }
    @{
        title    = $target
        subtitle = 'Last 4 pings'
        rows     = $rows
        actions  = @(@{ id = 'ping'; label = 'Ping again'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval, and action:ping (the button and the popup action) print the tile.
$ms = Measure-Ping $target
if ($null -eq $ms) {
    $state = @{ title = $target; subtitle = 'unreachable'; icon = @{ glyph = 'cancel'; color = 'bad' }; urgent = $true }
} else {
    $color = if ($ms -lt 50) { 'ok' } elseif ($ms -lt 150) { 'warn' } else { 'bad' }
    $state = @{ title = $target; subtitle = "$ms ms"; icon = @{ glyph = 'check'; color = $color } }
}
$state.buttons = @(@{ id = 'ping'; glyph = 'refresh'; tooltip = 'Ping now' })
$state.tooltip = "Pings $target. Click for details."
$state | ConvertTo-Json -Depth 5 -Compress
```

- [ ] **Step 4: Ship and install the samples**

In `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, insert before the line `<Import Project="..\..\build\CopyPlugins.targets" />`:
```xml
  <ItemGroup>
    <!-- Sample scripts next to the exe; SampleScripts copies them to %LOCALAPPDATA%\TaskbarTiles\scripts on first run. -->
    <None Include="..\..\scripts\*.ps1" Link="scripts\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Replace the whole content of `src/TaskbarTiles.Host/AppPaths.cs` with:
```csharp
using System.IO;

namespace TaskbarTiles.Host;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarTiles");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>The user's scripts; "run" paths such as scripts/x.ps1 resolve against DataDir.</summary>
    public static string ScriptsDir => Path.Combine(DataDir, "scripts");

    /// <summary>Sample scripts shipped next to the exe.</summary>
    public static string SampleScriptsDir => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Built-in plugins next to the exe first, then the user's plugins.</summary>
    public static IReadOnlyList<string> PluginRoots =>
        [Path.Combine(AppContext.BaseDirectory, "plugins"), Path.Combine(DataDir, "plugins")];
}
```

`src/TaskbarTiles.Host/SampleScripts.cs`:
```csharp
using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host;

/// <summary>Copies the sample scripts into the data folder on first run.</summary>
public static class SampleScripts
{
    /// <summary>
    /// Copies every file in sourceDir into targetDir, but only when targetDir does not exist yet, so samples the
    /// user deleted or edited are never brought back. Returns true when it copied. Never throws for IO problems.
    /// </summary>
    public static bool InstallIfAbsent(string sourceDir, string targetDir, ITileLog log)
    {
        if (Directory.Exists(targetDir) || !Directory.Exists(sourceDir)) return false;
        try
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
            log.Info($"Installed sample scripts into {targetDir}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Could not install sample scripts into {targetDir}", ex);
            return false;
        }
    }
}
```

In `src/TaskbarTiles.Host/App.xaml.cs`, replace:
```csharp
            var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
```
with:
```csharp
            var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
```

In `src/TaskbarTiles.Host/Config/ConfigStore.cs`, replace lines 9-20:
```csharp
    public const string DefaultJson = """
    {
      // Each tile is driven by a connector (a plugin). Place tiles on displays under "displays".
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true } ] },
        "*":       { "slots": [] }
      }
    }
    """;
```
with:
```csharp
    public const string DefaultJson = """
    {
      // Each tile is driven by a connector (a plugin). Place tiles on displays under "displays".
      // Script tiles run the files in the scripts folder next to this file (docs/script-connectors.md in the repo).
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } },
        "uptime":  { "connector": "script", "settings": { "hooks": {
                       "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                       "flyout":   { "run": "scripts/uptime.ps1" } } } }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true }, "uptime" ] },
        "*":       { "slots": [] }
      }
    }
    """;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "SampleScriptsTests|ConfigStoreTests"`
Expected: `Failed: 0`; the 7 `SampleScriptsTests` pass.

Run: `dotnet build TaskbarTiles.sln` and then `ls src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/scripts/`
Expected: `ping.ps1` and `uptime.ps1`.

- [ ] **Step 6: Write the script author docs**

`docs/script-connectors.md`:
````markdown
# Script connectors

The built-in `script` connector drives a tile from scripts. You wire scripts to *hooks* in `config.json`; each script prints JSON that becomes the tile or its popup. PowerShell and Python get special handling; anything else runs through `cmd.exe`, so batch files and any program on `PATH` work too.

## Quick start

1. Put a script in `%LOCALAPPDATA%\TaskbarTiles\scripts\`. On first run Taskbar Tiles copies two samples there: `uptime.ps1` (clock and uptime) and `ping.ps1` (ping a host, with a button and a popup).
2. Add a tile and place it on a display in `%LOCALAPPDATA%\TaskbarTiles\config.json` (right-click a tile → Configure connector… opens it):

```jsonc
{
  "tiles": {
    "uptime": { "connector": "script", "settings": { "hooks": {
                  "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                  "flyout":   { "run": "scripts/uptime.ps1" } } } },
    "ping":   { "connector": "script", "settings": { "host": "1.1.1.1", "hooks": {
                  "interval":    { "every": "15s", "run": "scripts/ping.ps1" },
                  "flyout":      { "run": "scripts/ping.ps1" },
                  "action:ping": { "run": "scripts/ping.ps1" } } } }
  },
  "displays": {
    "primary": { "slots": [ "uptime", "ping" ] }
  }
}
```

3. Save. The file is reloaded automatically. If it does not parse, a notice above the taskbar names the line and column, and the last good config stays active.

## Hooks

`settings.hooks` maps hook names to `{ "run": "...", "timeout": "20s" }`. Except for `interval`, a plain string is short for `{ "run": "..." }`.

| Hook | `TILE_HOOK` | When it runs | What it prints |
|---|---|---|---|
| `interval` `{ every, run }` | `interval` | At start and every `every` (e.g. `"30s"`, `"10m"`). A run is skipped while the previous one is still going. Also on "Refresh now", after the PC wakes from sleep, and after an action hook that printed nothing. | Tile JSON |
| `watch` `{ run }` | `watch` | At start; meant to run forever. Every line it prints is one tile update. When it exits it is started again after 2 s, 10 s, 1 min, then every 5 min (back to 2 s after a valid line). | One tile JSON object per line |
| `click` `{ run }` | `click` | On a plain click, only when the tile has no popup. | Tile JSON, or nothing |
| `flyout` `{ run }` | `flyout` | When the tile is clicked, to build its popup. | Popup JSON; print nothing for no popup (the click then goes to `click`) |
| `action:<id>` `{ run }` | `action` | When a tile button or popup action with that `id` is pressed. `TILE_ACTION` holds the id. | Tile JSON, or nothing |
| `startup` `{ run }` | `startup` | Once at start, before the first `interval` run. Also on "Refresh now" when there is no `interval`. | Tile JSON |
| `resume` `{ run }` | `resume` | After the PC wakes from sleep. | Tile JSON |

Durations use `ms`, `s`, `m` or `h`: `"500ms"`, `"30s"`, `"10m"`, `"1h"`.

A tile that has only `click`, `flyout` or `action:` hooks shows its tile id until a script prints something.

### Timeouts

Each run may take up to `timeout` (default 60 s), then the script and everything it started is killed and the tile shows an error. `click`, `flyout` and `action:` runs are also bounded by the host's 10 s limit for any tile action: past that, the script is killed and the tile shows "Not responding" for 3 s. `watch` has no timeout.

## `run`

`run` is a script path relative to the config folder (`%LOCALAPPDATA%\TaskbarTiles`), an absolute path, or a command line. The first word is the script; quote it if it contains spaces. Everything after it is passed on as arguments.

| `run` starts with | Runs as |
|---|---|
| `*.ps1` | `pwsh` (PowerShell 7) if it is on `PATH`, else `powershell.exe`, with `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <arguments>` |
| `*.py` | `python -u <script> <arguments>` |
| any other existing file (`.cmd`, `.bat`, `.exe`, …) | `cmd.exe /d /s /c "<script> <arguments>"` |
| anything else | the whole line through `cmd.exe /d /s /c` |

Scripts run hidden, with the config folder as the working directory and no input (stdin is closed).

## Environment

| Variable | Value |
|---|---|
| `TILE_ID` | The tile's id from `config.json`, e.g. `ping` |
| `TILE_HOOK` | `interval`, `watch`, `click`, `flyout`, `action`, `startup` or `resume` |
| `TILE_ACTION` | The action id for `action:<id>` hooks (e.g. `ping`); empty otherwise |
| `TILE_SETTINGS_JSON` | The tile's whole `settings` object as JSON, including `hooks`. Put your own options next to `hooks` (like `"host"` above). |

One script can serve several hooks by looking at `TILE_HOOK`, as both samples do.

## Tile JSON

Print one JSON object. Every field is optional; field names are camelCase.

| Field | Type | Meaning |
|---|---|---|
| `title` | string | First line. Numbers are accepted and shown as text. |
| `subtitle` | string | Second line, dimmer. |
| `tooltip` | string | Shown on hover. |
| `icon` | string or object | A glyph name (`"check"`), or `{ "glyph": "check", "color": "ok" }`. |
| `image` | string | Base64 PNG or JPEG, shown instead of the icon. A `data:image/png;base64,` prefix is fine. |
| `buttons` | array | Inline icon buttons: `{ "id": "rerun", "glyph": "refresh", "tooltip": "Run again", "enabled": true }`. `id` is required and runs the `action:<id>` hook; `glyph` defaults to `info`, `enabled` to `true`. |
| `progress` | number | 0 to 1; a thin bar along the bottom. |
| `sparkline` | array of numbers | A small line chart (needs at least two values). |
| `urgent` | boolean | Pulsing red outline. |

```json
{ "title": "CI", "subtitle": "main: passing", "icon": { "glyph": "check", "color": "ok" },
  "buttons": [ { "id": "rerun", "glyph": "refresh", "tooltip": "Run again" } ], "sparkline": [3, 5, 4, 6] }
```

## Popup JSON

| Field | Type | Meaning |
|---|---|---|
| `title` | string | Popup heading. |
| `subtitle` | string | Under the heading. |
| `rows` | array | Each row is a string, or `{ "text": "Build #41", "value": "2 min", "icon": { "glyph": "check", "color": "ok" } }`; the icon shows as a colored dot. |
| `actions` | array | Buttons at the bottom: `{ "id": "rerun", "label": "Run again", "primary": true }`. `id` is required and runs the `action:<id>` hook; `label` defaults to the id. |
| `image` | string | Base64 PNG or JPEG shown next to the heading. |

```json
{ "title": "1.1.1.1", "subtitle": "Last 4 pings",
  "rows": [ { "text": "Ping 1", "value": "12 ms", "icon": { "glyph": "check", "color": "ok" } } ],
  "actions": [ { "id": "ping", "label": "Ping again", "primary": true } ] }
```

## Glyphs and colors

Glyph names: `play`, `pause`, `previous`, `next`, `check`, `cancel`, `warning`, `refresh`, `music`, `info`, `sync`, `cloud`, `clock`. An unknown name shows `info`.

Color names (case does not matter): `neutral` (default), `accent` (blue), `ok` (green), `warn` (amber), `bad` (red). An unknown name is `neutral`.

## When something goes wrong

- A script that cannot start, times out, exits with a non-zero code, or prints something that is not a JSON object puts the tile in the error look: a warning icon, "Error", and the message in the subtitle and tooltip. Click the tile to see the script's stderr (or its output if stderr is empty), with **Restart tile** and **Open log** buttons. The next successful run clears it.
- Unknown fields and values of the wrong type are ignored; each one is written to the tile's log as a warning, which helps catch typos such as `tittle`.
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`) put the tile in the error look and nothing runs until `config.json` is fixed.
- Every failure is written to `%LOCALAPPDATA%\TaskbarTiles\logs\tile-<id>.log`. Right-click the tile → **Show connector log** opens it.
- Right-click → **Refresh now** re-runs `interval` (or `startup`). **Restart tile** starts the connector again from scratch.

## Encoding

Output is read as UTF-8.

- Windows PowerShell 5.1 writes in the console code page. Start PowerShell scripts with `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` (the samples do) so non-ASCII text arrives intact. PowerShell 7 writes UTF-8 already.
- Python is started with `PYTHONIOENCODING=utf-8`.
- In `ConvertTo-Json`, use `-Compress` and a `-Depth` larger than your nesting (the default depth of 2 cuts off `icon` inside `rows`).

## More examples

Python (`interval`), `scripts/disk.py`, with `"run": "scripts/disk.py"`:

```python
import json, shutil
total, used, free = shutil.disk_usage("C:\\")
share = used / total
print(json.dumps({
    "title": "C:",
    "subtitle": f"{free / 2**30:.0f} GB free",
    "progress": share,
    "icon": {"glyph": "info", "color": "warn" if share > 0.9 else "ok"},
}))
```

A command line instead of a script file:

```jsonc
"hello": { "connector": "script", "settings": { "hooks": {
             "startup": "echo {\"title\":\"Hello\",\"icon\":\"info\"}" } } }
```

A `watch` script that pushes an update every 5 seconds, `scripts/watch-time.ps1`:

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
while ($true) {
    @{ title = (Get-Date).ToString('HH\:mm\:ss'); icon = 'clock' } | ConvertTo-Json -Compress
    Start-Sleep -Seconds 5
}
```
````

- [ ] **Step 7: Run the full suite**

Run: `dotnet test TaskbarTiles.sln`
Expected: `Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add scripts docs/script-connectors.md src/TaskbarTiles.Host/SampleScripts.cs src/TaskbarTiles.Host/AppPaths.cs src/TaskbarTiles.Host/App.xaml.cs src/TaskbarTiles.Host/TaskbarTiles.Host.csproj src/TaskbarTiles.Host/Config/ConfigStore.cs tests/TaskbarTiles.Tests/SampleScriptsTests.cs tests/TaskbarTiles.Tests/ConfigStoreTests.cs
git commit -m "feat: sample scripts installed on first run, script connector docs" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Install and verify with the user

**Files:**
- Outside the repo: `%LOCALAPPDATA%\TaskbarTiles\app\` (install), `%LOCALAPPDATA%\TaskbarTiles\config.json`, `%LOCALAPPDATA%\TaskbarTiles\scripts\`

**Interfaces:**
- Consumes: everything above
- Produces: the user's confirmation that milestone 2 works on the real taskbar

The user performs Steps 3-8 and reports back. Do not edit the user's `config.json` without asking; give them the snippets.

- [ ] **Step 1: Publish over the installed copy**

```powershell
Get-Process TaskbarTiles -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src/TaskbarTiles.Host -c Release -o "$env:LOCALAPPDATA\TaskbarTiles\app"
Test-Path "$env:LOCALAPPDATA\TaskbarTiles\app\plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll"
Test-Path "$env:LOCALAPPDATA\TaskbarTiles\app\scripts\ping.ps1"
```
Expected: both `Test-Path` lines print `True` (this also covers the publish path of `CopyPlugins.targets`, deferred from milestone 1).

- [ ] **Step 2: Start it**

```powershell
Start-Process "$env:LOCALAPPDATA\TaskbarTiles\app\TaskbarTiles.exe"
Get-ChildItem "$env:LOCALAPPDATA\TaskbarTiles\scripts"
```
Expected: `uptime.ps1` and `ping.ps1` are listed (copied because the folder did not exist; if the user already had a `scripts` folder, copy the two files from `app\scripts` by hand). The media tile still shows.

- [ ] **Step 3 (user): Add the sample tiles**

Ask the user to right-click the media tile → **Configure connector…**, add the `uptime` and `ping` tiles from the Quick start in `docs/script-connectors.md` under `"tiles"`, append `"uptime", "ping"` to the `primary` slots, and save.
Expected: two new tiles appear within a second or two: the time with "up …", and `1.1.1.1` with a latency and a refresh button.

- [ ] **Step 4 (user): Popups and actions**

- Click the uptime tile: a popup with Time, Booted and Uptime rows.
- Click the ping tile: a popup with four pings and a **Ping again** button; press it: the popup closes and the tile's latency updates.
- Press the ping tile's refresh button several times quickly: the tile updates once per finished ping and never shows "Not responding" or an error (clicks on a button whose action is still running are dropped).

- [ ] **Step 5 (user): Context menu**

Right-click the ping tile and check the menu reads: Configure connector…, Refresh now, Show connector log, Restart tile, Open config file, Exit Taskbar Tiles.
- **Refresh now**: the latency updates.
- **Show connector log**: `logs\tile-ping.log` opens in the default editor.
- **Restart tile**: the tile briefly shows "Restarting…" and then the latency again.
- Clicking outside the open menu closes it.

- [ ] **Step 6 (user): Error look and recovery**

Create `%LOCALAPPDATA%\TaskbarTiles\scripts\broken.ps1`:
```powershell
[Console]::Error.WriteLine('something went wrong')
exit 3
```
Add this tile and put `"broken"` in the `primary` slots:
```jsonc
"broken": { "connector": "script", "settings": { "hooks": { "interval": { "every": "1h", "run": "scripts/broken.ps1" } } } }
```
Expected: the tile shows the warning icon, "Error" and "interval: exited with code 3" (also as tooltip). Clicking it opens a popup with `something went wrong`, **Restart tile** and **Open log**; **Open log** opens `tile-broken.log` with the same text.
Then replace the script body with `@{ title = 'fixed' } | ConvertTo-Json -Compress`, right-click the tile → **Refresh now**: the tile shows "fixed".

- [ ] **Step 7 (user): Invalid config notification**

In `config.json`, delete a comma between two tiles and save.
Expected: within a second, a notice at the bottom-right above the taskbar: "config.json was not applied", the parser message with line and column, "The last good config stays active." All tiles keep working. The notice disappears after about 8 seconds; clicking it instead opens `config.json`. Restore the comma and save: the notice (if still open) closes and nothing else changes.

- [ ] **Step 8 (user): Sleep and resume (optional)**

Put the PC to sleep and wake it.
Expected: the uptime tile shows the current time right after wake-up (the interval hook re-runs on resume).

- [ ] **Step 9: Record the outcome**

Ask the user to confirm Steps 3-7 (and 8 if done). Note anything that failed in `.superpowers/sdd/progress.md` for a fix round; milestone 2 is done when the user confirms.

---

## Self-review notes

- Spec coverage for milestone 2:
  - Script plugin, all hooks of the spec table (interval with skip-while-running, watch with backoff restart, click, flyout, action:<id>, startup, resume): Tasks 5-9; `run` resolution (`.ps1` → pwsh/powershell, `.py` → python, else shell; relative to the config folder): Task 6; environment `TILE_ID`, `TILE_HOOK`, `TILE_ACTION`, `TILE_SETTINGS_JSON`: Task 9; camelCase JSON with glyph and color names: Task 7; non-zero exit / invalid JSON → error look with stderr in the popup: Tasks 2, 7, 9.
  - Testing section: "script output → state mapping" and "backoff schedule" unit tests (Tasks 7, 1); "ScriptConnector with real PowerShell scripts: interval, action, watch, timeout, non-zero exit" (Task 10, plus the runner in Task 8 and the samples in Task 11).
  - Error handling table: connector throws → error look, popup with the exception, Restart tile and Open log (Tasks 2, 3); keeps failing → restart with 2 s / 10 s / 1 min / 5 min backoff, reset on success (Task 2); action/popup over 10 s → "Not responding" (kept from M1, Task 2); script exits non-zero or prints invalid JSON (Tasks 7, 9); watch exits → restart with the same backoff (Task 9); invalid `config.json` → keep last good config, notice with line and column (Task 4).
  - Interaction: right-click Configure connector…, Refresh now, Show connector log, plus Restart tile, Open config file, Exit (Task 3). Deferred to milestone 3 with edit mode: Move left/right, Add to stack / Pause rotation, Edit layout, Remove tile.
  - Lifecycle 3 "Sleep/resume: Resumed event for plugins, resume hook for scripts": Tasks 2, 9.
- Folded-in M1 minors: UI errors to the host log (Task 3); `Resumed` handlers isolated per handler (Task 2); in-flight guard for repeated button clicks (Task 2, in `TileRuntime` so it is unit-tested); plugin constructors no longer run on the UI thread and their exceptions are no longer wrapped in `TargetInvocationException` (Task 2); publish path of `CopyPlugins.targets` verified (Task 12). Late `ctx.Update` after dispose/restart is dropped by the generation check (Task 2).
- Deliberate choices beyond the spec text: any exception from `StartAsync`, `OnActionAsync` or `GetFlyoutAsync` counts as "connector throws" and restarts the connector; "Refresh now" on a crashed tile restarts it; an action hook that prints nothing triggers an interval refresh; resume also triggers an interval run; empty `flyout` output means "no popup"; default script timeout 60 s; the notice sits at the bottom-right of the primary work area.
- Checked while writing the plan: all code in this plan, applied to a clean clone of `main` (c49cb24), builds without warnings and passes `dotnet test TaskbarTiles.sln` (249 tests, 16 of them integration); the states after Task 2 and after Task 4 also build and pass; `dotnet publish` output contains `plugins\TaskbarTiles.Plugins.Script\` and `scripts\`. Expected test counts in the steps come from that run.
