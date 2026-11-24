using System.Diagnostics;
using System.Globalization;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Calendar;

/// <summary>
/// Your next meeting from Google Calendar, and today's and tomorrow's events in the popup. Reads each calendar's secret
/// iCal address, kept in Windows Credential Manager (<see cref="CredentialStore"/>); an address is only ever sent to
/// calendar.google.com, and never logged, shown or written to a file. Fetches every "refresh" (default 5 minutes), on
/// Refresh and after sleep, and redraws the countdown every minute from the last fetch.
/// </summary>
[TileConnector("google-calendar", Width = 240)]
public sealed class CalendarConnector : ITileConnector
{
    const int MaxCalendars = 8; // the addresses share one credential, which holds 2560 bytes

    readonly ICalendarStore _store;
    readonly ICalendarSource _source;
    readonly Func<DateTimeOffset> _now;
    readonly TimeZoneInfo _zone;
    readonly CultureInfo _culture;
    readonly Action<string> _open;
    readonly SemaphoreSlim _fetching = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly object _sync = new();
    readonly byte[]? _logo = Logo();
    ITileContext? _ctx;
    CalendarSettings _settings = CalendarSettings.Read(default);
    IReadOnlyList<string> _addresses = []; // under _sync
    Agenda? _agenda;      // the last fetch that read at least one calendar; under _sync
    string? _stale;       // why the last fetch failed while _agenda is older; under _sync
    string? _error;       // the last failure with nothing to show; under _sync
    string? _setupNote;   // why the last address was refused, shown once in the setup popup; under _sync
    bool _adding;         // the popup asks for another address; under _sync
    Task? _loop, _ticker;

    public CalendarConnector() : this(new CredentialStore(), new HttpCalendarSource(), () => DateTimeOffset.UtcNow,
        TimeZoneInfo.Local, CultureInfo.CurrentCulture, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose()) { }

    public CalendarConnector(ICalendarStore store, ICalendarSource source, Func<DateTimeOffset> now, TimeZoneInfo zone, CultureInfo culture, Action<string> open)
    {
        _store = store;
        _source = source;
        _now = now;
        _zone = zone;
        _culture = culture;
        _open = open;
    }

    static byte[]? Logo()
    {
        using var stream = typeof(CalendarConnector).Assembly.GetManifestResourceStream("calendar.png");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    CalendarView View() => new(_now(), _zone, _culture);

    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _settings = CalendarSettings.Read(ctx.Settings, ctx.Log);
        try { lock (_sync) _addresses = _store.Read(); }
        catch (Exception ex) { ctx.Log.Warn($"Could not read the saved calendars from Credential Manager: {ex.Message}"); }
        ctx.Update(HasCalendars ? new TileState { Title = CalendarView.Title, Subtitle = "Loading…", Icon = new TileIcon("sync"), Image = _logo, ImageColor = _logo is null ? null : TileColor.Neutral }
            : CalendarView.SetupTile(_logo));
        ctx.Resumed += (_, _) => _ = FetchSafelyAsync(_stop.Token);
        _loop = LoopAsync(_stop.Token);
        _ticker = TickAsync(_stop.Token);
        return Task.CompletedTask;
    }

    bool HasCalendars { get { lock (_sync) return _addresses.Count > 0; } }

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

    /// <summary>Redraws at the start of every minute, so the countdown moves between fetches.</summary>
    async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var now = _now();
                await Task.Delay(TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond), ct);
                Show();
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task FetchSafelyAsync(CancellationToken ct)
    {
        try { await FetchNowAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ctx?.Log.Error("Fetching the calendars failed", ex); }
    }

    (DateTimeOffset From, DateTimeOffset To) Window()
    {
        var today = TimeZoneInfo.ConvertTime(_now(), _zone).Date;
        DateTimeOffset At(DateTime local) => new(local, _zone.GetUtcOffset(local));
        return (At(today.AddDays(-1)), At(today.AddDays(2)));
    }

    /// <summary>Reads one calendar; the error names it by its number, never by its address.</summary>
    async Task<(IReadOnlyList<CalendarEvent>? Events, string? Problem)> ReadAsync(string address, int number, CancellationToken ct)
    {
        var (from, to) = Window();
        string ics;
        try { ics = await _source.GetAsync(address, ct); }
        catch (CalendarFetchException ex) { return (null, $"Calendar {number}: {ex.Message}"); }
        try { return (CalendarParse.Parse(ics, CalendarAddress.Owner(address), from, to, _zone), null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ctx?.Log.Warn($"Calendar {number} could not be read: {ex.GetType().Name}");
            return (null, $"Calendar {number}: not a calendar file");
        }
    }

    /// <summary>One fetch at a time; waits for one already running, then fetches again.</summary>
    public async Task FetchNowAsync(CancellationToken ct)
    {
        await _fetching.WaitAsync(ct);
        try
        {
            IReadOnlyList<string> addresses;
            lock (_sync) addresses = _addresses;
            if (addresses.Count == 0) { Show(); return; }

            var results = await Task.WhenAll(addresses.Select((a, i) => ReadAsync(a, i + 1, ct)));
            var problems = results.Where(r => r.Problem is not null).Select(r => r.Problem!).ToList();
            lock (_sync)
            {
                if (results.Any(r => r.Events is not null))
                {
                    _agenda = new Agenda(results.SelectMany(r => r.Events ?? []).OrderBy(e => e.Start).ToList(), _now(), problems);
                    _stale = null; _error = null;
                }
                else if (_agenda is null) _error = problems.FirstOrDefault();
                else _stale = problems.FirstOrDefault();
            }
            Show();
        }
        finally { _fetching.Release(); }
    }

    void Show()
    {
        var ctx = _ctx;
        if (ctx is null) return;
        Agenda? agenda; string? stale, error; bool any;
        lock (_sync) { agenda = _agenda; stale = _stale; error = _error; any = _addresses.Count > 0; }
        if (!any) ctx.Update(CalendarView.SetupTile(_logo));
        else if (agenda is not null) ctx.Update(View().Tile(agenda, _logo, stale));
        else if (error is not null) ctx.ReportError(error);
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        bool needFetch;
        lock (_sync) needFetch = _agenda is null && _error is null && _addresses.Count > 0 && !_adding;
        if (needFetch) await FetchNowAsync(ct); // the first fetch has not finished: wait for it
        lock (_sync)
        {
            if (_addresses.Count == 0 || _adding)
            {
                var note = _setupNote;
                _setupNote = null;
                return CalendarView.SetupFlyout(_logo, note, _addresses.Count > 0);
            }
            if (_agenda is not null) return View().Flyout(_agenda, _logo, _stale);
            return new FlyoutState(CalendarView.Title, _error ?? "Could not read the calendars", [new FlyoutRow("Add a calendar", Action: "add")],
                [new FlyoutAction("reload", "Try again", Primary: true)], Image: _logo, StayOpen: true);
        }
    }

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        if (actionId.StartsWith("open:", StringComparison.Ordinal)) { Open(actionId[5..]); return; }
        switch (actionId)
        {
            case TileActions.Refresh or "reload": await FetchNowAsync(ct); return;
            case "add": lock (_sync) _adding = true; return;
            case "cancel": lock (_sync) _adding = false; return;
            case "forget": Forget(); return;
            default: _ctx?.Log.Warn($"Unknown action '{actionId}'."); return;
        }
    }

    /// <summary>Opens only Google Calendar pages and video call links.</summary>
    void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host is "calendar.google.com" or "www.google.com" or "meet.google.com" or "teams.microsoft.com"
                || uri.Host == "zoom.us" || uri.Host.EndsWith(".zoom.us", StringComparison.OrdinalIgnoreCase)))
            _open(url);
        else _ctx?.Log.Warn("Not a calendar or video call link; not opened.");
    }

    public async Task OnInputAsync(string actionId, string text, CancellationToken ct)
    {
        if (actionId != "add") { await OnActionAsync(actionId, ct); return; }
        if (CalendarAddress.Normalize(text) is not { } address)
        {
            lock (_sync) _setupNote = "That is not a Google Calendar iCal address";
            return;
        }
        IReadOnlyList<string> addresses;
        lock (_sync) addresses = _addresses;
        if (addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            lock (_sync) { _setupNote = "That calendar is already added"; }
            return;
        }
        if (addresses.Count >= MaxCalendars)
        {
            lock (_sync) _setupNote = $"Up to {MaxCalendars} calendars";
            return;
        }

        var (_, problem) = await ReadAsync(address, addresses.Count + 1, ct);
        if (problem is not null)
        {
            lock (_sync) _setupNote = problem;
            return;
        }
        var updated = addresses.Append(address).ToList();
        try { _store.Write(updated); }
        catch (Exception ex)
        {
            _ctx?.Log.Warn($"Could not save the calendar in Credential Manager: {ex.Message}");
            lock (_sync) _setupNote = "Could not save it in Credential Manager";
            return;
        }
        lock (_sync) { _addresses = updated; _adding = false; }
        _ctx?.Log.Info($"Calendar {updated.Count} added.");
        await FetchNowAsync(ct);
    }

    void Forget()
    {
        try { _store.Delete(); }
        catch (Exception ex) { _ctx?.Log.Warn($"Could not remove the calendars from Credential Manager: {ex.Message}"); return; }
        lock (_sync) { _addresses = []; _agenda = null; _stale = null; _error = null; _adding = false; }
        _ctx?.Log.Info("Calendars forgotten.");
        Show();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var task in new[] { _loop, _ticker })
            if (task is not null)
                try { await task; } catch (OperationCanceledException) { }
        _stop.Dispose();
        _fetching.Dispose();
    }
}
