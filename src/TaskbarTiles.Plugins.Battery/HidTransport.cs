using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace TaskbarTiles.Plugins.Battery;

/// <summary>
/// A Logitech HID++ interface: requests go out on its long-report collection (usage page 0xFF00, usage 2); answers come
/// back there, except HID++ 1.0 errors (such as "no device in this receiver slot"), which arrive as short reports on the
/// interface's usage 1 collection, so both are read. Opened shared so Logi Options+ keeps working.
/// </summary>
public sealed class HidTransport : IHidppTransport
{
    const byte LongReport = 0x11, ShortReport = 0x10, SoftwareId = 0x0A;
    const ushort VendorPage = 0xFF00, ShortUsage = 0x0001, LongUsage = 0x0002;
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    readonly FileStream _long;
    readonly FileStream? _short;
    readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly SemaphoreSlim _gate = new(1, 1);
    volatile bool _dead;

    HidTransport(string path, FileStream longStream, FileStream? shortStream)
    {
        Path = path;
        _long = longStream;
        _short = shortStream;
        _ = PumpAsync(_long, 20, endsTransport: true);
        if (_short is not null) _ = PumpAsync(_short, 7, endsTransport: false);
    }

    public string Path { get; }

    public bool IsDead => _dead;

    /// <summary>Every Logitech HID++ interface present now (USB receivers and devices, and Bluetooth devices).</summary>
    public static async Task<IReadOnlyList<HidppPort>> FindAsync(CancellationToken ct)
    {
        var shortPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await PathsAsync(ShortUsage, ct)) shortPaths.TryAdd(InterfaceKey(path), path);
        return (await PathsAsync(LongUsage, ct))
            .Select(path => new HidppPort(path, shortPaths.GetValueOrDefault(InterfaceKey(path))))
            .ToList();
    }

    static async Task<IEnumerable<string>> PathsAsync(ushort usage, CancellationToken ct) =>
        (await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(VendorPage, usage)).AsTask(ct))
            .Select(i => i.Id)
            .Where(IsLogitech);

    /// <summary>Logitech's vendor id in a USB path (VID_046D) or a Bluetooth LE one (VID&amp;02046d).</summary>
    public static bool IsLogitech(string path) =>
        path.Contains("VID_046D", StringComparison.OrdinalIgnoreCase) || path.Contains("VID&02046D", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The part of a collection path both collections of one interface share: \\?\HID#VID_046D&amp;PID_C548&amp;MI_02&amp;Col02#9&amp;1584edf3&amp;0&amp;0001#{…}
    /// → VID_046D&amp;PID_C548&amp;MI_02#9&amp;1584edf3&amp;0 (collection number and last instance part dropped).
    /// </summary>
    public static string InterfaceKey(string path)
    {
        var parts = path.Split('#');
        if (parts.Length < 3) return path;
        var hardware = parts[1].Split('&').Where(p => !p.StartsWith("Col", StringComparison.OrdinalIgnoreCase));
        var instance = parts[2].Split('&');
        return string.Join('&', hardware) + "#" + string.Join('&', instance.Take(Math.Max(1, instance.Length - 1)));
    }

    /// <summary>Opens the interface; null when its long collection cannot be opened.</summary>
    public static IHidppTransport? Open(HidppPort port)
    {
        if (OpenStream(port.Path) is not { } longStream) return null;
        return new HidTransport(port.Path, longStream, port.ShortPath is { } s ? OpenStream(s) : null);
    }

    static FileStream? OpenStream(string path)
    {
        const uint ReadWrite = 0xC0000000, ShareReadWrite = 3, OpenExisting = 3, Overlapped = 0x40000000;
        var handle = CreateFile(path, ReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    /// <summary>Reads reports until the stream is disposed or the device is unplugged; the long one ending kills the transport.</summary>
    async Task PumpAsync(FileStream stream, int length, bool endsTransport)
    {
        try
        {
            while (true)
            {
                var report = new byte[length];
                if (await stream.ReadAsync(report) == 0) break;
                _reports.Writer.TryWrite(report);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        if (endsTransport) _dead = true;
    }

    public async Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[] args, CancellationToken ct)
    {
        var request = new byte[20];
        request[0] = LongReport;
        request[1] = device;
        request[2] = feature;
        request[3] = (byte)((function << 4) | SoftwareId);
        args.AsSpan(0, Math.Min(args.Length, 16)).CopyTo(request.AsSpan(4));

        await _gate.WaitAsync(ct);
        try
        {
            while (_reports.Reader.TryRead(out _)) { } // notifications, and late answers to requests that timed out
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await _long.WriteAsync(request, timeout.Token);
            while (true)
            {
                var r = await _reports.Reader.ReadAsync(timeout.Token);
                if (Match(request, r) is { } answer) return answer.Length == 0 ? null : answer;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timed out
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ChannelClosedException)
        {
            _dead = true; // unplugged: the next read opens it again
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The parameters of <paramref name="r"/> when it answers <paramref name="request"/>; empty for an error; null when unrelated.</summary>
    public static byte[]? Match(byte[] request, byte[] r)
    {
        if (r.Length < 7 || r[1] != request[1]) return null;
        if (r[0] == ShortReport) // HID++ 1.0 error: [0x10, device, 0x8F, feature, function|sw, code, 0]
            return r[2] == 0x8F && r[3] == request[2] && r[4] == request[3] ? [] : null;
        if (r[0] != LongReport || r.Length < 20) return null;
        if (r[2] == 0xFF && r[3] == request[2] && r[4] == request[3]) return []; // HID++ 2.0 error
        return r[2] == request[2] && r[3] == request[3] ? r[4..] : null;
    }

    public void Dispose()
    {
        _dead = true;
        _long.Dispose();
        _short?.Dispose();
        _reports.Writer.TryComplete();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags,
        IntPtr template);
}
