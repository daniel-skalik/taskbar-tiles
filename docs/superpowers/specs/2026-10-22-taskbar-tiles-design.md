# Taskbar Tiles — Design

Date: 2026-10-22
Status: approved in brainstorming, pending spec review

## Goal

A generic Windows 11 taskbar widget host. It places a row of small tiles in the free space of the taskbar. Each tile is driven by a connector: a C# plugin DLL, or a script wired up through hooks. It replaces the single-purpose Spotify widget (`SpotifyTaskbar`), whose media code becomes one plugin.

UX reference: the clickable mockup `mockup/taskbar-tiles.html` (published as https://claude.ai/artifact/F1dRW5AaTu5unDfwyUo4j4). Tiles use the same look as the Spotify widget: rounded 6 px, faint fill, 1 px light outline, 4 px vertical inset inside the taskbar.

## Decisions

| Topic | Decision |
|---|---|
| Layout model | Side-by-side tiles plus rotating stacks, from v1 |
| Connector model | C# plugin DLLs are the only abstraction; hook-style scripts ship as a built-in plugin |
| Tile UI | Declarative `TileState` / `FlyoutState` rendered by the host; contract leaves room for custom WPF controls later |
| Configuration | One `config.json`, hot-reloaded; edit mode writes layout; "Configure connector…" opens the file in the default editor |
| Plugin reload | Hot reload; if the old plugin will not unload, the host restarts itself automatically |
| Spotify widget | Ported into a built-in `media` plugin; `SpotifyTaskbar.exe` retired after milestone 1 |
| Displays | Per-display layouts; a tile can appear on several displays and is backed by one shared runtime |
| Space pressure | Reflow on every free-zone change: full → compact → overflow (`»` tile) |

## Architecture

Solution `TaskbarTiles`:

| Project | Role | References |
|---|---|---|
| `TaskbarTiles.Contracts` | Plugin contract: `ITileConnector`, `ITileContext`, `TileState`, `FlyoutState`, `TileConnectorAttribute` | none |
| `TaskbarTiles.Host` | WPF exe: taskbar tracking, layout, rendering, popups, edit mode, config, plugin loading | Contracts |
| `TaskbarTiles.Plugins.Media` | Media session tile (port of SpotifyTaskbar) | Contracts |
| `TaskbarTiles.Plugins.Script` | Hook-driven script runner | Contracts |
| `TaskbarTiles.Tests` | Unit and integration tests | all |

Target: `net9.0-windows10.0.19041.0`, per-monitor-v2 DPI aware.

### Host units

- **`TaskbarTracker`** tracks every taskbar window (`Shell_TrayWnd` and each `Shell_SecondaryTrayWnd`) and publishes, per display, a `TaskbarInfo { DisplayId, TaskbarRect, FreeZone, Hidden, IsLightTheme }`.
  - Event sources, no polling: `SetWinEventHook` on the Explorer process (`EVENT_OBJECT_REORDER`…`EVENT_OBJECT_LOCATIONCHANGE`, filtered to taskbar, Start, icon-area and tray windows), `EVENT_SYSTEM_FOREGROUND` globally, appbar `ABN_FULLSCREENAPP`, `TaskbarCreated`, `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_SETTINGCHANGE`.
  - Free zone:
    - Icons centered (Start button is not at the taskbar's left edge): from taskbar left + margin to Start left − margin.
    - Icons left-aligned: from icon-area right + margin (`ReBarWindow32` on the main taskbar, `WorkerW` on secondary ones) to the tray left − margin (`TrayNotifyWnd`; secondary taskbars have no tray window, so use the taskbar right edge minus a clock reserve, default 120 DIP).
    - Alignment comes from geometry. The `TaskbarAl` registry value is not used, because Settings does not reliably write it.
  - `Hidden` is true when the foreground window covers that display's monitor.
  - Lives behind `ITaskbarSource` so the UI can run against a fake source.
- **`PluginLoader`** shadow-copies each plugin folder under `plugins\` to a temp directory and loads it into its own collectible `AssemblyLoadContext`. `Contracts` is shared from the host's default context. It discovers connector types by `[TileConnector("id")]`, watches `plugins\` with a debounced `FileSystemWatcher`, and on change unloads and reloads. If a `WeakReference` to the old context is still alive after 10 GC cycles, it restarts the host.
- **`ConfigStore`** loads, validates, saves and watches `config.json`. On invalid JSON it keeps the last good config and reports line and column. It never writes over a file that fails to parse.
- **`TileRuntime`** (one per tile id that is placed on at least one display) owns the connector instance, holds the latest `TileState`, coalesces updates onto the UI dispatcher, routes actions, and applies the error and restart policy.
- **`TileBar`** (one WPF window per display) renders that display's slots from runtime states. It handles stacks, compact/overflow layout, popups, the context menu and edit mode, and positions itself inside the display's free zone as a topmost tool window with `WS_EX_NOACTIVATE`, using the approach proven in SpotifyTaskbar.
- **`LayoutEngine`** is a pure function: `(freeWidth, slots with full/compact widths and pin flags) → (full, compact, overflow)`.

### Plugin contract

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class TileConnectorAttribute(string id) : Attribute { public string Id { get; } = id; }

public interface ITileConnector : IAsyncDisposable
{
    Task StartAsync(ITileContext ctx, CancellationToken ct);
    Task OnActionAsync(string actionId, CancellationToken ct);   // "click", inline button ids, popup action ids
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);     // null = clicking does not open a popup
}

public interface ITileContext
{
    string TileId { get; }
    JsonElement Settings { get; }
    void Update(TileState state);       // thread-safe; latest wins
    event EventHandler Resumed;         // system resumed from sleep
    ILogger Log { get; }
}

public sealed record TileState
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public TileIcon? Icon { get; init; }              // glyph name + semantic color (ok/warn/bad/accent/neutral)
    public byte[]? Image { get; init; }               // e.g. album art; PNG/JPEG
    public IReadOnlyList<TileButton> Buttons { get; init; } = [];  // inline icon buttons
    public double? Progress { get; init; }            // 0..1, thin bar along the bottom
    public IReadOnlyList<double>? Sparkline { get; init; }
    public bool Urgent { get; init; }                 // pulsing outline; urgent stack item takes over
    public string? Tooltip { get; init; }
}

public sealed record FlyoutState(string Title, string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows, IReadOnlyList<FlyoutAction> Actions, byte[]? Image = null);
```

Custom WPF controls are out of scope for v1. A later optional interface (`ICustomTileView`) can add them without changing `ITileConnector`.

### Built-in plugins

- **`media`**: port of SpotifyTaskbar. It reads `GlobalSystemMediaTransportControlsSessionManager`, filters sessions by `settings.app` (default `"Spotify"`), shows art, title, artist and previous/play-pause/next buttons, and has a popup with next-up info. Click on art = launch the app.
- **`script`**: runs hooks from `settings.hooks`:

  | Hook | Behavior |
  |---|---|
  | `interval` `{ every, run }` | Run on start and every period; stdout JSON → `TileState`. Skip a run if the previous one is still going. |
  | `watch` `{ run }` | Long-running; each stdout line is a `TileState` update; restarted with backoff when it exits. |
  | `click` `{ run }` | Run on plain click; output (if any) updates the tile. |
  | `flyout` `{ run }` | Run when the popup opens; stdout JSON → `FlyoutState`. |
  | `action:<id>` `{ run }` | Run when a button with that id is pressed. |
  | `startup`, `resume` `{ run }` | Run once at start / after sleep. |

  - `run` is a path relative to the config folder, or a command line. `.ps1` runs via `pwsh` (falling back to `powershell.exe`), `.py` via `python`, anything else via the shell.
  - Environment: `TILE_ID`, `TILE_HOOK`, `TILE_ACTION`, `TILE_SETTINGS_JSON`.
  - The JSON uses camelCase `TileState` / `FlyoutState` fields, with icons by glyph name.

## Configuration

Location: `%LOCALAPPDATA%\TaskbarTiles\`, containing `config.json`, `scripts\`, `plugins\`, `state-cache.json` and `logs\`.

```jsonc
{
  "tiles": {
    "spotify": { "connector": "media",  "settings": { "app": "Spotify" } },
    "ci":      { "connector": "script", "settings": { "hooks": {
                   "interval": { "every": "30s", "run": "scripts/ci-status.ps1" },
                   "flyout":   { "run": "scripts/ci-builds.ps1" },
                   "action:rerun": { "run": "scripts/ci-rerun.ps1" } } } },
    "weather": { "connector": "script", "settings": { "hooks": { "interval": { "every": "10m", "run": "scripts/weather.py" } } } }
  },
  "displays": {
    "primary":           { "slots": [ { "tile": "spotify", "pin": true }, "ci", { "stack": ["weather"], "rotate": "5s" } ] },
    "DEL41A8#5&2b1c...": { "slots": [ "ci" ] },
    "*":                 { "slots": [] }
  }
}
```

- A slot is a tile id, `{ "tile", "pin" }`, or `{ "stack": [ids], "rotate" }`.
- Display keys:
  - `primary`
  - a monitor device id (from `EnumDisplayDevices`, written by edit mode)
  - `*`, the fallback for displays without an entry
- Resolution order: exact device id, then `primary` if the display is primary, then `*`.
- Edit mode writes this file. Hand edits are picked up by the watcher.

## Data flow

```
connector ──ctx.Update(TileState)──► TileRuntime (latest, coalesced) ──► dispatcher ──► every TileView bound to it
TileView click/button ──► TileRuntime.OnActionAsync(id)      (thread pool, 10 s timeout)
popup open ──► TileRuntime.GetFlyoutAsync()                  (spinner until it returns, 10 s timeout)
TaskbarTracker change ──► TileBar(display).Reflow() ──► LayoutEngine ──► reposition window + re-render
```

## Layout and overflow

- Each tile has a full width (by content type; fixed per tile kind in v1: media 268, default 132 DIP) and a compact width (icon only, 40 DIP).
- On every free-zone change or slot change, `LayoutEngine` runs:
  1. If everything fits at full width, done.
  2. Otherwise compact unpinned tiles from the right end until it fits.
  3. Still too wide: move tiles from the right end into a `»` overflow tile (40 DIP) until it fits. Pinned tiles overflow last.
- The overflow popup lists overflowed tiles at full size, still interactive: buttons, popups, context menu.
- Stacks count as one tile. A stack rotates every `rotate` (default 5 s), pauses on hover, cycles on mouse wheel, and shows dots for its position. An item with `Urgent = true` takes over the stack until clicked.

## Interaction (from the mockup)

- Click tile: open its popup (`GetFlyoutAsync`), or send `"click"` if it returns null. Inline buttons send their own action id.
- Right-click: Configure connector…, Refresh now, Show connector log, Move left/right, Add to stack / Pause rotation, Edit layout, Remove tile.
- Edit layout: dashed tiles, drag to reorder, drop onto the middle of a stack to add to it, × to remove, `+` opens the connector gallery (tile kinds from loaded plugins plus script templates), Done or Esc to leave. It works on each display's bar.
- Hover: tooltip from `TileState.Tooltip`.
- Theme: follows the taskbar's light/dark setting (`SystemUsesLightTheme`), re-read on `WM_SETTINGCHANGE` `ImmersiveColorSet`.

## Error handling

| Failure | Behavior |
|---|---|
| Connector throws | Tile shows the error look (muted warning icon, "Error", message in tooltip). Popup shows the exception with Restart tile and Open log. |
| Connector keeps failing | Restart with backoff: 2 s, 10 s, 1 min, then every 5 min. Reset after a successful update. |
| Action or popup exceeds 10 s | Cancelled; tile shows "Not responding" for 3 s. |
| Script exits non-zero or prints invalid JSON | Error look; stderr in the popup. |
| `watch` script exits | Restart with the same backoff. |
| Invalid `config.json` | Keep the last good config; toast with line and column. |
| Unknown connector id or missing plugin | That tile shows "Unknown connector: id"; the rest works. |
| Plugin will not unload on reload | Host restarts itself. Config and state cache make this lossless. |
| Fatal host crash | Watchdog (`TaskbarTiles.exe --watchdog`, launched by the Startup shortcut) restarts the host. After 3 crashes in 60 s it stops and shows a notification naming the last loaded plugin. |

`ctx.Update` never calls into WPF directly; plugins never run on the UI thread.

## Lifecycle

1. Startup: load config, load plugins, create runtimes for tiles placed on any display, render tiles from `state-cache.json`, start runtimes in parallel.
2. Hot reload of a plugin: dispose its runtimes, unload, verify unload (else restart host), load the new version, start the runtimes again.
3. Sleep/resume: `Resumed` event for plugins, `resume` hook for scripts.
4. Shutdown: `DisposeAsync` on every runtime, 3 s budget, write state cache, exit.
5. Autostart: shortcut in the user's Startup folder (it shows up in Task Manager, unlike the `Run` registry key in the earlier attempt).

## Testing

- Unit (xUnit):
  - `LayoutEngine` (fits, compact, overflow, pinned, stacks, zero width)
  - free-zone calculation from window rects (centered, left-aligned, secondary without tray, Start missing)
  - config parsing, validation and display resolution
  - script output → state mapping
  - backoff schedule
  - stack rotation and urgent takeover
- Integration:
  - `PluginLoader` with test DLLs: load, reload, unload, and a deliberately leaky plugin that triggers the restart path
  - `ScriptConnector` with real PowerShell scripts: interval, action, watch, timeout, non-zero exit
- Manual checklist:
  - alignment switch
  - opening and closing apps (icon count)
  - monitor plug/unplug
  - Explorer restart
  - fullscreen per monitor
  - DPI change
  - light/dark taskbar
  - Startup shortcut visible in Task Manager

## Milestones

1. **Host + media plugin.** Contracts, multi-display `TaskbarTracker`, `TileBar` rendering, `ConfigStore` (load and watch), theme following, `media` plugin at parity with SpotifyTaskbar. SpotifyTaskbar retired.
2. **Script plugin.** All hooks, popups, context menu, error states and backoff.
3. **Layout.** Stacks, compact and overflow, edit mode per display writing `config.json`.
4. **Robustness.** Plugin hot reload, auto restart on leaked unload, watchdog, state cache.

## Out of scope for v1

Generated settings dialog, custom WPF tile/popup controls, HTTP push endpoint, plugin distribution or marketplace, per-tile width configuration.
