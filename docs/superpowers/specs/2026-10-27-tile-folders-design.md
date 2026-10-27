# Tile folders — design

Date: 2026-10-27. Builds on [2026-10-22-taskbar-tiles-design.md](2026-10-22-taskbar-tiles-design.md) (Milestones 1–3 merged).

## Why

Today a tile's whole definition (connector, hooks, script paths) lives inline in `config.json`, and its scripts live in a shared `scripts\` folder. Two problems:

- A tile cannot be shared: its definition and its files are in different places.
- Removing a tile with × deletes its definition, so the user loses how it was built.

## Goal

Each **tile kind** is one folder holding everything it needs. `config.json` keeps only tile instances (which kind, plus setting overrides) and the layout. Sharing a tile means zipping its folder.

Out of scope: a real installer (setup exe / MSIX), migrating old configs, the taskbar's right-side free zone, unloading plugin DLLs at run time.

## Folder layout

```
%LOCALAPPDATA%\TaskbarTiles\
  config.json          tile instances + layout
  logs\
  tiles\
    uptime\  tile.json, uptime.ps1                              (bundled: replaced on install)
    ping\    tile.json, ping.ps1                                (bundled: replaced on install)
    media\   tile.json, TaskbarTiles.Plugins.Media.dll + deps   (bundled: replaced on install)
    meeting\ tile.json, urgent.ps1                              (the user's: never touched)
```

- The folder name is the kind's id (`"kind": "ping"`).
- The script connector stays built into the app (`app\plugins\TaskbarTiles.Plugins.Script\`); script kinds only need it to run their scripts. Every other connector DLL lives in a kind folder. `app\plugins` keeps only the script connector.
- The user plugin root `%LOCALAPPDATA%\TaskbarTiles\plugins` is no longer scanned.
- `scripts\` (and its first-run copy, `SampleScripts`) goes away. The sample scripts become the bundled kinds `uptime` and `ping`. An existing `%LOCALAPPDATA%\TaskbarTiles\scripts` folder is left alone, never deleted.
- In the repo, bundled kinds live in `tiles\<kind>\` (scripts and `tile.json`); the media kind's `tile.json` lives there too, and the build copies the media plugin's output into `tiles\media\` of the published app.

## tile.json

```jsonc
{
  "name": "Ping a host",                    // shown in the + / stack picker
  "description": "Pings a host every 15 s", // subtitle in the picker
  "connector": "script",                    // "script", or a connector id declared by a DLL in this folder
  "settings": {                             // defaults for every instance of this kind
    "host": "1.1.1.1",
    "hooks": {
      "interval":    { "every": "15s", "run": "ping.ps1" },
      "flyout":      { "run": "ping.ps1" },
      "action:ping": { "run": "ping.ps1" }
    }
  }
}
```

- `name` and `connector` are required; `description` and `settings` are optional (`settings` defaults to `{}`). Unknown keys are ignored.
- Script hook `run` paths are relative to the kind folder, and scripts run with the kind folder as their working directory. (Today both are the data folder.)
- A DLL in a kind folder is loaded only for that kind, in its own `PluginLoadContext`, the way plugins load today. Its `[TileConnector]` ids are private to that folder: two kinds may each ship a connector with the same id without clashing. If `connector` is not `"script"`, it must match a connector id declared by a DLL in the same folder.

## config.json

```jsonc
{
  "tiles": {
    "ping-dns":  { "kind": "ping" },
    "ping-home": { "kind": "ping", "settings": { "host": "192.168.1.1" } },
    "media":     { "kind": "media" }
  },
  "displays": { "primary": { "slots": [ "ping-dns", "media" ] } }
}
```

- A tile instance is `{ "kind": "<folder>", "settings": { … } }`; `settings` is optional.
- **Effective settings** = the kind's `settings` with the instance's top-level keys replacing the kind's keys of the same name (shallow, key by key). This is what connectors get, and what the script connector passes as `TILE_SETTINGS_JSON`.
- `hooks` may not appear in instance settings: behaviour belongs to the kind folder.
- The old inline form `{ "connector": … }` is a config error (no migration).
- `displays` and everything about layout, stacks and edit mode are unchanged.

## Loading and watching

- At startup the host scans `tiles\*\tile.json` into a **kind table**: id (folder name), name, description, connector, default settings, folder path. Kind DLLs load when the table is built.
- `tiles\` is watched like `config.json`:
  - a saved `tile.json` or script change restarts only the tiles of that kind, within a second;
  - a new kind folder appears in the picker;
  - a changed or added DLL needs an app restart (.NET cannot unload it safely); a notice says so.
- `config.json` validation checks each instance's `kind` against the kind table.

## Gallery, menu, removing

- The picker (`+`, **Create stack…**, **Add to stack…**) lists, as today, first the existing instances this display does not show, then one entry per kind: title = `name`, subtitle = `description`. Picking a kind writes `"<kind>": { "kind": "<kind>" }`, or `<kind>-2`, `<kind>-3`, … when the id is taken (`TileGallery.UniqueId`). `ScriptTemplates` goes away.
- **Configure connector…** in the tile menu becomes **Show tile folder**: it selects that tile's kind folder in Explorer. **Show config file** stays.
- × removes the instance from `config.json` under the same rules as today; the kind folder is never deleted.

## Install

- New `install.cmd` in the repo root, so install is a double-click:

  ```bat
  @echo off
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
  if errorlevel 1 pause
  ```

  On success the window closes and the app starts; on failure it stays open so the error can be read.
- `install.ps1`, after swapping in `app\`, replaces `tiles\uptime`, `tiles\ping` and `tiles\media` with the bundled copies (delete the folder, copy the new one). Other folders in `tiles\` are never touched. It runs while the app is stopped, as the swap already does.
- New parameter `-TilesDir` (default `%LOCALAPPDATA%\TaskbarTiles\tiles`), so the kind refresh can be tested against a temp folder. Agents never run it against the real AppData.
- Docs: install by double-clicking `install.cmd`; to change a bundled kind, copy its folder under a new name first, because install replaces it.

## Errors

The config notice and the tile error popup already exist; the last good config stays active on a config error.

| Problem | Shown as |
|---|---|
| Instance uses `"connector"` | Config notice: `Tile 'x' uses "connector"; tiles now name a kind: "kind": "<folder>"` |
| Instance `kind` has no folder in `tiles\` | Config notice naming the tile and the missing `tiles\<kind>` folder |
| Instance `settings` contains `hooks` | Config notice: `hooks belong in tiles\<kind>\tile.json` |
| `tile.json` missing, not valid JSON, or missing `name`/`connector` | Kind still in the table as broken; logged; its tiles show a tile error naming the file (and line for JSON errors). Other kinds work. |
| `connector` not `"script"` and not declared by a DLL in the folder | Tile error on that kind's tiles |
| A kind DLL fails to load | Tile error on that kind's tiles |
| Two DLLs in one folder declare the same connector id | Tile error on that kind's tiles |

A broken kind never rejects the config; only mistakes in `config.json` do. A folder in `tiles\` without any `tile.json` is ignored (not a kind) except when an instance names it, which is the "missing folder" notice with a hint that `tile.json` is missing.

## Testing

- Unit tests: kind table scan (good, broken, missing `tile.json`); effective settings merge; config errors above; script `run` path and working directory resolved against the kind folder; gallery lists kinds by name and picks free ids; × leaves the kind folder; two kind folders with the same connector id do not clash.
- Watch test: a `tile.json` edit restarts only that kind's tiles.
- Each bundled `tiles\*\tile.json` in the repo parses and names a connector that exists.
- `install.ps1 -TilesDir <temp>`: bundled kinds replaced, other folders untouched.
- The user's on-screen check, then merge on request.
