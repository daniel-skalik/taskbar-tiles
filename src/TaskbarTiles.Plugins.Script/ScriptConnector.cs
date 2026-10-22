using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>
/// Drives a tile from scripts wired to hooks in settings.hooks (see docs/script-connectors.md).
/// A failing script never throws out of the connector: it is reported with ITileContext.ReportError,
/// and the next successful run clears it (a run that prints nothing re-sends the last good state).
/// A failed flyout run is not a tile error: it is logged and shown in the popup only.
/// </summary>
[TileConnector("script")]
public sealed class ScriptConnector : ITileConnector
{
    enum HookRun { Failed, Updated, NoOutput }

    readonly IScriptRunner _runner;
    readonly string _powerShell;
    readonly BackoffSchedule _backoff;
    readonly List<Task> _background = [];
    readonly object _stateLock = new();
    CancellationTokenSource _cts = new();
    TileState? _lastGood;
    bool _errorShown;
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

    /// <summary>True when no interval run is going (a trigger now would not be skipped). For tests.</summary>
    internal bool IsIntervalIdle => _interval?.IsRunning != true;

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
            Publish(IdleState());

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
        switch (actionId)
        {
            case TileActions.Refresh:
                Refresh();
                return;
            case TileActions.Click:
                if (_settings.Hook("click") is { } click)
                    await Interactive("click", ct, t => RunTileHookAsync("click", click, null, t));
                return;
        }

        if (_settings.Action(actionId) is not { } action)
        {
            Ctx.Log.Warn($"No hook for action '{actionId}'; add \"action:{actionId}\" to settings.hooks.");
            return;
        }
        // An action that prints nothing usually changed what the status shows: refresh it.
        if (await Interactive("action", ct, t => RunTileHookAsync("action", action, actionId, t)) == HookRun.NoOutput)
            _interval?.Trigger(_cts.Token);
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        if (_settings?.Hook("flyout") is not { } flyout) return null;
        var timeout = flyout.Timeout ?? ScriptSettings.DefaultTimeout;
        var result = await Interactive("flyout", ct, t => RunScriptAsync("flyout", flyout, null, timeout, t));
        var outcome = HookResults.ForFlyout("flyout", result, timeout);
        foreach (var warning in outcome.Warnings) Ctx.Log.Warn(warning);
        if (!outcome.Failed) return outcome.Value; // empty output: no popup, the click goes to the click hook

        // Not ReportError: the host would then answer every open with its own error popup and the
        // connector could never recover. The popup carries the host's restart/log actions instead.
        LogError(outcome.ErrorMessage!, outcome.ErrorDetails);
        var rows = outcome.ErrorDetails.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .Select(line => new FlyoutRow(line))
            .ToList();
        return new FlyoutState("Script error", outcome.ErrorMessage, rows,
            [new FlyoutAction("host:restart", "Restart tile", true), new FlyoutAction("host:show-log", "Open log")]);
    }

    /// <summary>Runs an interactive call; logs when the host (not the tile's own shutdown) abandons it.</summary>
    async Task<T> Interactive<T>(string hook, CancellationToken ct, Func<CancellationToken, Task<T>> work)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            return await work(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            Ctx.Log.Warn($"{hook}: cancelled by the host");
            throw;
        }
    }

    void Refresh()
    {
        if (_interval is not null) _interval.Trigger(_cts.Token);
        else if (_settings?.Hook("startup") is { } startup) _ = Guarded("startup", () => RunTileHookAsync("startup", startup, null, _cts.Token));
    }

    void OnResumed(object? sender, EventArgs e)
    {
        if (_settings is null || _cts.IsCancellationRequested) return;
        var token = _cts.Token;
        var resume = _settings.Hook("resume");
        // The data is likely stale after sleep: the resume hook runs first, then the interval refreshes.
        _ = Guarded("resume", async () =>
        {
            if (resume is not null) await RunTileHookAsync("resume", resume, null, token);
            _interval?.Trigger(token);
        });
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
            Publish(parsed.Value);
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
        if (outcome.Value is null)
        {
            ClearErrorLook();
            return HookRun.NoOutput;
        }
        Publish(outcome.Value);
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

    TileState IdleState() => new() { Title = Ctx.TileId, Icon = new TileIcon("info") };

    void LogError(string message, string details) =>
        Ctx.Log.Error(details.Length == 0 ? message : $"{message}{Environment.NewLine}{details}");

    void Report(string message, string details)
    {
        LogError(message, details);
        lock (_stateLock)
        {
            if (_cts.IsCancellationRequested) return; // disposed: the host no longer listens
            _errorShown = true;
            Ctx.ReportError(message, details);
        }
    }

    void Publish(TileState state)
    {
        lock (_stateLock)
        {
            if (_cts.IsCancellationRequested) return;
            _lastGood = state;
            _errorShown = false;
            Ctx.Update(state);
        }
    }

    /// <summary>A successful run that prints nothing leaves the error look in place; re-send the last good state.</summary>
    void ClearErrorLook()
    {
        lock (_stateLock)
        {
            if (!_errorShown || _cts.IsCancellationRequested) return;
            _errorShown = false;
            Ctx.Update(_lastGood ?? IdleState());
        }
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
