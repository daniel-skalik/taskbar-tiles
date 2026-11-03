namespace TaskbarTiles.Plugins.SystemMonitor;

/// <summary>The five things the tile can show, in the order they appear on it.</summary>
public enum Metric { Cpu, Ram, Gpu, Network, Disk }

/// <summary>
/// One stat on the tile: a metric, and for <see cref="Metric.Disk"/> the drive ("C:"), as every drive is its own stat.
/// A disk stat without a drive is the fullest one.
/// </summary>
public readonly record struct Stat(Metric Metric, string? Drive = null)
{
    public static implicit operator Stat(Metric metric) => new(metric);
}

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
    /// <summary>The fullest drive; null without any drive.</summary>
    public DiskReading? FullestDisk => Disks.Count == 0 ? null : Disks.MaxBy(d => d.Percent);

    /// <summary>The drive named <paramref name="name"/> ("C:", ignoring case), or the fullest one for null.</summary>
    public DiskReading? Disk(string? name) =>
        name is null ? FullestDisk : Disks.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the machine. Behind an interface so tests can feed readings.</summary>
public interface ISystemSampler : IDisposable
{
    SystemReading Read();
}

/// <summary>The last ~60 values of each stat, for the sparklines: every drive on its own. Network keeps the download rate.</summary>
public sealed class SystemHistory(int capacity = 60)
{
    readonly Dictionary<Stat, List<double>> _values = [];

    public void Push(SystemReading r)
    {
        Add(Metric.Cpu, r.Cpu);
        Add(Metric.Ram, r.Ram.Percent);
        if (r.Gpu is { } gpu) Add(Metric.Gpu, gpu); // n/a leaves a gap rather than a fake zero line
        Add(Metric.Network, r.DownBytesPerSecond);
        foreach (var disk in r.Disks) Add(new Stat(Metric.Disk, disk.Name.ToUpperInvariant()), disk.Percent);
    }

    void Add(Stat stat, double value)
    {
        if (!_values.TryGetValue(stat, out var list)) _values[stat] = list = [];
        list.Add(value);
        if (list.Count > capacity) list.RemoveAt(0);
    }

    public IReadOnlyList<double> Get(Stat stat) =>
        _values.TryGetValue(stat with { Drive = stat.Drive?.ToUpperInvariant() }, out var list) ? list.ToList() : [];
}
