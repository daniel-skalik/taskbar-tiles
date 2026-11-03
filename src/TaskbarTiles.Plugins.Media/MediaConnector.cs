using System.Diagnostics;
using System.Text.Json;
using TaskbarTiles.Contracts;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarTiles.Plugins.Media;

/// <summary>
/// Now-playing tile for an app's Windows media session (any app by default). Setting "app" names one app, several
/// ("Spotify, Firefox": the one playing, else the first listed) or "Any"; see <see cref="AppFilter"/>.
/// Ported from SpotifyTaskbar: no Spotify API or login, just the system media controls.
/// The song's position shows as a thin bar along the tile's bottom edge, moved every second while it plays.
/// </summary>
[TileConnector("media", Width = 268)]
public sealed class MediaConnector : ITileConnector, ITileChoices
{
    readonly SemaphoreSlim _gate = new(1, 1);
    ITileContext? _ctx;
    AppFilter _filter = AppFilter.Parse("Any");
    string _launch = ""; // empty: nothing to open (with "Any")
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly object _pickLock = new();
    volatile GlobalSystemMediaTransportControlsSession? _session;
    volatile bool _disposed;
    string? _artKey;
    byte[]? _art;
    TileState? _shown;
    readonly CancellationTokenSource _stop = new();
    Task? _ticker;

    static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        if (ctx.Settings.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String) _filter = AppFilter.Parse(app.GetString());
        _launch = ctx.Settings.TryGetProperty("launch", out var launch) && launch.ValueKind == JsonValueKind.String
            ? launch.GetString()!
            : _filter.Any ? "" : _filter.Apps[0].ToLowerInvariant() + ":";

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        lock (_pickLock)
        {
            if (_disposed) return;
            _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            ctx.Resumed += OnResumed;
            PickSession();
        }
        await PublishAsync();
        _ticker = TickAsync(_stop.Token);
    }

    /// <summary>
    /// The apps with a media session now, as names "app" matches, for the settings dialog's suggestions: their Start
    /// menu name ("Firefox", "Media Player") where Windows knows one, else a short form of the session id.
    /// </summary>
    public Task<IReadOnlyList<string>> GetChoicesAsync(string key, CancellationToken ct)
    {
        if (key != "app" || _manager is not { } manager) return Task.FromResult<IReadOnlyList<string>>([]);
        // "Any" stays the first suggestion, ahead of the apps playing now.
        IReadOnlyList<string> names = manager.GetSessions()
            .Select(s => AppNames.DisplayName(s.SourceAppUserModelId) ?? AppName(s.SourceAppUserModelId))
            .Where(n => n.Length > 0).Prepend("Any").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(names);
    }

    /// <summary>
    /// A short name for an app's media session id that still matches it: "Spotify.exe" is "Spotify", a packaged app's
    /// "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" is "Microsoft.ZuneMusic", a path its file name.
    /// </summary>
    public static string AppName(string? aumid)
    {
        var id = (aumid ?? "").Trim();
        var bang = id.IndexOf('!');
        if (bang >= 0)
        {
            id = id[..bang];
            var underscore = id.LastIndexOf('_'); // the package family's publisher hash
            if (underscore > 0) id = id[..underscore];
        }
        id = Path.GetFileName(id);
        return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
    }

    /// <summary>
    /// How far the song is, 0..1, at <paramref name="now"/>: Windows reports the position as of LastUpdatedTime (Spotify
    /// only on seeks and track changes), so while playing the time since then is added. Null without a known length.
    /// </summary>
    public static double? Progress(TimeSpan start, TimeSpan end, TimeSpan position, DateTimeOffset updated, bool playing, DateTimeOffset now)
    {
        var length = end - start;
        if (length <= TimeSpan.Zero) return null;
        var at = position - start;
        if (playing && now > updated) at += now - updated;
        return Math.Clamp(at / length, 0, 1);
    }

    static double? Progress(GlobalSystemMediaTransportControlsSession s, bool playing)
    {
        var t = s.GetTimelineProperties();
        return t is null ? null : Progress(t.StartTime, t.EndTime, t.Position, t.LastUpdatedTime, playing, DateTimeOffset.Now);
    }

    /// <summary>Moves the bar while a song plays; only the progress of the last state changes.</summary>
    async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // With several apps, another one may have started playing: the tile follows it within a second.
                if (_filter.Several && !_disposed)
                {
                    bool switched;
                    lock (_pickLock) switched = PickSession(byApp: true);
                    if (switched) await PublishAsync();
                }
                await _gate.WaitAsync(ct);
                try
                {
                    if (_disposed || _shown is not { Progress: not null } shown || _session is not { } s) continue;
                    if (s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                    var progress = Progress(s, playing: true);
                    if (progress == shown.Progress) continue;
                    _shown = shown with { Progress = progress };
                    _ctx?.Update(_shown);
                }
                catch (Exception ex) { _ctx?.Log.Warn($"Media position update failed: {ex.Message}"); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        var s = _session;
        switch (actionId)
        {
            case "prev" when s is not null: await s.TrySkipPreviousAsync(); break;
            case "toggle" when s is not null: await s.TryTogglePlayPauseAsync(); break;
            case "next" when s is not null: await s.TrySkipNextAsync(); break;
            case "open":
            case "click":
                Launch();
                break;
        }
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null; // a click then launches the app

        var props = await s.TryGetMediaPropertiesAsync();
        if (props is null) return null;
        var timeline = s.GetTimelineProperties();
        var rows = new List<FlyoutRow>();
        if (!string.IsNullOrEmpty(props.AlbumTitle)) rows.Add(new FlyoutRow("Album", props.AlbumTitle));
        if (timeline.EndTime > TimeSpan.Zero) rows.Add(new FlyoutRow("Position", $"{Format(timeline.Position)} / {Format(timeline.EndTime)}"));

        return new FlyoutState(
            string.IsNullOrEmpty(props.Title) ? SessionName(s) : props.Title,
            props.Artist,
            rows,
            _launch.Length > 0 ? [new FlyoutAction("open", $"Open {(_filter.Any ? "app" : _filter.Apps[0])}", Primary: true)] : [],
            _art);
    }

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            lock (_pickLock) PickSession();
            _ = PublishAsync();
        }
        catch (Exception ex) { _ctx?.Log.Warn($"Media sessions change failed: {ex.Message}"); }
    }

    void OnResumed(object? sender, EventArgs e) => _ = PublishAsync();

    /// <summary>The app a session belongs to, as people know it: "Firefox" rather than its id.</summary>
    static string SessionName(GlobalSystemMediaTransportControlsSession s) =>
        AppNames.DisplayName(s.SourceAppUserModelId) ?? AppName(s.SourceAppUserModelId);

    // Caller must hold _pickLock. True when the tile now follows another session. The once-a-second look (byApp) keeps
    // the session of the same app; a change in the sessions list takes the new object even for the same app.
    bool PickSession(bool byApp = false)
    {
        if (_disposed || _manager is not { } manager) return false;
        var sessions = manager.GetSessions();
        var current = manager.GetCurrentSession();
        var infos = sessions.Select(x => new SessionInfo(x.SourceAppUserModelId, AppNames.DisplayName(x.SourceAppUserModelId),
            x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            current is not null && x.SourceAppUserModelId == current.SourceAppUserModelId)).ToList();
        var chosen = _filter.Choose(infos);
        var match = chosen < 0 ? null : sessions[chosen];
        if (ReferenceEquals(match, _session)) return false;
        if (byApp && match is not null && _session is not null && match.SourceAppUserModelId == _session.SourceAppUserModelId) return false;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = match;
        _artKey = null;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        return true;
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => SafePublish();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => SafePublish();
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => SafePublish(); // a seek

    void SafePublish()
    {
        if (_disposed) return;
        try { _ = PublishAsync(); }
        catch (Exception ex) { _ctx?.Log.Warn($"Media update failed: {ex.Message}"); }
    }

    async Task PublishAsync()
    {
        var ctx = _ctx;
        if (ctx is null || _disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            var s = _session;
            if (s is null)
            {
                _shown = null;
                ctx.Update(new TileState
                {
                    Title = _filter.Label,
                    Subtitle = _filter.Any ? "Nothing playing" : "Not running",
                    Icon = new TileIcon("music"),
                    Tooltip = _launch.Length > 0 ? $"Click to open {_filter.Apps.FirstOrDefault() ?? _launch}" : "Plays here when an app plays media",
                    Buttons =
                    [
                        new TileButton("prev", "previous", "Previous", false),
                        new TileButton("toggle", "play", "Play/Pause", false),
                        new TileButton("next", "next", "Next", false),
                    ],
                });
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            if (props is null) return;
            var playback = s.GetPlaybackInfo();
            var playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var key = $"{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artKey)
            {
                _art = null;
                _art = props.Thumbnail is null ? null : await ReadAllAsync(props.Thumbnail);
                // Spotify often reports the track before its thumbnail; only cache once art arrived.
                if (_art is not null) _artKey = key;
            }

            if (_disposed) return;
            _shown = new TileState
            {
                Title = string.IsNullOrEmpty(props.Title) ? SessionName(s) : props.Title,
                Subtitle = props.Artist,
                Image = _art,
                Icon = new TileIcon("music"),
                Tooltip = $"{props.Title}\n{props.Artist}\n{props.AlbumTitle}",
                Buttons =
                [
                    new TileButton("prev", "previous", "Previous", playback?.Controls.IsPreviousEnabled ?? false),
                    new TileButton("toggle", playing ? "pause" : "play", "Play/Pause", true),
                    new TileButton("next", "next", "Next", playback?.Controls.IsNextEnabled ?? false),
                ],
                Progress = Progress(s, playing),
            };
            ctx.Update(_shown);
        }
        catch (Exception ex)
        {
            ctx.Log.Warn($"Media update failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<byte[]> ReadAllAsync(IRandomAccessStreamReference reference)
    {
        using var winStream = await reference.OpenReadAsync();
        using var stream = winStream.AsStreamForRead();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    void Launch()
    {
        if (_launch.Length == 0) return;
        try { using var _ = Process.Start(new ProcessStartInfo(_launch) { UseShellExecute = true }); }
        catch (Exception ex) { _ctx?.Log.Warn($"Could not open {_launch}: {ex.Message}"); }
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_ticker is not null) await _ticker;
        lock (_pickLock)
        {
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            if (_ctx is not null) _ctx.Resumed -= OnResumed;
            var s = _session;
            if (s is not null)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
        }
        _stop.Dispose();
    }
}
