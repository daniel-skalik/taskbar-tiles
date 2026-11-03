# Taskbar Tiles

Small live tiles on the Windows taskbar, next to your app icons: what's playing, your Claude usage, CPU and memory, device batteries, a ping, the clock, or a tiny pixel farm that follows the real sunrise. Hover a tile for details, click it to act, and add your own tiles with a script or a .NET plugin.

<img src="docs/images/tile-media.png" alt="Media tile: the song playing in Spotify with playback buttons" height="40">
<img src="docs/images/tile-claude-usage.png" alt="Claude usage tile: 5-hour and weekly limits" height="40">
<img src="docs/images/tile-cozy-farm.png" alt="Cozy farm tile: a pixel farm under the real sky" height="40">
<img src="docs/images/tile-system.png" alt="System monitor tile: CPU load with a chart" height="40">
<img src="docs/images/tile-battery.png" alt="Device batteries tile: mouse battery level" height="40">

Made for Windows 11 (64-bit).

## Install

1. Download `TaskbarTiles-<version>-win-x64.zip` from the [latest release](https://github.com/daniel-skalik/taskbar-tiles/releases/latest).
2. Unzip it anywhere.
3. Double-click `install.cmd`.

That's it: no .NET to install first. The app is copied to `%LOCALAPPDATA%\TaskbarTiles\app`, starts, and starts again when you sign in (a `TaskbarTiles` shortcut in your Startup folder). Nothing needs admin rights.

The app is not code-signed, so Windows may warn before running `install.cmd` or the app the first time ("Windows protected your PC"). Choose **More info**, then **Run anyway**. If you'd rather not, build it yourself from source (below).

## Use

- **Add a tile:** the **+** button at the end of your tiles. Tiles are grouped in folders, for example **Fun › Games**.
- **Details:** click a tile for its popup, with actions and a ⚙ for its settings (also right-click › **Settings…**).

  <img src="docs/images/popup.png" alt="The Claude usage tile's popup: 5-hour and weekly limits with reset times" width="320">

- **Move or remove tiles:** right-click a tile › **Edit layout**, then drag tiles or click their ×. Right-click › **Create stack…** puts several tiles in one slot.
- **Exit:** right-click a tile › **More** › **Exit Taskbar Tiles**.

Bundled tiles: Media (Spotify by default), Claude usage, Claude sessions, System monitor, Device batteries, Clipboard peek, Clock and uptime, Ping a host, and the pixel scenes Aquarium, Cozy farm, Snake and Racers.

Your layout and settings live in `%LOCALAPPDATA%\TaskbarTiles\config.json`. You rarely need to touch it, but it's all described in [docs/configuration.md](docs/configuration.md). To write your own tile with a PowerShell (or any) script, see [docs/script-connectors.md](docs/script-connectors.md).

## Update

Download the new release and run its `install.cmd`. It replaces the app and the bundled tiles. Your config, logs and your own tiles are kept.

## Uninstall

Run `uninstall.cmd` from the release zip (or the repository). It stops the app, removes it and its Startup shortcut, and keeps your config, logs and tiles so a later install picks them up. To remove those too:

```bat
uninstall.cmd -RemoveSettings
```

## Build from source

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and Git.

```bat
git clone https://github.com/daniel-skalik/taskbar-tiles.git
cd taskbar-tiles
install.cmd
```

In the repository `install.cmd` builds the app first, then installs it the same way. Run it again after `git pull` to update. Tests: `dotnet test`.

To make a release zip yourself: `powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Version 1.0.0` (written to `artifacts\package`). Pushing a tag such as `v1.0.0` makes GitHub build it and publish the release.
