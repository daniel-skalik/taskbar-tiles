# Configuring Taskbar Tiles

Everything lives in `%LOCALAPPDATA%\TaskbarTiles\config.json`. Right-click a tile → **Show config file** shows it in Explorer. The file is watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).

## Install or update

From your own terminal, in the repository:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

It publishes a Release build into a staging folder next to the install folder and verifies it (a failed build changes nothing), then stops the installed copy, swaps the new folder in for `%LOCALAPPDATA%\TaskbarTiles\app` (so no stale files stay), creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, scripts, plugins and logs are never touched. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.

## Tiles and displays

```jsonc
{
  "tiles": {
    "spotify": { "connector": "media", "settings": { "app": "Spotify" } },
    "uptime":  { "connector": "script", "settings": { "hooks": { "interval": { "every": "30s", "run": "scripts/uptime.ps1" } } } },
    "ping":    { "connector": "script", "settings": { "hooks": { "interval": { "every": "15s", "run": "scripts/ping.ps1" } } } }
  },
  "displays": {
    "primary": { "slots": [ { "tile": "spotify", "pin": true }, { "stack": ["uptime", "ping"], "rotate": "5s" } ] },
    "*":       { "slots": [] }
  }
}
```

- `tiles` defines each tile once: its connector and the connector's settings. A tile can appear on several displays; it runs once.
- `displays` says which tiles each taskbar shows, left to right. A slot is:
  - a tile id: `"uptime"`
  - a pinned tile: `{ "tile": "spotify", "pin": true }` (see below)
  - a stack: `{ "stack": ["uptime", "ping"], "rotate": "5s" }`
- Display keys, in the order they are tried:
  1. the monitor's device id (edit mode writes these, e.g. `"\\\\?\\DISPLAY#DEL41A8#5&2b1c…"`, with the backslashes doubled as JSON requires)
  2. `primary`, for the primary display
  3. `*`, for every other display

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
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there.
- Drag a tile onto another monitor's taskbar to move it to that display: before, into or after the tile under the pointer, as on its own taskbar, or at the end when you release it anywhere else on that taskbar (`+`, **Done**, or a taskbar without tiles). If that display already shows the tile, it only leaves this one. Both displays' entries are saved together. Releasing outside every taskbar cancels the drag. Tiles inside the `»` popup cannot be dragged.
- Drop it onto the middle of a stack to add it to the stack, or onto the middle of another tile to make a stack of the two.
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its definition under `tiles` is deleted in the same save; `config.json.bak` still has it. Tiles you defined by hand but have not placed anywhere yet are never deleted, and `+` lists them first.
- `+` adds a tile at the end: a tile you already have that this display does not show, a new tile from a loaded plugin, or a new script tile from one of the sample scripts. New tiles get a free id such as `ping-2`.
- **Done** or Esc leaves edit mode.

The right-click menu changes the layout too, in and outside edit mode:

- On a tile, **Create stack with ▸** lists the other tiles on this display that are not in a stack (by the title they show, or their id). Pick one to make a stack of the two where the tile you clicked is.
- On a stack, **Add to stack ▸** lists this display's tiles that are not in a stack; the one you pick moves into this stack. **Remove from stack** takes the member shown out, right after the stack; **Pause rotation** stops it.
- **Remove tile** works like `×`.

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
