using System.Diagnostics;
using System.Text.Json;
using TaskbarTiles.Contracts;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarTiles.Plugins.Media;

/// <summary>
/// Now-playing tile for one app's Windows media session (Spotify by default).
/// Ported from SpotifyTaskbar: no Spotify API or login, just the system media controls.
/// </summary>
[TileConnector("media", Width = 268)]
public sealed class MediaConnector : ITileConnector
{
    readonly SemaphoreSlim _gate = new(1, 1);
    ITileContext? _ctx;
    string _app = "Spotify";
    string _launch = "spotify:";
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly object _pickLock = new();
    volatile GlobalSystemMediaTransportControlsSession? _session;
    volatile bool _disposed;
    string? _artKey;
    byte[]? _art;

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        if (ctx.Settings.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String) _app = app.GetString()!;
        _launch = ctx.Settings.TryGetProperty("launch", out var launch) && launch.ValueKind == JsonValueKind.String
            ? launch.GetString()!
            : _app.ToLowerInvariant() + ":";

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
            string.IsNullOrEmpty(props.Title) ? _app : props.Title,
            props.Artist,
            rows,
            [new FlyoutAction("open", $"Open {_app}", Primary: true)],
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

    // Caller must hold _pickLock.
    void PickSession()
    {
        if (_disposed) return;
        var match = _manager?.GetSessions()
            .FirstOrDefault(x => x.SourceAppUserModelId.Contains(_app, StringComparison.OrdinalIgnoreCase));
        if (match == _session) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = match;
        _artKey = null;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => SafePublish();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => SafePublish();

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
                ctx.Update(new TileState
                {
                    Title = _app,
                    Subtitle = "Not running",
                    Icon = new TileIcon("music"),
                    Tooltip = $"Click to open {_app}",
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
            ctx.Update(new TileState
            {
                Title = string.IsNullOrEmpty(props.Title) ? _app : props.Title,
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
            });
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
        try { using var _ = Process.Start(new ProcessStartInfo(_launch) { UseShellExecute = true }); }
        catch (Exception ex) { _ctx?.Log.Warn($"Could not open {_launch}: {ex.Message}"); }
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_pickLock)
        {
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            if (_ctx is not null) _ctx.Resumed -= OnResumed;
            var s = _session;
            if (s is not null)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
            }
        }
        return ValueTask.CompletedTask;
    }
}
