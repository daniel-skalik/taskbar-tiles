using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Battery;

/// <summary>Turns readings into the tile and its popup. Pure.</summary>
public static class BatteryView
{
    public const string BluetoothSettingsAction = "bluetooth";

    /// <summary>Lowest battery first; devices that reported no level last; then by name.</summary>
    public static IReadOnlyList<DeviceBattery> Sorted(IEnumerable<DeviceBattery> devices) =>
        devices.OrderBy(d => d.Effective ?? int.MaxValue).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static TileState Tile(IReadOnlyList<DeviceBattery> devices)
    {
        var sorted = Sorted(devices);
        if (sorted.Count == 0)
            return new TileState
            {
                Title = "Battery",
                Subtitle = "No devices",
                Icon = new TileIcon("battery-0"),
                Tooltip = "No connected device reports its battery. Click for details.",
            };

        var lowest = sorted[0];
        return new TileState
        {
            Title = Amount(lowest),
            Subtitle = lowest.Name,
            Icon = new TileIcon(Glyph(lowest), Color(lowest) is TileColor.Ok ? TileColor.Neutral : Color(lowest)),
            Urgent = lowest.Effective < 5 && !lowest.Charging,
            Tooltip = string.Join("\n", sorted.Select(d => $"{d.Name}: {Value(d)}")),
        };
    }

    public static FlyoutState Flyout(IReadOnlyList<DeviceBattery> devices)
    {
        var sorted = Sorted(devices);
        return new FlyoutState(
            "Batteries",
            sorted.Count switch { 0 => "No devices", 1 => "1 device", var n => $"{n} devices" },
            sorted.Select(d => new FlyoutRow(d.Name, Value(d), new TileIcon(Glyph(d), Color(d)))).ToList(),
            [new FlyoutAction(BluetoothSettingsAction, "Bluetooth settings")]);
    }

    /// <summary>battery-N or battery-charging-N, N = tenths of full, rounded.</summary>
    public static string Glyph(DeviceBattery d)
    {
        var tenths = (int)Math.Round(Math.Clamp(d.Effective ?? 0, 0, 100) / 10.0, MidpointRounding.AwayFromZero);
        return d.Charging ? $"battery-charging-{tenths}" : $"battery-{tenths}";
    }

    public static TileColor Color(DeviceBattery d) => d.Effective switch
    {
        null => TileColor.Neutral,
        < 10 => TileColor.Bad,
        < 20 => TileColor.Warn,
        _ => TileColor.Ok,
    };

    static string Amount(DeviceBattery d) =>
        d.Percent is { } p ? $"{p} %" : d.Level is { } level ? level.ToString() : "Unknown";

    /// <summary>"45 %", "45 %, charging", "low", "45 %, asleep".</summary>
    public static string Value(DeviceBattery d)
    {
        var parts = new List<string> { d.Percent is { } p ? $"{p} %" : d.Level?.ToString().ToLowerInvariant() ?? "unknown" };
        if (d.Charging) parts.Add("charging");
        if (d.Asleep) parts.Add("asleep");
        return string.Join(", ", parts);
    }
}
