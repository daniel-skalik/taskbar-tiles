namespace TaskbarTiles.Plugins.Battery;

public enum DeviceType { Other, Mouse, Keyboard, Headphones }

/// <summary>What a device reports when it has no percentage (some Logitech devices).</summary>
public enum BatteryLevel { Critical, Low, Good, Full }

/// <summary>
/// One device's battery. Percent or Level is set (Percent wins when both are). Asleep: the device stopped answering and
/// this is its last reading.
/// </summary>
public sealed record DeviceBattery(
    string Id,
    string Name,
    DeviceType Type,
    int? Percent,
    BatteryLevel? Level = null,
    bool Charging = false,
    bool Asleep = false)
{
    /// <summary>Percent, or a stand-in for a level, for sorting and colors; null when the device reported neither.</summary>
    public int? Effective => Percent ?? Level switch
    {
        BatteryLevel.Critical => 5,
        BatteryLevel.Low => 15,
        BatteryLevel.Good => 50,
        BatteryLevel.Full => 100,
        _ => null,
    };
}

/// <summary>A place batteries are read from (Bluetooth, Logitech receivers). May throw; the connector logs it.</summary>
public interface IBatterySource
{
    string Name { get; }
    Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct);
}
