namespace TaskbarTiles.Plugins.SystemMonitor;

/// <summary>The five things the tile can show, in the order they appear on it.</summary>
public enum Metric { Cpu, Ram, Gpu, Network, Disk }

public sealed record MemoryReading(long UsedBytes, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public sealed record DiskReading(string Name, long TotalBytes, long FreeBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
}

/// <summary>One second's readings. Gpu is null when the counters are not available; rates are bytes per second.</summary>
public sealed record SystemReading(
    double Cpu,
    MemoryReading Ram,
    double? Gpu,
    double DownBytesPerSecond,
    double UpBytesPerSecond,
    IReadOnlyList<DiskReading> Disks,
    int LogicalCores = 0)
{
    /// <summary>The fullest drive, the one the tile shows; null without any drive.</summary>
    public DiskReading? FullestDisk => Disks.Count == 0 ? null : Disks.MaxBy(d => d.Percent);
}

/// <summary>Reads the machine. Behind an interface so tests can feed readings.</summary>
public interface ISystemSampler : IDisposable
{
    SystemReading Read();
}

/// <summary>The last ~60 values of each metric, for the sparklines. Network keeps the download rate.</summary>
public sealed class SystemHistory(int capacity = 60)
{
    readonly Dictionary<Metric, List<double>> _values = Enum.GetValues<Metric>().ToDictionary(m => m, _ => new List<double>());

    public void Push(SystemReading r)
    {
        Add(Metric.Cpu, r.Cpu);
        Add(Metric.Ram, r.Ram.Percent);
        if (r.Gpu is { } gpu) Add(Metric.Gpu, gpu); // n/a leaves a gap rather than a fake zero line
        Add(Metric.Network, r.DownBytesPerSecond);
        if (r.FullestDisk is { } disk) Add(Metric.Disk, disk.Percent);
    }

    void Add(Metric metric, double value)
    {
        var list = _values[metric];
        list.Add(value);
        if (list.Count > capacity) list.RemoveAt(0);
    }

    public IReadOnlyList<double> Get(Metric metric) => _values[metric].ToList();
}
