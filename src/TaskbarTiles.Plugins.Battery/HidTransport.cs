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
    const string LogitechVid = "VID_046D";
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    readonly FileStream _long;
    readonly FileStream? _short;
    readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly SemaphoreSlim _gate = new(1, 1);

    HidTransport(string path, FileStream longStream, FileStream? shortStream)
    {
        Path = path;
        _long = longStream;
        _short = shortStream;
        _ = PumpAsync(_long, 20);
        if (_short is not null) _ = PumpAsync(_short, 7);
    }

    public string Path { get; }

    /// <summary>Every Logitech HID++ interface that can be opened now.</summary>
    public static IReadOnlyList<IHidppTransport> FindAll()
    {
        var shortPaths = Paths(ShortUsage).ToDictionary(InterfaceKey, p => p, StringComparer.OrdinalIgnoreCase);
        var found = new List<IHidppTransport>();
        foreach (var path in Paths(LongUsage))
        {
            if (Open(path) is not { } longStream) continue;
            var shortStream = shortPaths.TryGetValue(InterfaceKey(path), out var s) ? Open(s) : null;
            found.Add(new HidTransport(path, longStream, shortStream));
        }
        return found;
    }

    static IEnumerable<string> Paths(ushort usage) =>
        DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(VendorPage, usage)).AsTask().GetAwaiter().GetResult()
            .Select(i => i.Id)
            .Where(id => id.Contains(LogitechVid, StringComparison.OrdinalIgnoreCase));

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

    static FileStream? Open(string path)
    {
        const uint ReadWrite = 0xC0000000, ShareReadWrite = 3, OpenExisting = 3, Overlapped = 0x40000000;
        var handle = CreateFile(path, ReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    /// <summary>Reads reports until the stream is disposed or the device is unplugged.</summary>
    async Task PumpAsync(FileStream stream, int length)
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
            while (_reports.Reader.TryRead(out _)) { } // notifications that arrived between requests
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await _long.WriteAsync(request, timeout.Token);
            while (true)
            {
                byte[] r;
                try { r = await _reports.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timed out
                if (Match(request, r) is { } answer) return answer.Length == 0 ? null : answer;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; } // unplugged; the next scan drops it
        finally { _gate.Release(); }
    }

    /// <summary>The parameters of <paramref name="r"/> when it answers <paramref name="request"/>; empty for an error; null when unrelated.</summary>
    public static byte[]? Match(byte[] request, byte[] r)
    {
        if (r.Length < 7 || r[1] != request[1]) return null;
        if (r[0] == ShortReport && r[2] == 0x8F && r[3] == request[2]) return []; // HID++ 1.0 error
        if (r[0] != LongReport || r.Length < 20) return null;
        if (r[2] == 0xFF && r[3] == request[2] && r[4] == request[3]) return []; // HID++ 2.0 error
        return r[2] == request[2] && r[3] == request[3] ? r[4..] : null;
    }

    public void Dispose()
    {
        _long.Dispose();
        _short?.Dispose();
        _reports.Writer.TryComplete();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags,
        IntPtr template);
}
