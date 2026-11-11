using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>Opens pages and console windows. Tests stand in for it.</summary>
public interface IShell
{
    void Open(string url);

    /// <summary>Runs a PowerShell command in a new console window the user can see and answer (winget and gh ask questions).</summary>
    void Console(string command);

    bool HasWinget { get; }
}

public sealed class WindowsShell : IShell
{
    public void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public void Console(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, ArgumentList = { "-NoProfile", "-NoExit", "-EncodedCommand", encoded } })?.Dispose();
    }

    public bool HasWinget =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));
}

/// <summary>
/// What on GitHub waits for you: pull requests waiting for your review, yours sorted by what they wait on, recent
/// GitHub Actions runs, unread notifications and issues assigned to you. Talks to GitHub through the GitHub CLI and its
/// sign-in, so it never sees a token. Fetches every "refresh" (default 2 minutes), on Refresh and after sleep; the
/// popup shows the last fetch at once. Settings: see <see cref="GitHubSettings"/>.
/// </summary>
[TileConnector("github", Width = 200)]
public sealed partial class GitHubConnector : ITileConnector
{
    readonly IGh _gh;
    readonly IShell _shell;
    readonly Func<DateTimeOffset> _now;
    readonly GitHubFetcher _fetcher;
    readonly SemaphoreSlim _fetching = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly object _sync = new();
    readonly byte[]? _logo = Logo();
    ITileContext? _ctx;
    GitHubSettings _settings = GitHubSettings.Read(default);
    GitHubData? _data; // the last good fetch; under _sync
    string? _stale;    // why the last fetch failed while _data is older; under _sync
    SetupNeed _setup;  // under _sync
    string? _error;    // the last failure with no data to show; under _sync
    Task? _loop;

    public GitHubConnector() : this(new GhCli(), new WindowsShell(), () => DateTimeOffset.UtcNow) { }

    public GitHubConnector(IGh gh, IShell shell, Func<DateTimeOffset> now)
    {
        _gh = gh;
        _shell = shell;
        _now = now;
        _fetcher = new GitHubFetcher(gh, now);
    }

    static byte[]? Logo()
    {
        using var stream = typeof(GitHubConnector).Assembly.GetManifestResourceStream("github.png");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _settings = GitHubSettings.Read(ctx.Settings, ctx.Log);
        ctx.Update(new TileState { Title = GitHubView.Title, Subtitle = "Loading…", Icon = new TileIcon("sync"), Image = _logo, ImageColor = _logo is null ? null : TileColor.Neutral });
        ctx.Resumed += (_, _) => _ = FetchSafelyAsync(_stop.Token);
        _loop = LoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await FetchSafelyAsync(ct);
            using var timer = new PeriodicTimer(_settings.Refresh);
            while (await timer.WaitForNextTickAsync(ct)) await FetchSafelyAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    async Task FetchSafelyAsync(CancellationToken ct)
    {
        try { await FetchNowAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _ctx?.Log.Error("Fetching from GitHub failed", ex); }
    }

    /// <summary>One fetch at a time; the tile shows its outcome. Waits for a fetch already running, then fetches again.</summary>
    public async Task FetchNowAsync(CancellationToken ct)
    {
        await _fetching.WaitAsync(ct);
        try
        {
            FetchResult result;
            try { result = await _fetcher.FetchAsync(_settings, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new FetchResult(null, Error: "GitHub: " + ex.Message);
            }
            Apply(result);
        }
        finally { _fetching.Release(); }
    }

    void Apply(FetchResult result)
    {
        var ctx = _ctx;
        if (ctx is null) return;
        lock (_sync)
        {
            _setup = result.Setup;
            if (result.Data is { } data) { _data = data; _stale = null; _error = null; }
            else if (result.Setup == SetupNeed.None)
            {
                if (_data is null) _error = result.Error;
                else _stale = result.Error;
            }
        }
        Show();
    }

    /// <summary>Draws the tile from the current state.</summary>
    void Show()
    {
        var ctx = _ctx;
        if (ctx is null) return;
        GitHubData? data; string? stale, error; SetupNeed setup;
        lock (_sync) { data = _data; stale = _stale; error = _error; setup = _setup; }
        if (setup != SetupNeed.None) ctx.Update(GitHubView.SetupTile(setup, _logo));
        else if (data is not null) ctx.Update(GitHubView.Tile(new Board(data, _settings, _now()), _logo, stale));
        else if (error is not null) ctx.ReportError(error);
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        bool needFetch;
        lock (_sync) needFetch = _data is null && _setup == SetupNeed.None;
        if (needFetch) await FetchNowAsync(ct); // the first fetch has not finished: wait for one
        lock (_sync)
        {
            if (_setup != SetupNeed.None) return GitHubView.SetupFlyout(_setup, _logo);
            if (_data is not null) return GitHubView.Flyout(new Board(_data, _settings, _now()), _logo, _stale);
            return new FlyoutState(GitHubView.Title, _error ?? "Could not read GitHub", [], [new FlyoutAction("reload", "Try again", Primary: true)],
                Image: _logo, StayOpen: true);
        }
    }

    [GeneratedRegex(@"^note:([0-9]+)$")]
    private static partial Regex NoteAction();

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        if (actionId is TileActions.Refresh or "reload") { await FetchNowAsync(ct); return; }
        if (actionId.StartsWith("open:", StringComparison.Ordinal))
        {
            var url = actionId[5..];
            if (url.StartsWith("https://github.com/", StringComparison.Ordinal) || url.StartsWith("https://cli.github.com/", StringComparison.Ordinal)) _shell.Open(url);
            else _ctx?.Log.Warn($"Not a GitHub link: {url}");
            return;
        }
        if (NoteAction().Match(actionId) is { Success: true } note) { await ReadNoteAsync(note.Groups[1].Value, ct); return; }
        switch (actionId)
        {
            case "readall": await ReadAllAsync(ct); return;
            case "install": Install(); return;
            case "signin": SignIn(); return;
            default: _ctx?.Log.Warn($"Unknown action '{actionId}'."); return;
        }
    }

    /// <summary>Opens a notification's page and marks it read; the popup redraws without it at once.</summary>
    async Task ReadNoteAsync(string id, CancellationToken ct)
    {
        Notification? found;
        lock (_sync) found = _data?.Notifications.FirstOrDefault(n => n.Id == id);
        if (found is not null) _shell.Open(found.Url);
        var result = await _gh.RunAsync(["api", "-X", "PATCH", $"notifications/threads/{id}"], ct);
        if (result.ExitCode != 0) { _ctx?.Log.Warn("Could not mark a notification read: " + result.FirstLine); return; }
        DropNotes(n => n.Id == id);
    }

    async Task ReadAllAsync(CancellationToken ct)
    {
        var result = await _gh.RunAsync(["api", "-X", "PUT", "notifications", "-f", $"last_read_at={_now().UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"], ct);
        if (result.ExitCode != 0) { _ctx?.Log.Warn("Could not mark notifications read: " + result.FirstLine); return; }
        DropNotes(_ => true);
    }

    void DropNotes(Func<Notification, bool> which)
    {
        lock (_sync)
            if (_data is not null) _data = _data with { Notifications = _data.Notifications.Where(n => !which(n)).ToList() };
        Show();
    }

    /// <summary>Installs gh with winget in a window the user can follow (winget asks first), then signs in; else the download page.</summary>
    void Install()
    {
        if (!_shell.HasWinget) { _shell.Open("https://cli.github.com/"); return; }
        _shell.Console("""
            Write-Host 'Installing the GitHub CLI for the Taskbar Tiles GitHub tile...'
            winget install --id GitHub.cli --exact --source winget
            $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
            if (Test-Path $gh) { Write-Host 'Now sign in to GitHub:'; & $gh auth login --web --git-protocol https }
            """);
    }

    void SignIn()
    {
        if (GhCli.Find() is not { } gh) { _ctx?.Log.Warn("The GitHub CLI is not installed."); return; }
        _shell.Console($"Write-Host 'Sign in to GitHub for the Taskbar Tiles GitHub tile:'; & '{gh.Replace("'", "''")}' auth login --web --git-protocol https");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _fetching.Dispose();
    }
}
