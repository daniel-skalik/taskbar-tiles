# Configuring Taskbar Tiles

Your tiles and layout live in `%LOCALAPPDATA%\TaskbarTiles\config.json`; what each kind of tile does lives in its own folder under `%LOCALAPPDATA%\TaskbarTiles\tiles\`. Right-click a tile → **Show config file** shows `config.json` in Explorer, **Show tile folder** that tile's kind folder. Both are watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).

## Install or update

Double-click `install.cmd` in the repository. On success its window closes and the app starts; on failure it stays open so you can read the error. (From a terminal: `install.cmd`, or `powershell -ExecutionPolicy Bypass -File tools\install.ps1`.)

It publishes a Release build into a staging folder next to the install folder and verifies it (a failed build changes nothing), then stops the installed copy, swaps the new folder in for `%LOCALAPPDATA%\TaskbarTiles\app` (so no stale files stay), replaces the bundled tile kinds `tiles\uptime`, `tiles\ping` and `tiles\media` with the new copies, creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, your logs and every other folder in `tiles\` are never touched. To change a bundled kind, copy its folder under a new name first (for example `tiles\ping` to `tiles\my-ping`): install replaces the bundled ones. A `scripts` folder left over from an older version is not used any more and is left alone. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.

## Tiles and displays

```jsonc
{
  "tiles": {
    "spotify":   { "kind": "media" },
    "uptime":    { "kind": "uptime" },
    "ping":      { "kind": "ping" },
    "ping-home": { "kind": "ping", "settings": { "host": "192.168.1.1" } }
  },
  "displays": {
    "primary": { "slots": [ { "tile": "spotify", "pin": true }, { "stack": ["uptime", "ping"], "rotate": "5s" }, "ping-home" ] },
    "*":       { "slots": [] }
  }
}
```

- `tiles` lists each tile once: its `kind` (a folder in `tiles\`, see below) and, optionally, `settings` that replace the kind's settings. A tile can appear on several displays; it runs once.
- `displays` says which tiles each taskbar shows, left to right. A slot is:
  - a tile id: `"uptime"`
  - a pinned tile: `{ "tile": "spotify", "pin": true }` (see below)
  - a stack: `{ "stack": ["uptime", "ping"], "rotate": "5s" }`
- Display keys, in the order they are tried:
  1. the monitor's device id (edit mode writes these, e.g. `"\\\\?\\DISPLAY#DEL41A8#5&2b1c…"`, with the backslashes doubled as JSON requires)
  2. `primary`, for the primary display
  3. `*`, for every other display

## Tile kinds

Each kind of tile is one folder in `%LOCALAPPDATA%\TaskbarTiles\tiles\`, holding everything it needs. To share a tile, zip its folder; to install one, unzip it there (it appears in the `+` picker within a second).

```
tiles\
  uptime\  tile.json, uptime.ps1              bundled: replaced on install
  ping\    tile.json, ping.ps1                bundled: replaced on install
  media\   tile.json, TaskbarTiles.Plugins.Media.dll   bundled: replaced on install
  meeting\ tile.json, urgent.ps1              yours: never touched
```

The folder name is the kind's id (`"kind": "ping"`). Its `tile.json`:

```jsonc
{
  "name": "Ping a host",                    // shown in the + / stack picker (required)
  "description": "Pings a host every 15 s", // subtitle in the picker
  "connector": "script",                    // "script", or a connector id declared by a DLL in this folder (required)
  "settings": {                             // defaults for every tile of this kind
    "host": "1.1.1.1",
    "hooks": {
      "interval":    { "every": "15s", "run": "ping.ps1" },
      "flyout":      { "run": "ping.ps1" },
      "action:ping": { "run": "ping.ps1" }
    }
  }
}
```

- Unknown keys are ignored. Script `run` paths are relative to the kind folder, and scripts run in it ([script-connectors.md](script-connectors.md)).
- **Settings:** a tile gets the kind's `settings` with each top-level key of its own `settings` replacing the kind's key of the same name (`"host"` above). `hooks` may not appear in a tile's own `settings` in `config.json`: behaviour belongs to the kind folder (the notice says `hooks belong in tiles\ping\tile.json`).
- **DLL connectors:** a DLL in a kind folder is loaded only for that kind; its connector ids are private to the folder, so two kinds may ship a connector with the same id. DLLs load when Taskbar Tiles starts: after adding or changing a DLL, a notice asks you to exit and start Taskbar Tiles again (.NET cannot unload a DLL safely).
- **Edits apply by themselves:** saving `tile.json` or a script (`.ps1`, `.psm1`, `.py`, `.cmd` or `.bat` directly in the kind folder) restarts only the tiles of that kind. Files a script writes into its own folder (logs, caches) do not restart it.
- **Mistakes:** a `tile.json` that is not valid JSON or lacks `name` or `connector`, a DLL that does not load, two DLLs declaring the same connector id, or a `connector` no DLL in the folder declares makes the kind broken: its tiles show the error (naming the file, and the line for JSON errors), it is written to `logs\host.log`, and every other kind keeps working. `config.json` is rejected only for its own mistakes: a tile naming a kind with no folder in `tiles\` (or a folder without `tile.json`), `hooks` in a tile's settings, a tile without `kind`, or the old form `{ "connector": … }` (tiles from before tile folders: rewrite them as `{ "kind": "<folder>" }`).
- A folder in `tiles\` without `tile.json` is not a kind.

## Stacks

A stack takes one tile's place and shows its members one at a time, with a dot per member at the bottom.

- It moves to the next member every `rotate` (default `5s`; any duration from `1s` to `1h`, otherwise the config is rejected).
- It pauses while the mouse is over it. Right-click → **Pause rotation** stops it until **Resume rotation** (not saved; a restart resumes it).
- The mouse wheel shows the next or previous member.
- A member that turns urgent (a pulsing red outline) takes over the stack until you click it.
- A stack is as wide as its widest member, so it does not change size while it rotates.
- Every member keeps running while it is not shown.

## When space runs out

Tiles fill the free part of the taskbar next to the Start button (centered icons) or before the tray (left-aligned icons). When they do not fit:

1. Tiles become compact, from the right end: only the icon (or the first letter of the title), 40 DIP wide, with the title, subtitle and tooltip in the tooltip. Pinned tiles stay full size.
2. If that is not enough, tiles move into a `»` tile at the end, from the right end; pinned tiles go last. Click `»` to see them at full size. They still work there: buttons, popups (shown above the `»` tile) and the right-click menu.

This is recomputed whenever the free space changes (apps opened or closed, alignment, display or DPI changes).

## Edit layout

Right-click a tile or the bar → **Edit layout**. All taskbars switch to edit mode at once (so a display without tiles can get some). Each one edits its own display's tiles; dragging a tile onto another taskbar moves it to that display.

- Tiles get a dashed outline and a `×`. Clicks no longer open popups.
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there; release it past the last tile (over `»`, `+` or **Done**) to put it at the end. While you drag, a see-through copy of the tile follows the pointer (across monitors too), the tile itself fades, and the tiles of the taskbar under the pointer slide apart where it will land. Over the middle of a tile nothing slides; that tile lights up instead.
- Drag a tile onto another monitor's taskbar to move it to that display: before, into or after the tile under the pointer, as on its own taskbar, or at the end when you release it anywhere else on that taskbar (`+`, **Done**, or a taskbar without tiles). If that display already shows the tile, it only leaves this one. Both displays' entries are saved together. Releasing outside every taskbar cancels the drag. Tiles inside the `»` popup cannot be dragged.
- Drop it onto the middle of a stack to add it to the stack, or onto the middle of another tile to make a stack of the two.
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its entry under `tiles` is deleted in the same save; the previous version stays in `config.json.bak` until the next save. Its kind folder in `tiles\` is never deleted. Tiles you listed by hand but have not placed anywhere yet are never deleted, and `+` lists them first.
- `+` adds a tile at the end: a tile you already have that this display does not show, or a new tile of any kind in `tiles\` (listed by its `name`, with its `description` below). A new tile is written as `"ping": { "kind": "ping" }`, or with a free id such as `ping-2` when that id is taken.
- **Done** or Esc leaves edit mode.

The right-click menu changes the layout too, in and outside edit mode:

- On a tile, **Create stack…** opens the same panel as **+**. It lists the other tiles on this display that are not in a stack (by the title they show, or their id), then the tiles you can add. Pick one to make a stack of the two where the tile you clicked is.
- On a stack, **Add to stack…** opens the same panel: this display's tiles that are not in a stack, then the tiles you can add. The one you pick joins the end of this stack. **Remove from stack** takes the member shown out, right after the stack; **Pause rotation** stops it.
- **Remove tile** works like `×`.
- **Show tile folder** selects the tile's kind folder in Explorer; **Show config file** selects `config.json`.

To move a tile, drag it in edit mode.

### Which display entry is written

Changes are saved right away, under this display's key:

- the monitor's device id if it already has its own entry;
- otherwise `primary` when you edit the primary display;
- otherwise the monitor's device id, as a new entry copied from `*`. `*` itself is never written by edit mode, so editing one secondary display never changes the others.

### Saving rewrites config.json

Every change made in edit mode or with the menu rewrites the whole file:

- **Comments and your own formatting are lost** on the first save. Everything else stays: unknown keys, the order of tiles and displays, and settings exactly as they were. The file is written with two-space indentation.
- The previous version is kept next to it as `config.json.bak` (one level of undo: each save replaces it).
- The file is written to `config.json.tmp` first and then swapped in, so a crash never leaves half a file.
- If `config.json` has a mistake when you make a change, nothing is saved and a notice tells you where the mistake is.
