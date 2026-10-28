# Device battery tile — implementation plan

Spec: [2026-10-28-battery-tile-design.md](../specs/2026-10-28-battery-tile-design.md). Branch `feat/battery-tile`. Always pass `--disable-build-servers` to dotnet.

## Task 1: Glyphs

- `src/TaskbarTiles.Host/UI/Theme.cs`: add `battery-0`..`battery-10`, `battery-charging-0`..`battery-charging-10`, `mouse`, `keyboard`, `headphones` (code points in the spec).
- `src/TaskbarTiles.Contracts/TileState.cs` glyph list comment, `docs/script-connectors.md` glyph list.
- Test: each new name maps to its code point; an unknown name still maps to `info`.

## Task 2: Plugin project and model

- `src/TaskbarTiles.Plugins.Battery/TaskbarTiles.Plugins.Battery.csproj` (copy of the media csproj, `AllowUnsafeBlocks` not needed), added to `TaskbarTiles.sln`.
- `DeviceBattery.cs`: `DeviceType`, `BatteryLevel`, `DeviceBattery`, `IBatterySource`.
- Host csproj and test csproj: `PluginProject … Kind="battery"`; the test project also references it with `ReferenceOutputAssembly="true"` for unit tests.
- `tiles/battery/tile.json`: name `Device batteries`, description `Battery of your mouse, keyboard and headphones`, connector `battery`, settings `{ "interval": "60s" }`.

## Task 3: BatteryView (pure)

- `BatteryView.cs`: `Tile(IReadOnlyList<DeviceBattery>)`, `Flyout(...)`, `Sorted(...)`, `Glyph(...)`, `Color(...)`.
- Tests `BatteryViewTests`: lowest first; percent vs level; charging glyph; colors 9/19/20; urgent 4 % not charging, not urgent charging; empty; tooltip lines; popup rows and action.

## Task 4: HID++

- `Hidpp.cs`: `IHidppTransport` (`Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[] args, CancellationToken)`; null = error/timeout), `HidppDevice` cache (feature indices, name, type, last reading), `LogitechSource` logic over a list of transports (`DiscoverAsync`, `ReadAsync`).
- `HidTransport.cs`: real transport over a HID path (CreateFile + FileStream), `HidTransport.FindAll()` via `HidDevice.GetDeviceSelector(0xFF00, 2, 0x046D)`.
- Tests `HidppTests` with a scripted fake transport: direct device at 0xFF; receiver with devices 1 and 2; name in two chunks; unified battery percent, level-only, charging; 0x1000 fallback; device unreachable after a reading → asleep; never read → skipped.

## Task 5: Bluetooth source and connector

- `BluetoothSource.cs`: the AQS query and container lookup from the spec; `TypeFromCategories(string[])` is pure and tested.
- `BatteryConnector.cs` `[TileConnector("battery")]`: parses `interval` with `Duration`, timer loop, `refresh` and `Resumed`, `bluetooth` action opens `ms-settings:bluetooth`, merges sources, logs per-source failures, `ReportError` when all fail.

## Task 6: Bundle, install, docs

- `BundledKindsTests`: kinds list includes `battery`, its plugin id is `battery`.
- `tools/install.ps1`: `battery` in `$bundledKinds`, the comment, and the required files (`tiles\battery\TaskbarTiles.Plugins.Battery.dll`, `tiles\battery\tile.json`).
- `docs/configuration.md` kind tree and install sentence; a short "Device batteries" section.

## Task 7: Verify

- Full test run, 0 warnings.
- Run the connector logic against the real devices through a scratch console (not the installed app) and compare with the probe.
- Review the branch diff (subagent), fix findings, commit.
