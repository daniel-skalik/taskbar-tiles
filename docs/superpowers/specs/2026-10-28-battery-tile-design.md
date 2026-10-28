# Device battery tile — design

Date: 2026-10-28. Builds on [2026-10-27-tile-folders-design.md](2026-10-27-tile-folders-design.md).

## Goal

A small tile with the battery of the user's wireless devices (mouse, keyboard, headphones), lowest first, and a popup listing every device with its level and charging state. Pre-approved by the user ("all is pre approved").

## Devices and sources (probed on the user's PC)

| Device | Connection | Source | Probe result |
|---|---|---|---|
| JLab JBuds Lux ANC | Bluetooth | Windows `DEVPKEY_Bluetooth_Battery` (`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`) | 100 %, on the "Hands-Free AG" device node; container `JLab JBuds Lux ANC`, category ids `Communication.Headset.Bluetooth, Audio` |
| MX Keys S | Logi Bolt receiver `046D:C548` | HID++ 2.0, device index 1 | HID++ 4.5; Unified Battery `0x1004`: 45 %, discharging; DeviceName type 0 (keyboard) |
| MX Master 3S | same receiver | HID++ 2.0, device index 2 | 75 %; type 3 (mouse) |

Extra information beyond battery is limited to what devices report: charging state (HID++) and, for Logitech devices without a percentage, a level word. Headphone modes (ANC) need the vendor's app and are out of scope.

## Kind

Bundled kind `tiles\battery\` with a C# connector DLL, like `media`: project `src\TaskbarTiles.Plugins.Battery`, connector id `battery`, default width 132. Install replaces `tiles\battery`.

`tile.json` settings: `"interval"` (duration, default `"60s"`, `10s`..`1h`).

## Sources

`IBatterySource.ReadAsync(ct) → IReadOnlyList<DeviceBattery>`; `DeviceBattery(Id, Name, DeviceType Type, int? Percent, BatteryLevel? Level, bool Charging, bool Asleep)`; `DeviceType` = Mouse, Keyboard, Headphones, Other; `BatteryLevel` = Critical, Low, Good, Full.

### Bluetooth

`DeviceInformation.FindAllAsync` with AQS `System.Devices.Present:=System.StructuredQueryType.Boolean#True AND System.Devices.DeviceInstanceId:~<"BTH"`, kind `Device`, asking for the battery key and `System.Devices.ContainerId`. Every node with a battery value is one reading; nodes of one container are one device (highest value wins, they agree in practice). The device's name and type come from its container (`DeviceInformationKind.DeviceContainer`, `System.Devices.CategoryIds`): ids containing `Headset`, `Headphone` or `Audio` → Headphones; `Input.Mouse` → Mouse; `Input.Keyboard` → Keyboard; else Other. Only containers with `System.Devices.Connected` true are shown.

### Logitech HID++

- Transport: every HID collection with usage page `0xFF00`, usage `0x0002` (HID++ long reports, 20 bytes, report id `0x11`) of vendor `046D`, found with `HidDevice.GetDeviceSelector` + `DeviceInformation.FindAllAsync` and opened with `CreateFile` (read/write, shared) as an overlapped `FileStream`. Requests are sequential per handle: write, then read until the matching response (same device index, feature index, function and software id `0xA`) or an error, 1.5 s timeout. Errors: `[0x11, dev, 0xFF, feat, fn|sw, code]` (HID++ 2.0) or a short report `[0x10, dev, 0x8F, …]` (HID++ 1.0, e.g. device unreachable).
- Discovery: ping index `0xFF` (root feature `0x0000` function 1). An HID++ ≥ 2.0 answer means a directly connected device at `0xFF`; otherwise the collection is a receiver and indices 1..6 are pinged.
- Per device (cached per collection + index until the collection disappears): feature indices from root `getFeature`; name and type from `0x0005` DeviceName (function 0 length, function 1 chunks of 16 chars from an offset, function 2 type: 0 keyboard, 3 mouse, 4 trackpad, 5 trackball, 8 headset → Keyboard, Mouse, Mouse, Mouse, Headphones, else Other).
- Battery: `0x1004` Unified Battery: function 0 capabilities (`flags & 0x02` = percentage supported), function 1 status `[percent, level bits (1 critical, 2 low, 4 good, 8 full), charging (0 discharging, 1 charging, 2 slow charging, 3 complete, 4 error), external power]`. Percentage when supported, else the highest level bit. Charging = status 1 or 2; complete = 100 % charging false. Fallback `0x1000` Battery Level Status: function 0 `[percent, next, status]`, charging when status is 1..3. Neither feature: the device is skipped.
- A device that stops answering (asleep) keeps its last reading with `Asleep = true`; a device never read is not shown.

## Tile

Pure `BatteryView.Tile(readings)` / `BatteryView.Flyout(readings)`; devices sorted by effective level (percent, or Critical 5, Low 15, Good 50, Full 100; unknown last), then name.

- Tile, lowest device: title `45 %` (or `Low`), subtitle its name, icon `battery-N` (`battery-charging-N` when charging), N = percent rounded to tens / 10; color `bad` under 10 %, `warn` under 20 %, else `neutral`. Urgent under 5 % and not charging. Tooltip: one line per device, `MX Keys S: 45 %`.
- No devices: title `Battery`, subtitle `No devices`, icon `battery-0` neutral.
- Popup: title `Batteries`, subtitle `3 devices`, rows `{ name, "45 %" | "45 %, charging" | "low" | "45 %, asleep", dot }` with the tile's colors (`ok` otherwise), actions `bluetooth` → opens `ms-settings:bluetooth`.
- Refresh: every `interval`, on the `refresh` action, after resume. A source that throws is logged and the others still show; when every source fails the error goes to `ReportError`.

## Host changes

New glyph names in `Theme`: `battery-0`..`battery-10` (U+E850..U+E859, U+E83F), `battery-charging-0`..`battery-charging-10` (U+E85A..U+E862, U+E83E, U+EA93), `mouse` (U+E962), `keyboard` (U+E765), `headphones` (U+E7F6). Docs list them.

## Testing

- HID++: a fake transport answers by (device, feature, function); tests cover direct vs receiver discovery, multi-chunk names, Unified Battery percent / level / charging, `0x1000` fallback, unreachable device kept as asleep, error responses.
- `BatteryView`: lowest first, glyph level and charging, colors and urgent, empty, tooltip, popup rows.
- `BundledKindsTests` lists `battery`; `Theme` maps the new glyphs.
- Bluetooth and the real HID transport: manual check on the user's PC.
