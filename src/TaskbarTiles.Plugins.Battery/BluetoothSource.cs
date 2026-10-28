using Windows.Devices.Enumeration;

namespace TaskbarTiles.Plugins.Battery;

/// <summary>
/// The battery Windows keeps for Bluetooth devices (DEVPKEY_Bluetooth_Battery, as in Settings › Bluetooth &amp; devices).
/// It sits on one of the device's nodes (for headphones the Hands-Free service); name and type come from the device's
/// container.
/// </summary>
public sealed class BluetoothSource : IBatterySource
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ContainerKey = "System.Devices.ContainerId";
    const string Query = "System.Devices.Present:=System.StructuredQueryType.Boolean#True AND System.Devices.DeviceInstanceId:~<\"BTH\"";

    public string Name => "Bluetooth";

    public async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var nodes = await DeviceInformation.FindAllAsync(Query, [BatteryKey, ContainerKey], DeviceInformationKind.Device).AsTask(ct);
        var byContainer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!node.Properties.TryGetValue(BatteryKey, out var value) || value is not byte percent) continue;
            if (!node.Properties.TryGetValue(ContainerKey, out var id) || id?.ToString() is not { Length: > 0 } container) continue;
            byContainer[container] = Math.Max(byContainer.GetValueOrDefault(container), percent);
        }

        var result = new List<DeviceBattery>();
        foreach (var (container, percent) in byContainer)
        {
            DeviceInformation info;
            try
            {
                info = await DeviceInformation.CreateFromIdAsync(container,
                    ["System.Devices.CategoryIds", "System.Devices.Connected"], DeviceInformationKind.DeviceContainer).AsTask(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; } // removed since the first query
            if (!info.Properties.TryGetValue("System.Devices.Connected", out var connected) || connected is not true) continue;
            var categories = info.Properties.TryGetValue("System.Devices.CategoryIds", out var c) ? c as string[] ?? [] : [];
            result.Add(new DeviceBattery($"bluetooth:{container}", info.Name, TypeFrom(categories), Math.Min(percent, 100)));
        }
        return result;
    }

    /// <summary>Device type from a container's category ids, e.g. "Communication.Headset.Bluetooth", "Input.Mouse".</summary>
    public static DeviceType TypeFrom(IEnumerable<string> categoryIds)
    {
        var ids = categoryIds.ToList();
        bool Has(string part) => ids.Any(i => i.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (Has("Input.Mouse")) return DeviceType.Mouse;
        if (Has("Input.Keyboard")) return DeviceType.Keyboard;
        if (Has("Headset") || Has("Headphone") || Has("Audio")) return DeviceType.Headphones;
        return DeviceType.Other;
    }
}
