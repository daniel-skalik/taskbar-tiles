using System.Diagnostics;
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Battery;

/// <summary>
/// Battery of connected devices: Bluetooth (from Windows) and Logitech receivers and devices (HID++). Shows the lowest on
/// the tile and every device in the popup. Setting "interval": how often to read, default 60s.
/// </summary>
[TileConnector("battery")]
public sealed class BatteryConnector : ITileConnector
{
    static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10), MaxInterval = TimeSpan.FromHours(1);

    readonly IReadOnlyList<IBatterySource> _sources;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    ITileContext? _ctx;
    IReadOnlyList<DeviceBattery> _last = [];
    Task? _loop;

    public BatteryConnector() : this([new BluetoothSource(), new LogitechSource(HidTransport.FindAll)]) { }

    public BatteryConnector(IReadOnlyList<IBatterySource> sources) => _sources = sources;

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        var interval = Interval(ctx.Settings, ctx.Log);
        ctx.Resumed += OnResumed;
        await RefreshAsync(ct);
        _loop = LoopAsync(interval, _stop.Token);
    }

    /// <summary>"interval" from settings, clamped to 10s..1h; a bad value is logged and the default used.</summary>
    public static TimeSpan Interval(JsonElement settings, ITileLog log)
    {
        if (!settings.TryGetProperty("interval", out var value)) return DefaultInterval;
        if (value.ValueKind == JsonValueKind.String && Duration.TryParse(value.GetString(), out var parsed))
            return parsed < MinInterval ? MinInterval : parsed > MaxInterval ? MaxInterval : parsed;
        log.Warn($"\"interval\" must be a duration such as \"60s\"; using {DefaultInterval.TotalSeconds:0}s.");
        return DefaultInterval;
    }

    async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await RefreshAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    void OnResumed(object? sender, EventArgs e) => _ = RefreshAsync(_stop.Token);

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        switch (actionId)
        {
            case TileActions.Refresh:
                await RefreshAsync(ct);
                break;
            case BatteryView.BluetoothSettingsAction:
                try { using var _ = Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
                catch (Exception ex) { _ctx?.Log.Warn($"Could not open Bluetooth settings: {ex.Message}"); }
                break;
        }
    }

    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(BatteryView.Flyout(_last));

    /// <summary>Reads every source; one failing is logged and the rest still show. Only when all fail is the error shown.</summary>
    async Task RefreshAsync(CancellationToken ct)
    {
        var ctx = _ctx;
        if (ctx is null) return;
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            var readings = new List<IReadOnlyList<DeviceBattery>>();
            var errors = new List<string>();
            foreach (var source in _sources)
            {
                try { readings.Add(await source.ReadAsync(ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    ctx.Log.Warn($"{source.Name} batteries could not be read: {ex.Message}");
                    errors.Add($"{source.Name}: {ex.Message}");
                }
            }
            if (readings.Count == 0 && errors.Count > 0)
            {
                ctx.ReportError("Could not read batteries", string.Join("\n", errors));
                return;
            }
            _last = Merge(readings);
            ctx.Update(BatteryView.Tile(_last));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// All readings, one per device name: a device seen by several sources (a Logitech device on Bluetooth) keeps the
    /// later source's reading, which knows more (charging).
    /// </summary>
    public static IReadOnlyList<DeviceBattery> Merge(IEnumerable<IReadOnlyList<DeviceBattery>> readings)
    {
        var byName = new Dictionary<string, DeviceBattery>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var device in readings.SelectMany(r => r)) byName[device.Name] = device;
        return byName.Values.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null) _ctx.Resumed -= OnResumed;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        await _gate.WaitAsync(); // a refresh in progress finishes before the sources go
        foreach (var source in _sources) (source as IDisposable)?.Dispose();
        _stop.Dispose();
    }
}
