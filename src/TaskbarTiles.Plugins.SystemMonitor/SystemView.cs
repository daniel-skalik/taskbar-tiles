using System.Globalization;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.SystemMonitor;

/// <summary>One metric as text: Name "RAM", Value "48%", Detail "7.7 of 16 GB". Percent is null where there is none.</summary>
public sealed record MetricText(Metric Metric, string Name, string Value, string? Detail, double? Percent, string Compact);

/// <summary>Turns readings into the tile and its popup, and parses the tile's settings. Pure.</summary>
public static class SystemView
{
    public const string TaskManagerAction = "taskmgr";
    public const double WarnPercent = 80, BadPercent = 95;

    static readonly TimeSpan DefaultRotate = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MinRotate = TimeSpan.FromSeconds(2), MaxRotate = TimeSpan.FromMinutes(1);

    static string Pct(double p) => $"{Math.Round(p, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";

    // --- settings -------------------------------------------------------------------------------------------------

    /// <summary>"show" from settings in the tile's fixed order; every metric when absent or unusable (logged).</summary>
    public static IReadOnlyList<Metric> Shown(System.Text.Json.JsonElement settings, ITileLog log)
    {
        var all = Enum.GetValues<Metric>();
        if (settings.ValueKind != System.Text.Json.JsonValueKind.Object || !settings.TryGetProperty("show", out var value)) return all;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array
            || !value.EnumerateArray().All(x => x.ValueKind == System.Text.Json.JsonValueKind.String))
        {
            log.Warn("\"show\" must be a list of CPU, RAM, GPU, Network, Disk; showing all of them.");
            return all;
        }
        var wanted = new HashSet<Metric>();
        foreach (var item in value.EnumerateArray())
        {
            var name = item.GetString()!;
            if (TryMetric(name, out var metric)) wanted.Add(metric);
            else log.Warn($"\"show\" has an unknown entry '{name}'; it is ignored.");
        }
        if (wanted.Count == 0)
        {
            if (value.GetArrayLength() > 0) log.Warn("\"show\" has no known entry; showing all of them.");
            return all;
        }
        return all.Where(wanted.Contains).ToList();
    }

    static bool TryMetric(string name, out Metric metric) => Enum.TryParse(name.Trim(), ignoreCase: true, out metric) && Enum.IsDefined(metric);

    /// <summary>True for "Rotate" (any case); "All at once", absent and anything else are false, the unknown ones logged.</summary>
    public static bool IsRotating(System.Text.Json.JsonElement settings, ITileLog log)
    {
        if (settings.ValueKind != System.Text.Json.JsonValueKind.Object || !settings.TryGetProperty("layout", out var value)) return false;
        var text = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (string.Equals(text, "Rotate", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(text, "All at once", StringComparison.OrdinalIgnoreCase))
            log.Warn("\"layout\" must be \"All at once\" or \"Rotate\"; using \"All at once\".");
        return false;
    }

    /// <summary>"rotate" from settings, clamped to 2s..1m; a bad value is logged and the default (5s) used.</summary>
    public static TimeSpan RotateInterval(System.Text.Json.JsonElement settings, ITileLog log)
    {
        if (settings.ValueKind != System.Text.Json.JsonValueKind.Object || !settings.TryGetProperty("rotate", out var value)) return DefaultRotate;
        if (value.ValueKind == System.Text.Json.JsonValueKind.String && Duration.TryParse(value.GetString(), out var parsed))
        {
            if (parsed >= MinRotate && parsed <= MaxRotate) return parsed;
            var clamped = parsed < MinRotate ? MinRotate : MaxRotate;
            log.Warn($"\"rotate\" must be from 2s to 1m; using {clamped.TotalSeconds:0}s.");
            return clamped;
        }
        log.Warn($"\"rotate\" must be a duration such as \"5s\"; using {DefaultRotate.TotalSeconds:0}s.");
        return DefaultRotate;
    }

    /// <summary>The metric on show when rotating: the selected ones in turn, each for <paramref name="every"/>.</summary>
    public static Metric Rotated(IReadOnlyList<Metric> selected, TimeSpan elapsed, TimeSpan every)
    {
        if (every <= TimeSpan.Zero) return selected[0];
        var step = (long)Math.Max(0, elapsed.Ticks / every.Ticks);
        return selected[(int)(step % selected.Count)];
    }

    // --- formatting -----------------------------------------------------------------------------------------------

    /// <summary>"512 B/s", "84 KB/s", "1.2 MB/s": one decimal below 10, none above.</summary>
    public static string Rate(double bytesPerSecond)
    {
        var v = Math.Max(0, bytesPerSecond);
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var unit = 0;
        while (unit < units.Length - 1 && v >= 1023.5) { v /= 1024; unit++; }
        return $"{Number(v, unit == 0 ? 0 : 1)} {units[unit]}";
    }

    static string Number(double v, int decimalsBelowTen)
    {
        if (decimalsBelowTen > 0 && Math.Round(v, 1) < 10) return v.ToString("0.0", CultureInfo.InvariantCulture);
        return v.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>"476 GB", "1.8 TB", "7.7 GB": the size with a unit.</summary>
    public static string Size(long bytes)
    {
        double gb = bytes / (1024.0 * 1024 * 1024);
        return gb >= 1023.5 ? $"{Number(gb / 1024, 1)} TB" : $"{Number(gb, 1)} GB";
    }

    static string Gb(long bytes) => Number(bytes / (1024.0 * 1024 * 1024), 1);

    /// <summary>"C: 71%" for the fullest drive; null without a drive.</summary>
    static string? DiskCompact(DiskReading? d) => d is null ? null : $"{d.Name} {Pct(d.Percent)}";

    public static string DiskDetail(DiskReading d) => $"{Size(d.FreeBytes)} free of {Size(d.TotalBytes)}";

    // --- levels ---------------------------------------------------------------------------------------------------

    /// <summary>Ok below 80%, Warn from 80%, Bad from 95%; Neutral when there is no percentage.</summary>
    public static TileColor Level(double? percent) => percent switch
    {
        null => TileColor.Neutral,
        >= BadPercent => TileColor.Bad,
        >= WarnPercent => TileColor.Warn,
        _ => TileColor.Ok,
    };

    /// <summary>The tile icon's color: the worst level among the shown percentages, calm (Neutral) when none is high.</summary>
    public static TileColor IconColor(SystemReading r, IReadOnlyList<Metric> shown)
    {
        var worst = TileColor.Neutral;
        foreach (var metric in shown)
        {
            var color = Level(Describe(metric, r).Percent);
            if (color is TileColor.Bad) return color;
            if (color is TileColor.Warn) worst = color;
        }
        return worst;
    }

    // --- text -----------------------------------------------------------------------------------------------------

    public static MetricText Describe(Metric metric, SystemReading r)
    {
        switch (metric)
        {
            case Metric.Cpu:
                return new(metric, "CPU", Pct(r.Cpu), r.LogicalCores > 0 ? $"{r.LogicalCores} logical cores" : null, r.Cpu, $"CPU {Pct(r.Cpu)}");
            case Metric.Ram:
                return new(metric, "RAM", Pct(r.Ram.Percent), $"{Gb(r.Ram.UsedBytes)} of {Size(r.Ram.TotalBytes)}", r.Ram.Percent, $"RAM {Pct(r.Ram.Percent)}");
            case Metric.Gpu:
                var gpu = r.Gpu is { } g ? Pct(g) : "n/a";
                return new(metric, "GPU", gpu, "3D engines", r.Gpu, $"GPU {gpu}");
            case Metric.Network:
                var down = "↓" + Rate(r.DownBytesPerSecond);
                return new(metric, "Network", down, "↑" + Rate(r.UpBytesPerSecond), null, down);
            default:
                var disk = r.FullestDisk;
                if (disk is null) return new(metric, "Disk", "n/a", null, null, "Disk n/a");
                return new(metric, "Disk", $"{disk.Name} {Pct(disk.Percent)}", DiskDetail(disk), disk.Percent, DiskCompact(disk)!);
        }
    }

    /// <summary>
    /// The compact items split over two lines: the first half on the title, the rest on the subtitle. With an odd count
    /// the subtitle gets the extra one, as it is the line with less else on it.
    /// </summary>
    public static (string Title, string? Subtitle) Split(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return ("System", null);
        if (items.Count == 1) return (items[0], null);
        var first = items.Count / 2;
        return (string.Join(" · ", items.Take(first)), string.Join(" · ", items.Skip(first)));
    }

    // --- tile -----------------------------------------------------------------------------------------------------

    /// <summary>The tile for one reading. <paramref name="rotating"/> shows <paramref name="current"/> alone with its chart.</summary>
    public static TileState Tile(SystemReading r, SystemHistory history, IReadOnlyList<Metric> shown, bool rotating, Metric current)
    {
        var icon = new TileIcon("sync", IconColor(r, shown));
        var tooltip = string.Join("\n", shown.Select(m => Line(Describe(m, r))));

        if (shown.Count == 1 || rotating)
        {
            var metric = shown.Count == 1 ? shown[0] : current;
            var text = Describe(metric, r);
            var spark = history.Get(metric);
            return new TileState
            {
                Title = $"{text.Name} {text.Value}",
                Subtitle = text.Detail,
                Icon = icon,
                Progress = text.Percent is { } p ? Math.Clamp(p / 100, 0, 1) : null,
                Sparkline = spark.Count > 1 ? spark : null,
                Tooltip = tooltip,
            };
        }

        var (title, subtitle) = Split(shown.Select(m => Describe(m, r).Compact).ToList());
        return new TileState { Title = title, Subtitle = subtitle, Icon = icon, Tooltip = tooltip };
    }

    static string Line(MetricText t) => t.Detail is null ? $"{t.Name}: {t.Value}" : $"{t.Name}: {t.Value} ({t.Detail})";

    // --- popup ----------------------------------------------------------------------------------------------------

    /// <summary>Every metric, not just the shown ones, and one row per disk. Before the first reading it is empty.</summary>
    public static FlyoutState Flyout(SystemReading? r, DateTime now)
    {
        FlyoutAction[] actions = [new(TaskManagerAction, "Task Manager")];
        if (r is null) return new FlyoutState("System", "Reading…", [], actions);

        TileIcon Dot(double? percent) => new("info", Level(percent));
        var rows = new List<FlyoutRow>();
        foreach (var metric in new[] { Metric.Cpu, Metric.Ram, Metric.Gpu })
        {
            var t = Describe(metric, r);
            rows.Add(new FlyoutRow(t.Name, metric == Metric.Ram ? $"{t.Value} · {t.Detail}" : t.Value, Dot(t.Percent)));
        }
        var net = Describe(Metric.Network, r);
        rows.Add(new FlyoutRow("Network", $"{net.Value} {net.Detail}", Dot(null)));
        foreach (var disk in r.Disks)
            rows.Add(new FlyoutRow(disk.Name, $"{DiskDetail(disk)} ({Pct(disk.Percent)})", Dot(disk.Percent)));
        return new FlyoutState("System", $"Updated {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}", rows, actions);
    }
}
