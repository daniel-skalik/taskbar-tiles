# Configuring Taskbar Tiles

Your tiles and layout live in `%LOCALAPPDATA%\TaskbarTiles\config.json`; what each kind of tile does lives in its own folder under `%LOCALAPPDATA%\TaskbarTiles\tiles\`. Right-click a tile → **Show** → **Config file** shows `config.json` in Explorer, **Show tile folder** that tile's kind folder. Both are watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).

## Install or update

See the [README](../README.md) for installing from a release zip or from source. Either way it is `install.cmd`: on success its window closes and the app starts; on failure it stays open so you can read the error. (From a terminal: `install.cmd`, or `powershell -ExecutionPolicy Bypass -File tools\install.ps1`.)

It stages the new build next to the install folder and verifies it (a failed copy or build changes nothing): from a release zip it copies the zip's `app` folder, in the repository it publishes a Release build first. Then it stops the installed copy, swaps the new folder in for `%LOCALAPPDATA%\TaskbarTiles\app` (so no stale files stay), replaces the bundled tile kinds in `tiles\` with the new copies (and removes retired ones), creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, your logs and every other folder in `tiles\` are never touched. To change a bundled kind, copy its folder under a new name first (for example `tiles\uptime` to `tiles\my-uptime`): install replaces the bundled ones. A `scripts` folder left over from an older version is not used any more and is left alone. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.

`uninstall.cmd` stops and removes the app and its Startup shortcut; your config, logs and tiles stay unless you add `-RemoveSettings`.

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
    "primary": { "left":  [ { "tile": "spotify", "pin": true }, { "stack": ["uptime", "ping"], "rotate": "5s" } ],
                 "right": [ "ping-home" ] },
    "*":       { "left": [] }
  }
}
```

- `tiles` lists each tile once: its `kind` (a folder in `tiles\`, see below) and, optionally, `settings` that replace the kind's settings and `width` (48 to 640), the width edit mode's resize grip saved. A tile can appear on several displays; it runs once.
- `displays` says which tiles each taskbar shows, left to right, on its two sides:
  - `"left"`: left of Start when the taskbar icons are centered. `"slots"`, its name in older configs, still works (not both in one display).
  - `"right"`: between the app icons and the tray, against the tray, so opening apps never moves them. Where the icons end is read with UI Automation, only while some display has right-side tiles.
  - With left-aligned icons both sides share the space between the icons and the tray: the right side sits against the tray, the left side right before it.
  - Each side shrinks and overflows on its own (see below). Drag a tile across in edit mode to move it to the other side.
- A slot is:
  - a tile id: `"uptime"`
  - a pinned tile: `{ "tile": "spotify", "pin": true }` (see below)
  - a stack: `{ "stack": ["uptime", "ping"], "rotate": "5s" }`
- Display keys, in the order they are tried:
  1. the monitor's device id (edit mode writes these, e.g. `"\\\\?\\DISPLAY#DEL41A8#5&2b1c…"`, with the backslashes doubled as JSON requires)
  2. `primary`, for the primary display
  3. `*`, for every other display

## Tile kinds

Each kind of tile is one folder in `%LOCALAPPDATA%\TaskbarTiles\tiles\`, holding everything it needs. To share a tile, zip its folder; to install one, unzip it there. A script-only kind appears in the `+` picker within a second; a kind with DLLs needs a Taskbar Tiles restart (a notice says so). Plugins live inside their kind folder: `%LOCALAPPDATA%\TaskbarTiles\plugins` is no longer scanned.

```
tiles\
  uptime\  tile.json, uptime.ps1              bundled: replaced on install
  media\   tile.json, TaskbarTiles.Plugins.Media.dll   bundled: replaced on install
  battery\ tile.json, TaskbarTiles.Plugins.Battery.dll  bundled: replaced on install
  meeting\ tile.json, urgent.ps1              yours: never touched
```

The folder name is the kind's id (`"kind": "ping"` for a `tiles\ping` folder). Its `tile.json`, for example:

```jsonc
{
  "name": "Ping a host",                    // shown in the + / stack picker (required)
  "description": "Pings a host every 15 s", // subtitle in the picker
  "group": "Network",                       // folder of the + / stack picker, slash separated: "Fun/Games" (default: top level)
  "connector": "script",                    // "script", or a connector id declared by a DLL in this folder (required)
  "width": 132,                             // tile width, 48 to 640 (default: the connector's; 132 for scripts, 268 for media)
  "compact": true,                          // false: its tiles never shrink to an icon when space runs out (default true)
  "resizable": true,                        // false: its tiles keep this width; edit mode offers no resize grip (default true)
  "minWidth": 80, "maxWidth": 400,          // how narrow and wide the resize grip goes (default 48 and 640)
  "fields": [                               // inputs of the tile's Settings… dialog (optional, see "Tile settings")
    { "key": "host", "label": "Host", "type": "text", "help": "Name or IP address to ping." }
  ],
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
- **Picker folders:** `group` files new tiles of the kind in a folder of the + / stack picker; slashes nest folders (`"Fun/Games"` is Games inside Fun). A folder that would hold a single tile is not shown: that tile is listed one level up. Parts are trimmed and empty ones dropped; folders with the same name in a different case are one folder. Your existing tiles are always listed at the top.
- A folder in `tiles\` without `tile.json` is not a kind.

### Tile settings

Right-click a tile → **Settings…** changes that tile's own settings without editing `config.json`. The entry is there when its kind's `tile.json` lists `"fields"`; the bundled `media` (Apps), `battery` (Read every, Devices) and `claude-usage` (Refresh every) do.

- Each field edits one top-level settings key; its default is the kind's `settings` value for that key.
  - `"key"`, `"label"` and `"type"` are required; `"help"` is shown under the input.
  - Types: `text` (optional `"choices"`: suggestions shown under the box, narrowed as you type; the running tile adds its own, e.g. the media tile the apps playing now); with `"browse": "folder"` (or `"file"`) a folder button in the box opens Windows' picker for a path; with `"multiple": true` the box holds a comma-separated list and each suggestion adds or removes its item, lit up while it is in the list (the media tile's Apps: `Spotify, Firefox` follows whichever plays, the first listed when neither does, and `Any` whatever app plays); `number` (optional `"min"`/`"max"` numbers); `toggle` (true or false); `choice` (`"choices": ["a", "b"]`); `duration` (written like `"60s"`, optional `"min"`/`"max"` such as `"10s"` and `"1h"`); `pick` (a checkbox per value, saved as a list of the ticked ones; the values are the running tile's own, e.g. its devices, any fixed `"choices"`, and those saved earlier; with nothing saved every box is ticked).
  - A field may not edit `hooks`, and each key appears once. A list with a mistake makes the kind broken, with the message naming the field.
- The dialog opens above the tile. A text, number or duration box holds the tile's own value; empty means the kind's default, shown dim behind it. A value that does not fit shows why under its field, and **Save** stays off. **Reset to defaults** empties every box. Esc or a click elsewhere closes it without saving.
- **Save** writes `tiles.<id>.settings` in `config.json`, the same way edit mode saves (see "Saving rewrites config.json"). A value equal to the default, or empty, removes its key, so the tile follows the kind's default again; other keys stay. The tile restarts with the new settings.

### Device batteries

The bundled `battery` kind shows the lowest battery of your wireless devices on the tile and every device in its popup, with charging and asleep states. It reads Bluetooth devices from Windows (the level shown in Settings › Bluetooth & devices) and Logitech devices on a Bolt or Unifying receiver, or connected directly over USB or Bluetooth, over Logitech's HID++ protocol (alongside Logi Options+). Setting `"interval"` (default `"60s"`, `10s` to `1h`) is how often it reads. Setting `"show"` lists the device names to show (e.g. `["MX Master 3S", "WH-1000XM5"]`, ignoring case); others, and devices connected later, are hidden. Without it every device shows. **Settings…** → Devices ticks them from the devices the tile has seen since it started; right-click → **Refresh now** reads at once. A Logitech device that falls asleep keeps its last level, marked "asleep". Other vendors' receivers (Razer, Corsair, …) only report through their own apps and are not shown.

### Claude usage

The bundled `claude-usage` kind shows your Claude plan's limits: the 5-hour window on the first line ("5h 42%/60% · resets 18:30"), the week below it ("Week 18%/86%"). The second number is what an even pace would have used by now: the 5-hour window evenly from its start to its reset, the week one seventh per day begun. The line along the bottom is the 5-hour use, over a dim line at the even pace; past it, the part beyond the pace turns orange. A dashed line carries on from its end to where the 5-hour use will be at the reset if you go on at the rate so far (not in the first tenth of a window, where one burst would throw it far off). It shows the Claude logo (`claude.png` in its folder), with an orange dot at 80% and a red one at 95%. Click it for both windows with their reset times, each with an **At this rate** row: the use at the reset, or when the limit runs out if that comes first; plus any per-model weekly limit; **Refresh now** reads them again at once, and **Open usage page** opens claude.ai's usage settings.

- It reads every 5 minutes (setting `"refresh"`, `1m` to `1h`, also in **Settings…**) and on **Refresh now**, from the usage endpoint Claude Code's `/usage` uses, signed in with Claude Code's login in `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%`; setting `"credentials"` overrides the path). The token goes only to `api.anthropic.com` and is never stored elsewhere. The endpoint is not documented, so a Claude update may change it.
- Claude Code renews the login while you use it. If it has expired, the tile says so until Claude Code runs again; numbers under an hour old keep showing in the meantime, with the reason in the tooltip.
- Only Claude Pro and Max plans have these limits.

### Clipboard peek

The bundled `clipboard` kind shows the last thing you copied: text as one line with when and how long, an image as its size, files as their count and first name. Click it for your recent copies; click one to copy it again (text only). **Clear** empties the list. The popup stays open after copying, Clear or Save latest. Setting `"history"` (default 15, 1 to 50, also in **Settings…**) is how many it keeps. **Save latest** writes what is on the clipboard now (the top copy) to a file: an image as PNG (when there is both an image and text, the image), text as a UTF-8 .txt file, named like `clipboard-2026-11-05-143012.png`. They go to the folder in setting `"saveFolder"` (also in **Settings…**, with a folder picker; default `Pictures\Clipboard`, created when needed). The popup then shows the file; click it to see it in Explorer. Copied files and copies marked private are not saved. Apart from Save, copies stay in memory only, never on disk or in the log, and are gone when the app closes. Copies that password managers mark as private (Windows' "exclude from clipboard history" flag) are skipped.

### System monitor

The bundled `system` kind shows CPU, memory, GPU, network speed and every drive's used space, sampled every second. **Settings…** → Show ticks which (setting `"show"`, e.g. `["CPU", "RAM"]`; all five without it; `"Disk"` means every fixed drive). Layout `"All at once"` fits them on two lines; `"Rotate"` shows one at a time, each drive on its own, with its own icon and its last minute as a chart behind the text, switching every `"rotate"` (default `"5s"`). The icon turns orange at 80% and red at 95%. Click it for every metric and each drive's free space; **Task Manager** opens it.

### Docker

The bundled `docker` kind shows how many of your Docker containers run and how many are stopped, read with the `docker` command Docker Desktop installs every 15 seconds (setting `"refresh"`, `5s` to `10m`, also in **Settings…**; setting `"docker"` names another `docker.exe`). It shows the Docker logo (`docker.png` in its folder), with an orange dot while a container restarts, is paused or reports unhealthy (the tile then counts those first), and in grey while Docker Desktop is not running. Click it for every container with its status, grouped by Compose project as in Docker Desktop (a project's row counts its running and stopped containers, its containers are listed under it), problems first, then running ones; **Open Docker Desktop** opens it. While Docker Desktop is not running the tile says so, and the popup offers **Start Docker Desktop**.

### Git repository

The bundled `git` kind follows one repository: its folder and branch on the first line, its local changes (staged, changed, new files, conflicts) and how far it is ahead of (`↑`) or behind (`↓`) its upstream below, or "Clean". Settings, also in **Settings…**:

- `"repo"`: the repository's folder (type it, or pick it with the folder button in the box). Until it is set the tile asks for one. Add the tile several times for several repositories.
- `"autoFetch"`: fetch from the remote every 5 minutes, so the behind count stays current (default off: behind counts are as of your last fetch).
- `"refresh"`: how often it reads `git status` (default `"15s"`, `5s` to `10m`). Setting `"git"` names another `git.exe`.

The icon is green when the branch is clean and up to date, blue with local changes or commits to push, orange when it is behind its upstream, and red with conflicts. Click it for the branch, upstream, change counts and last commit; **Fetch** fetches now and **Open folder** opens the repository in Explorer. It reads without taking Git's index lock, so it never gets in the way of your own Git commands.

### To do

The bundled `todo` kind keeps your tasks in a plain Markdown file: every `- [ ] task` line is an open task and every `- [x] task` line a done one (`*` instead of `-` works too). The tile shows how many are open and the first of them. Click it for the list: type in the box and press Enter to add a task (it goes after the last task in the file), click a task to tick it off or back on, **Clear done** deletes the done lines, and **Open file** opens the file in its usual app. The popup stays open while you work through it. Indented tasks (subtasks, by spaces or tabs) stay indented under their parent in the popup; done tasks are listed flat below.

- Setting `"file"` (also in **Settings…**, with a file picker) is the Markdown file; default `%USERPROFILE%\Documents\todo.md`, created with its folder on the first task you add. Put it in OneDrive, Dropbox or an Obsidian vault to have it elsewhere too.
- Setting `"showDone"` (default `true`) lists up to 10 done tasks under the open ones.
- Headings, notes and blank lines in the file are kept as they are and not shown. The file keeps its line endings and encoding (UTF-8, with or without BOM), and each change replaces it in one step.
- It reads the file every 10 seconds and right after every change, so edits made in another app show up on their own. For a second list, add a second To do tile with another file.

### Linear

The bundled `linear` kind shows the Linear issues assigned to you that are in progress or still to do (state types *started* and *unstarted*, every team; at most 50 issues are counted): how many are in progress and the most pressing one. Click it for up to 15 of them, in progress first, then by priority and the most recently updated; each row shows its state (a half-filled circle in progress, an empty one to do) and its priority on the right (a red "!" for urgent, three to one bars for high to low, dashes for none). Click one to open it in your browser. The tile and popup show the Linear icon from Linear's brand kit. **Open Linear** opens your assigned issues there.

- **Setting it up:** the first click asks for a personal API key. Create one in Linear under Settings › Security & access › Personal API keys (**Get a key** opens that page), paste it and press Enter. The tile checks it with Linear before keeping it. You can also store it from a terminal: `cmdkey /generic:TaskbarTiles-Linear /user:api /pass` (it asks for the key, so it stays out of your shell history).
- **Where the key lives:** in Windows Credential Manager (generic credential `TaskbarTiles-Linear`), encrypted for your Windows account. It is sent only to `api.linear.app`, and never written to `config.json`, the log or the cache. **Forget key** in the popup removes it.
- Setting `"refresh"` (default `2m`, 1 to 30 minutes, also in **Settings…**) is how often the issues are fetched; the popup reuses the last fetch while it is younger than that (at least 2 minutes). When a fetch fails, the tile keeps showing the last issues it got and says so in its tooltip.

### GitHub pull requests

The bundled `github` kind shows your open pull requests on GitHub (at most 30 of each kind): those waiting for your review, and your own sorted by what they wait on; and recent GitHub Actions runs. The title counts what needs you and what you have to review; the subtitle is the most urgent one. The tile and popup show the GitHub mark, from GitHub's Primer Octicons. On the tile the mark is red when one of yours is blocked, amber when one needs your action, blue when only reviews wait, green when nothing is open and gray when yours only wait for others. Click it for the list, in up to five groups; click a pull request or run to open it in your browser.

- **Waiting for your review:** someone asked you (or your team) for a review.
- **Blocked:** yours, with merge conflicts, failing checks, or approved but still blocked by a branch rule.
- **Needs your action:** yours, with changes requested, behind its base branch, or ready to merge.
- **Waiting for others:** yours, waiting for a review or for checks to finish; drafts come last.
- **Actions:** recent GitHub Actions runs: the latest run of each workflow on each branch from the last day, failed first, then waiting for approval, running, passed and cancelled. It lists runs you started in the repositories of your open pull requests, and anyone's runs in the repositories you watch. A failed run or one waiting for approval counts as needing you and turns the mark red or amber; **Re-run failed** re-runs the failed jobs of every failed run listed.

- **Setting it up:** the tile talks to GitHub through the [GitHub CLI](https://cli.github.com/) (`gh`) and its sign-in, so it never sees or stores a token. Without `gh` the popup offers **Install gh**: a window installs it with `winget` (it asks first), then runs `gh auth login` in your browser. When `gh` is installed but not signed in, **Sign in** opens that sign-in window.
- Setting `"repos"` (**Watch Actions in**) lists up to 10 repositories, as `owner/name` separated by commas, whose runs you see from anyone, such as `acme/api` to watch its main branch.
- Setting `"filter"` (also in **Settings…**) adds GitHub search qualifiers, such as `org:acme` or `repo:acme/api`; empty means every repository.
- Setting `"refresh"` (default `2m`, 1 to 30 minutes) is how often pull requests are fetched; the popup reuses the last fetch while it is younger than that (at least 2 minutes). When a fetch fails, the tile keeps showing the last list and says so in its tooltip.

### Pixel scenes

Four bundled kinds draw a tiny animated world across the whole tile; click to join in, hover for the score or status. In the + picker they are under **Fun**.

- `aquarium`: fish, bubbles and a treasure chest; a click drops food.
- `cozy-farm`: a farm that follows your clock and the real sun: dawn at sunrise, golden hour before sunset, then a starry night with fireflies. A click sets off a random surprise: by day the apple tree shakes, a bloom ripples across the field or the farmer jumps for joy; at night a meteor shower falls or the fireflies dance. Settings, also in **Settings…**:
  - `"workStart"` and `"workEnd"`: when the farmer is out in the field: `"sunrise"`, `"sunset"` or a 24-hour time such as `"7:30"` (default sunrise to sunset). The house sleeps from 23:00 to 5:30 whatever the hours.
  - `"latitude"` and `"longitude"`: where to compute sunrise and sunset, in degrees (north and east positive). Empty: the middle of the country set in Windows (Settings › Time & language › Region); no location permission is used.
- `snake`: plays itself; a click starts over. Keeps its best score.
- `racers`: pixel cars race; a click starts a new race. Counts wins per color.

Install removes the `pixel-scene` kind these replace; a tile still using it shows an error until you remove it and add one of these.

## Stacks

A stack takes one tile's place and shows its members one at a time, with a dot per member at the bottom.

- It moves to the next member every `rotate` (default `5s`; any duration from `1s` to `1h`, otherwise the config is rejected).
- It pauses while the mouse is over it. Right-click → **Pause rotation** stops it until **Resume rotation** (not saved; a restart resumes it).
- The mouse wheel shows the next or previous member.
- A member that turns urgent (a pulsing red outline) takes over the stack until you click it.
- A stack is as wide as its widest member, so it does not change size while it rotates.
- Every member keeps running while it is not shown.

## When space runs out

Each side fills its own free part of the taskbar: next to the Start button (`"left"` with centered icons) or before the tray (`"right"`, or both sides with left-aligned icons). When a side's tiles do not fit:

1. Tiles become compact, from the right end: only the icon (or the first letter of the title), 40 DIP wide, with the title, subtitle and tooltip in the tooltip. Pinned tiles stay full size, and so do tiles of a kind whose `tile.json` says `"compact": false` (the bundled `media` kind does; a stack stays full size if any member does).
2. If that is not enough, tiles move into a `»` tile at the end, from the right end; pinned tiles go last. Click `»` to see them at full size. They still work there: buttons, popups (shown above the `»` tile) and the right-click menu.

This is recomputed whenever the free space changes (apps opened or closed, alignment, display or DPI changes).

## Edit layout

Right-click a tile or the bar → **Edit layout**. A taskbar without a single tile shows just a `+` before the tray: click it to add a tile, or right-click it for **Edit layout**. All taskbars switch to edit mode at once, both sides of each (so a side without tiles can get some). Each side edits its own tiles; dragging a tile onto the other side or another taskbar moves it there.

- Tiles get a dashed outline and a `×`. Clicks no longer open popups.
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there; release it past the last tile (over `»`, `+` or **Done**) to put it at the end. While you drag, a see-through copy of the tile follows the pointer (across monitors too), the tile itself fades, and the tiles of the taskbar under the pointer slide apart where it will land. Over the middle of a tile nothing slides; that tile lights up instead.
- Drag a tile onto another monitor's taskbar to move it to that display: before, into or after the tile under the pointer, as on its own taskbar, or at the end when you release it anywhere else on that taskbar (`+`, **Done**, or a taskbar without tiles). If that display already shows the tile, it only leaves this one. Both displays' entries are saved together. Releasing outside every taskbar cancels the drag. Tiles inside the `»` popup cannot be dragged.
- Drop it onto the middle of a stack to add it to the stack, or onto the middle of another tile to make a stack of the two.
- Drag the grip on a tile's outer edge (the right edge; the left edge on the right side, which grows away from the tray) to resize it; letting go saves the width as `"width"` in the tile's entry under `tiles`, so it applies on every display. Double-click the grip for the kind's width again. Resizing a stack resizes all its members. The grip stops at the kind's `minWidth` and `maxWidth` (a stack: the range every member allows); a saved width outside them is kept within them. Kinds whose `tile.json` says `"resizable": false` have no grip, and a `width` saved for them is ignored. A narrow tile drops its buttons (such as the media controls) before its title gets too short, and the pixel scenes draw a world as wide as the tile: a longer field, a bigger tank, a longer race track.
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its entry under `tiles` is deleted in the same save; the previous version stays in `config.json.bak` until the next save. Its kind folder in `tiles\` is never deleted. Tiles you listed by hand but have not placed anywhere yet are never deleted; `+` lists them under **Your tiles**.
- `+` adds a tile at the end of its side: a tile you already have that this display does not show on either side (in the **Your tiles** folder, each marked "On another display" or "Not shown"), or a new tile of any kind in `tiles\` (listed by its `name`, with its `description` below). A new tile is written as `"ping": { "kind": "ping" }`, or with a free id such as `ping-2` when that id is taken.
- **Done** or Esc leaves edit mode.

The right-click menu changes the layout too, in and outside edit mode:

- On a tile, **Create stack…** opens the same panel as **+**. It lists the other tiles on this display that are not in a stack (by the title they show, or their id), then the tiles you can add. Pick one to make a stack of the two where the tile you clicked is.
- On a stack, **Add to stack…** opens the same panel: this display's tiles that are not in a stack, then the tiles you can add. The one you pick joins the end of this stack. **Remove from stack** takes the member shown out, right after the stack; **Pause rotation** stops it.
- **Show** → **Tile folder** selects the tile's kind folder in Explorer, **Connector log** its log, **Config file** `config.json`.
- **More** → **Restart tile** starts the tile again from scratch; **Exit Taskbar Tiles** quits.

To move a tile (also to the other side), drag it in edit mode; `×` removes it.

### Which display entry is written

Changes are saved right away, under this display's key:

- the monitor's device id if it already has its own entry;
- otherwise `primary` when you edit the primary display;
- otherwise the monitor's device id, as a new entry copied from `*`. `*` itself is never written by edit mode, so editing one secondary display never changes the others.

### Saving rewrites config.json

Every change made in edit mode or with the menu rewrites the whole file:

- **Comments and your own formatting are lost** on the first save. A display's `"slots"` is written as `"left"`, and `"right"` only while that side has tiles. Everything else stays: unknown keys, the order of tiles and displays, and settings exactly as they were. The file is written with two-space indentation.
- The previous version is kept next to it as `config.json.bak` (one level of undo: each save replaces it).
- The file is written to `config.json.tmp` first and then swapped in, so a crash never leaves half a file.
- If `config.json` has a mistake when you make a change, nothing is saved and a notice tells you where the mistake is.
