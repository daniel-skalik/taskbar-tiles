using System.Text;

namespace TaskbarTiles.Plugins.Battery;

/// <summary>
/// One Logitech HID++ long-report collection: a receiver (Bolt, Unifying) or a directly connected device. Implementations
/// send a HID++ 2.0 request and return the response's parameter bytes (16 bytes after the 4-byte header), or null on an
/// error response or a timeout. Requests on one transport are sequential.
/// </summary>
public interface IHidppTransport : IDisposable
{
    /// <summary>Stable while the collection stays plugged in; devices are cached under it.</summary>
    string Path { get; }

    /// <summary>The handle stopped working (the receiver was unplugged); a new one must be opened.</summary>
    bool IsDead { get; }

    Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[] args, CancellationToken ct);
}

/// <summary>A HID++ interface found on the system: its long-report collection, and its short one when there is one.</summary>
public sealed record HidppPort(string Path, string? ShortPath = null);

/// <summary>
/// Reads batteries over HID++ 2.0 from Logitech receivers and devices. Interfaces stay open between reads; a dead one is
/// reopened. Feature indices, names and types are cached per interface path and device index; a device that stops
/// answering keeps its last reading, marked asleep.
/// </summary>
public sealed class LogitechSource(
    Func<CancellationToken, Task<IReadOnlyList<HidppPort>>> findPorts,
    Func<HidppPort, IHidppTransport?> open) : IBatterySource, IDisposable
{
    public const byte DirectDevice = 0xFF;
    const ushort Root = 0x0000, DeviceName = 0x0005, BatteryStatus = 0x1000, UnifiedBattery = 0x1004;

    sealed class Known
    {
        public required string Name;
        public required DeviceType Type;
        public byte? Unified, Status;
        public bool PercentSupported;
        public DeviceBattery? Last;
    }

    readonly Dictionary<string, IHidppTransport> _transports = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Path, byte Device), Known> _known = [];

    public string Name => "Logitech";

    public async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        var result = new List<DeviceBattery>();
        foreach (var transport in _transports.Values)
        {
            try
            {
                foreach (var device in await DevicesAsync(transport, ct))
                    if (await ReadDeviceAsync(transport, device, ct) is { } reading) result.Add(reading);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One misbehaving interface must not hide the others' devices; it is retried next time.
            }
        }
        return result;
    }

    /// <summary>
    /// Opens interfaces not open yet, reopens dead ones (a receiver plugged back in keeps its path, and its devices'
    /// cache), and closes and forgets unplugged ones.
    /// </summary>
    async Task RefreshAsync(CancellationToken ct)
    {
        var ports = await findPorts(ct);
        var paths = ports.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _transports.Keys.Where(p => !paths.Contains(p)).ToList())
        {
            _transports[gone].Dispose();
            _transports.Remove(gone);
            foreach (var key in _known.Keys.Where(k => string.Equals(k.Path, gone, StringComparison.OrdinalIgnoreCase)).ToList())
                _known.Remove(key);
        }
        foreach (var port in ports)
        {
            if (_transports.TryGetValue(port.Path, out var current))
            {
                if (!current.IsDead) continue;
                current.Dispose();
                _transports.Remove(port.Path);
            }
            if (open(port) is { } opened) _transports[port.Path] = opened;
        }
    }

    /// <summary>0xFF when the collection is itself a HID++ 2.0 device, else receiver slots 1..6.</summary>
    async Task<IReadOnlyList<byte>> DevicesAsync(IHidppTransport t, CancellationToken ct)
    {
        if (_known.ContainsKey((t.Path, DirectDevice))) return [DirectDevice];
        if (await PingAsync(t, DirectDevice, ct)) return [DirectDevice];
        return Enumerable.Range(1, 6).Select(i => (byte)i).ToList();
    }

    /// <summary>Root function 1 answers with the protocol version; only HID++ 2.0 and later devices answer it.</summary>
    static async Task<bool> PingAsync(IHidppTransport t, byte device, CancellationToken ct) =>
        await t.RequestAsync(device, 0, 1, [0, 0, 0x5A], ct) is { } r && r[0] >= 2;

    async Task<DeviceBattery?> ReadDeviceAsync(IHidppTransport t, byte device, CancellationToken ct)
    {
        var key = (t.Path, device);
        if (!_known.TryGetValue(key, out var known))
        {
            if (await LearnAsync(t, device, ct) is not { } learned) return null;
            _known[key] = known = learned;
        }

        var reading = await ReadBatteryAsync(t, device, known, ct);
        if (reading is null) return known.Last is null ? null : known.Last with { Asleep = true };
        known.Last = reading;
        return reading;
    }

    /// <summary>Name, type and battery features of a device that answers; null for an empty slot or no battery feature.</summary>
    static async Task<Known?> LearnAsync(IHidppTransport t, byte device, CancellationToken ct)
    {
        if (!await PingAsync(t, device, ct)) return null;
        var unified = await FeatureAsync(t, device, UnifiedBattery, ct);
        var status = unified is null ? await FeatureAsync(t, device, BatteryStatus, ct) : null;
        if (unified is null && status is null) return null;

        var (name, type) = await NameAsync(t, device, ct);
        var known = new Known { Name = name ?? $"Logitech device {device}", Type = type, Unified = unified, Status = status };
        if (unified is { } u && await t.RequestAsync(device, u, 0, [], ct) is { } caps) known.PercentSupported = (caps[1] & 0x02) != 0;
        return known;
    }

    static async Task<byte?> FeatureAsync(IHidppTransport t, byte device, ushort id, CancellationToken ct) =>
        await t.RequestAsync(device, (byte)Root, 0, [(byte)(id >> 8), (byte)id], ct) is { } r && r[0] != 0 ? r[0] : null;

    static async Task<(string? Name, DeviceType Type)> NameAsync(IHidppTransport t, byte device, CancellationToken ct)
    {
        if (await FeatureAsync(t, device, DeviceName, ct) is not { } index) return (null, DeviceType.Other);
        var length = (await t.RequestAsync(device, index, 0, [], ct))?[0] ?? 0;
        var name = new StringBuilder();
        while (name.Length < length)
        {
            if (await t.RequestAsync(device, index, 1, [(byte)name.Length], ct) is not { } chunk) break;
            var take = Math.Min(Math.Min(16, chunk.Length), length - name.Length);
            if (take <= 0) break;
            name.Append(Encoding.ASCII.GetString(chunk, 0, take));
        }
        var type = (await t.RequestAsync(device, index, 2, [], ct))?[0] switch
        {
            0 => DeviceType.Keyboard,
            3 or 4 or 5 => DeviceType.Mouse, // mouse, trackpad, trackball
            8 => DeviceType.Headphones,
            _ => DeviceType.Other,
        };
        var text = name.ToString().TrimEnd('\0', ' ');
        return (text.Length > 0 ? text : null, type);
    }

    static async Task<DeviceBattery?> ReadBatteryAsync(IHidppTransport t, byte device, Known k, CancellationToken ct)
    {
        var id = $"logitech:{t.Path}:{device}";
        if (k.Unified is { } u)
        {
            if (await t.RequestAsync(device, u, 1, [], ct) is not { } s) return null;
            // [percent, level bits (1 critical, 2 low, 4 good, 8 full), charging (0 no, 1 yes, 2 slow, 3 complete, 4 error), power]
            BatteryLevel? level = (s[1] & 8) != 0 ? BatteryLevel.Full : (s[1] & 4) != 0 ? BatteryLevel.Good
                : (s[1] & 2) != 0 ? BatteryLevel.Low : (s[1] & 1) != 0 ? BatteryLevel.Critical : null;
            int? percent = k.PercentSupported ? Math.Min((int)s[0], 100) : s[2] == 3 ? 100 : null;
            return new DeviceBattery(id, k.Name, k.Type, percent, level, Charging: s[2] is 1 or 2);
        }
        if (k.Status is { } b)
        {
            if (await t.RequestAsync(device, b, 0, [], ct) is not { } s) return null;
            // [percent, next level, status (0 discharging, 1 recharging, 2 almost full, 3 full, 4 slow recharge, ...)]
            return new DeviceBattery(id, k.Name, k.Type, s[0] == 0 && s[2] == 3 ? 100 : Math.Min((int)s[0], 100),
                Charging: s[2] is 1 or 2 or 4);
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var t in _transports.Values) t.Dispose();
        _transports.Clear();
    }
}
