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
    readonly CancellationToken _stopping;
    volatile bool _disposed;
    ITileContext? _ctx;
    IReadOnlyList<DeviceBattery> _last = [];
    Task? _loop;

    public BatteryConnector() : this([new BluetoothSource(), new LogitechSource(HidTransport.FindAsync, HidTransport.Open)]) { }

    public BatteryConnector(IReadOnlyList<IBatterySource> sources)
    {
        _sources = sources;
        _stopping = _stop.Token; // kept so a late StartAsync or resume never touches the disposed source
    }

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        var interval = Interval(ctx.Settings, ctx.Log);
        ctx.Resumed += OnResumed;
        await RefreshAsync(ct);
        if (!_disposed) _loop = LoopAsync(interval, _stopping);
    }

    /// <summary>"interval" from settings, clamped to 10s..1h; a bad value is logged and the default used.</summary>
    public static TimeSpan Interval(JsonElement settings, ITileLog log)
    {
        if (!settings.TryGetProperty("interval", out var value)) return DefaultInterval;
        if (value.ValueKind == JsonValueKind.String && Duration.TryParse(value.GetString(), out var parsed))
        {
            if (parsed >= MinInterval && parsed <= MaxInterval) return parsed;
            var clamped = parsed < MinInterval ? MinInterval : MaxInterval;
            log.Warn($"\"interval\" must be from 10s to 1h; using {clamped.TotalSeconds:0}s.");
            return clamped;
        }
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

    void OnResumed(object? sender, EventArgs e)
    {
        if (!_disposed) _ = RefreshAsync(_stopping);
    }

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
        if (ctx is null || _disposed) return;
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
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
    /// All readings. A device seen by several sources (a Logitech device on Bluetooth too) appears once: an awake
    /// reading beats an asleep one, else the later source's, which knows more (charging). Devices of one source are
    /// never merged, so two identical headsets both show.
    /// </summary>
    public static IReadOnlyList<DeviceBattery> Merge(IEnumerable<IReadOnlyList<DeviceBattery>> readings)
    {
        var merged = new List<DeviceBattery>();
        foreach (var source in readings)
        {
            var earlier = merged.ToList();
            foreach (var device in source)
            {
                var same = earlier.FindIndex(d => string.Equals(d.Name, device.Name, StringComparison.CurrentCultureIgnoreCase));
                if (same < 0) { merged.Add(device); continue; }
                var index = merged.IndexOf(earlier[same]);
                earlier.RemoveAt(same); // matched once
                if (!device.Asleep || merged[index].Asleep) merged[index] = device;
            }
        }
        return merged;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ctx is not null) _ctx.Resumed -= OnResumed;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        await _gate.WaitAsync(); // a refresh in progress finishes before the sources go
        foreach (var source in _sources) (source as IDisposable)?.Dispose();
        _gate.Release();
        _stop.Dispose();
    }
}
