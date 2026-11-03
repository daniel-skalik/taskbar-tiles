using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.SystemMonitor;

/// <summary>Reads the real machine: GetSystemTimes, GlobalMemoryStatusEx, PDH for the GPU, network interfaces and drives.</summary>
public sealed partial class WindowsSampler : ISystemSampler
{
    const string GpuCounter = @"\GPU Engine(*engtype_3D)\Utilization Percentage";

    readonly ITileLog _log;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<string, (long Received, long Sent)> _nics = [];
    (ulong Idle, ulong Kernel, ulong User)? _cpu;
    TimeSpan _nicAt;
    IntPtr _query, _counter;
    bool _gpuBroken;

    public WindowsSampler(ITileLog log)
    {
        _log = log;
        OpenGpu();
        // Prime the deltas so the first real reading covers a whole interval.
        _cpu = ReadTimes();
        ReadNetwork();
        if (_query != IntPtr.Zero) Native.PdhCollectQueryData(_query);
    }

    public SystemReading Read()
    {
        var cpu = ReadTimes();
        var percent = _cpu is { } before && cpu is { } now ? CpuPercent(before, now) : 0;
        _cpu = cpu;
        var (down, up) = ReadNetwork();
        return new SystemReading(percent, ReadMemory(), ReadGpu(), down, up, ReadDisks(), Environment.ProcessorCount);
    }

    /// <summary>Busy share of the time between two samples. Kernel time includes idle time, so busy = kernel + user - idle.</summary>
    public static double CpuPercent((ulong Idle, ulong Kernel, ulong User) before, (ulong Idle, ulong Kernel, ulong User) now)
    {
        var idle = now.Idle - before.Idle;
        var total = (now.Kernel - before.Kernel) + (now.User - before.User);
        if (total == 0 || idle > total) return 0;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    static (ulong Idle, ulong Kernel, ulong User)? ReadTimes() =>
        Native.GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : null;

    static MemoryReading ReadMemory()
    {
        var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return new MemoryReading(0, 0);
        return new MemoryReading((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    static List<DiskReading> ReadDisks()
    {
        var disks = new List<DiskReading>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                disks.Add(new DiskReading(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException) { } // a drive that vanishes between the checks is just skipped
            catch (UnauthorizedAccessException) { }
        }
        return disks;
    }

    /// <summary>Bytes per second summed over interfaces that are up. An interface seen for the first time counts from the next sample on.</summary>
    (double Down, double Up) ReadNetwork()
    {
        var at = _clock.Elapsed;
        var seconds = (at - _nicAt).TotalSeconds;
        _nicAt = at;
        long received = 0, sent = 0;
        var current = new Dictionary<string, (long, long)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                if (!_nics.TryGetValue(nic.Id, out var previous)) continue;
                received += Math.Max(0, stats.BytesReceived - previous.Received);
                sent += Math.Max(0, stats.BytesSent - previous.Sent);
            }
        }
        catch (NetworkInformationException ex)
        {
            _log.Warn($"Network counters could not be read: {ex.Message}");
        }
        _nics.Clear();
        foreach (var (id, value) in current) _nics[id] = value;
        return seconds > 0 ? (received / seconds, sent / seconds) : (0, 0);
    }

    void OpenGpu()
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            Fail("the performance counter query could not be opened");
            return;
        }
        var status = Native.PdhAddEnglishCounterW(_query, GpuCounter, IntPtr.Zero, out _counter);
        if (status != 0) Fail($"the GPU counter is not available (0x{status:X8})");
    }

    void Fail(string why)
    {
        if (_gpuBroken) return;
        _gpuBroken = true;
        _log.Warn($"GPU usage will show n/a: {why}.");
    }

    double? ReadGpu()
    {
        if (_gpuBroken || _query == IntPtr.Zero) return null;
        var status = Native.PdhCollectQueryData(_query);
        if (status != 0) { Fail($"collecting counters failed (0x{status:X8})"); return null; }

        uint size = 0, count;
        status = Native.PdhGetFormattedCounterArrayW(_counter, Native.PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (status != Native.PdhMoreData || size == 0) return status == 0 ? 0 : null; // no 3D engine yet, or no value yet
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = Native.PdhGetFormattedCounterArrayW(_counter, Native.PdhFmtDouble, ref size, out count, buffer);
            if (status != 0) return null; // the instance set changed between the calls; the next second will do
            var items = new List<(string, double)>();
            var stride = Marshal.SizeOf<Native.PdhItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PdhItem>(buffer + i * stride);
                if (item.Status > 1) continue;
                items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
            return GpuPercent(items);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [GeneratedRegex(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Adapter();

    /// <summary>
    /// 3D load of the busiest adapter. The engines of one adapter add up (clamped to 100); the adapter is the luid and
    /// phys part of the instance name, and the busiest adapter wins so an idle integrated GPU does not hide a busy one.
    /// </summary>
    public static double GpuPercent(IEnumerable<(string Instance, double Value)> instances)
    {
        var perAdapter = new Dictionary<string, double>();
        foreach (var (instance, value) in instances)
        {
            var m = Adapter().Match(instance);
            var key = m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value : "";
            perAdapter[key] = perAdapter.GetValueOrDefault(key) + Math.Max(0, value);
        }
        return perAdapter.Count == 0 ? 0 : Math.Min(100, perAdapter.Values.Max());
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
