# Script connectors

The built-in `script` connector drives a tile from scripts. A script tile kind is a folder in `%LOCALAPPDATA%\TaskbarTiles\tiles\` with a `tile.json` whose `"connector"` is `"script"`; its `settings.hooks` wire scripts in that folder to *hooks*, and each script prints JSON that becomes the tile or its popup. PowerShell and Python get special handling; anything else runs through `cmd.exe`, so batch files and any program on `PATH` work too. Tile kinds in general are described in [configuration.md](configuration.md#tile-kinds).

## Quick start

Three script kinds come with the app: `tiles\uptime` (clock and uptime), `tiles\ping` (ping a host, with a button and a popup) and `tiles\claude` (which Claude Code sessions are working, idle or blocked, from `claude agents --json`, under the Claude app's logo with a status dot; its popup lists them, and **Open Claude** brings the desktop app forward; set `"claude"` to the path of `claude.exe` when it is neither on `PATH` nor in `%USERPROFILE%\.local\bin`). Install replaces them, so to change one, copy its folder under a new name first. To make your own:

1. Create a folder, e.g. `%LOCALAPPDATA%\TaskbarTiles\tiles\meeting\`, and put your script in it (`urgent.ps1`).
2. Next to it, write `tile.json`:

```jsonc
{
  "name": "Next meeting",
  "description": "Turns urgent five minutes before a meeting",
  "connector": "script",
  "settings": {
    "hooks": {
      "interval": { "every": "1m", "run": "urgent.ps1" },
      "flyout":   { "run": "urgent.ps1" }
    }
  }
}
```

3. Save. Within a second the kind appears in the `+` picker (right-click a tile → **Edit layout** → `+`) as "Next meeting". Picking it adds `"meeting": { "kind": "meeting" }` to `config.json` and places it. Saving `tile.json` or the script again restarts the kind's tiles. Right-click a tile → **Show tile folder** selects its folder in Explorer.

A tile can override the kind's other settings in `config.json`, key by key: `"ping-home": { "kind": "ping", "settings": { "host": "192.168.1.1" } }`. `hooks` stay in `tile.json`.

## Hooks

`settings.hooks` in `tile.json` maps hook names to `{ "run": "...", "timeout": "20s" }` (`interval` also takes `every`; any other key is an error). Except for `interval`, a plain string is short for `{ "run": "..." }`.

| Hook | `TILE_HOOK` | When it runs | What it prints |
|---|---|---|---|
| `interval` `{ every, run }` | `interval` | At start and every `every` (e.g. `"30s"`, `"10m"`; from `1s` to `24h`). `"every": "$refresh"` takes the duration from the top-level setting `refresh` instead, so a **Settings…** field can change it. A run is skipped while the previous one is still going. Also on "Refresh now", after the PC wakes from sleep, and after an action hook that printed nothing. | Tile JSON |
| `watch` `{ run }` | `watch` | At start; meant to run forever. Every line it prints is one tile update. When it exits it is started again after 2 s, 10 s, 1 min, then every 5 min (back to 2 s after a valid line). A non-zero exit or a failure to start shows the error look until the next valid line; a clean exit is only logged. | One tile JSON object per line |
| `click` `{ run }` | `click` | On a plain click, only when the tile has no popup. | Tile JSON, or nothing |
| `flyout` `{ run }` | `flyout` | When the tile is clicked, to build its popup. | Popup JSON; print nothing for no popup (the click then goes to `click`) |
| `action:<id>` `{ run }` | `action` | When a tile button or popup action with that `id` is pressed. `TILE_ACTION` holds the id. | Tile JSON, or nothing |
| `startup` `{ run }` | `startup` | Once at start, before the first `interval` run. Also on "Refresh now" when there is no `interval`. | Tile JSON |
| `resume` `{ run }` | `resume` | After the PC wakes from sleep. | Tile JSON |

Durations use `ms`, `s`, `m` or `h`: `"500ms"`, `"30s"`, `"10m"`, `"1h"`.

A tile that has only `click`, `flyout` or `action:` hooks shows its tile id until a script prints something.

### Timeouts

Each run may take up to `timeout` (default 60 s; any duration above 0 up to `24h`), then the script and everything it started is killed and the tile shows an error. `watch` has no timeout.

`click`, `flyout` and `action:` runs are interactive, and the host gives up on any tile action after 10 s. So for these hooks the effective limit is 10 s even if you set a longer `timeout`: past that, the script is killed and the tile shows "Not responding" for 3 s. Keep interactive scripts quick, and do slow work in an `interval` or `watch` script instead.

## `run`

`run` is a script path relative to the kind folder (`%LOCALAPPDATA%\TaskbarTiles\tiles\<kind>`), an absolute path, or a command line. The first word is the script; quote it if it contains spaces. Everything after it is passed on as arguments.

| `run` starts with | Runs as |
|---|---|
| `*.ps1` | `pwsh` (PowerShell 7) if it is on `PATH`, else `powershell.exe`, with `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <arguments>` |
| `*.py` | `python -u <script> <arguments>` |
| any other existing file (`.cmd`, `.bat`, `.exe`, …) | `cmd.exe /d /s /c "<script> <arguments>"` |
| anything else | the whole line through `cmd.exe /d /s /c` |

Scripts run hidden, with the kind folder as the working directory and no input (stdin is closed).

## Environment

| Variable | Value |
|---|---|
| `TILE_ID` | The tile's id from `config.json`, e.g. `ping` |
| `TILE_HOOK` | `interval`, `watch`, `click`, `flyout`, `action`, `startup` or `resume` |
| `TILE_ACTION` | The action id for `action:<id>` hooks (e.g. `ping`); empty otherwise |
| `TILE_SETTINGS_JSON` | The tile's effective settings as JSON: the kind's `settings` from `tile.json` (including `hooks`) with the tile's own `settings` from `config.json` replacing keys of the same name. Put your own options next to `hooks` (like `"host"` in `tiles\ping\tile.json`). |

One script can serve several hooks by looking at `TILE_HOOK`, as both bundled kinds do.

## Tile JSON

Print one JSON object. Every field is optional; field names are camelCase.

| Field | Type | Meaning |
|---|---|---|
| `title` | string | First line. Numbers are accepted and shown as text. |
| `subtitle` | string | Second line, dimmer. |
| `tooltip` | string | Shown on hover. |
| `icon` | string or object | A glyph name (`"check"`), or `{ "glyph": "check", "color": "ok" }`. |
| `image` | string | Base64 PNG or JPEG, shown instead of the icon. A `data:image/png;base64,` prefix is fine. |
| `buttons` | array | Inline icon buttons: `{ "id": "rerun", "glyph": "refresh", "tooltip": "Run again", "enabled": true }`. `id` is required and runs the `action:<id>` hook; `glyph` defaults to `info`, `enabled` to `true`. |
| `progress` | number | 0 to 1; a thin bar along the bottom. Must be a finite number: values such as `1e999` are dropped with a warning. |
| `sparkline` | array of numbers | A small line chart (needs at least two values). Non-finite values are dropped with a warning. |
| `urgent` | boolean | Pulsing red outline. |

```json
{ "title": "CI", "subtitle": "main: passing", "icon": { "glyph": "check", "color": "ok" },
  "buttons": [ { "id": "rerun", "glyph": "refresh", "tooltip": "Run again" } ], "sparkline": [3, 5, 4, 6] }
```

## Popup JSON

| Field | Type | Meaning |
|---|---|---|
| `title` | string | Popup heading. Without it the popup gets a warning in the log and an empty heading. |
| `subtitle` | string | Under the heading. |
| `rows` | array | Each row is a string, or `{ "text": "Build #41", "value": "2 min", "icon": { "glyph": "check", "color": "ok" } }`; the icon shows as a colored dot. |
| `actions` | array | Buttons at the bottom: `{ "id": "rerun", "label": "Run again", "primary": true }`. `id` is required and runs the `action:<id>` hook; `label` defaults to the id. Ids starting with `host:` are reserved for the host (for example `host:restart` and `host:show-log` on the error popup); do not use them. |
| `image` | string | Base64 PNG or JPEG shown next to the heading. |

```json
{ "title": "1.1.1.1", "subtitle": "Last 4 pings",
  "rows": [ { "text": "Ping 1", "value": "12 ms", "icon": { "glyph": "check", "color": "ok" } } ],
  "actions": [ { "id": "ping", "label": "Ping again", "primary": true } ] }
```

## Glyphs and colors

Glyph names: `play`, `pause`, `previous`, `next`, `check`, `cancel`, `warning`, `refresh`, `music`, `info`, `sync`, `cloud`, `clock`, `mouse`, `keyboard`, `headphones`, `battery-0` to `battery-10` and `battery-charging-0` to `battery-charging-10` (the battery filled to tenths). Names are case-insensitive. An unknown name shows `info`.

Color names (case does not matter): `neutral` (default), `accent` (blue), `ok` (green), `warn` (amber), `bad` (red). An unknown name is `neutral`.

## When something goes wrong

- A script that cannot start (for example `python` is not installed: the tile says "could not start the script"), times out, exits with a non-zero code, or prints malformed JSON or something that is not a JSON object puts the tile in the error look: a warning icon, "Error", and the message in the subtitle and tooltip. Click the tile to see the script's stderr (or its output if stderr is empty), with **Restart tile** and **Open log** buttons. The next successful run clears the error look, even if that run prints nothing.
- If a `flyout` (popup) script fails, the popup itself shows "Script error" with the details and the **Restart tile** and **Open log** buttons. The tile is not put in the error look, and the script runs again the next time you open the popup.
- Unknown fields, values of the wrong type and non-finite numbers are ignored; each one is written to the tile's log as a warning, which helps catch typos such as `tittle`.
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`, an unknown setting inside a hook such as `"timout"`, or `every` on a hook other than `interval`) put the tile in the error look and nothing runs until `tile.json` is fixed.
- A `tile.json` that is not valid JSON or lacks `name` or `connector` shows its error (with the line for JSON errors) on every tile of that kind; other kinds keep working.
- Every failure is written to `%LOCALAPPDATA%\TaskbarTiles\logs\tile-<id>.log`. Right-click the tile → **Show** → **Connector log** shows it selected in Explorer; **Tile folder** shows the kind folder with its `tile.json` and scripts.
- Right-click → **Refresh now** re-runs `interval` (or `startup`). **More** → **Restart tile** starts the connector again from scratch.

## Output size

Output is capped so a runaway script cannot eat memory. A single line longer than 1 MB is cut, and the total output of a run is capped too. When either happens, `(output truncated)` is appended to the script's stderr (visible in the error popup and the log). Print compact JSON and keep images small.

## Encoding

Output is read as UTF-8.

- Windows PowerShell 5.1 writes in the console code page. Any script that prints non-ASCII text must start with `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` (the bundled kinds do) so the text arrives intact. PowerShell 7 writes UTF-8 already.
- Python is started with `PYTHONIOENCODING=utf-8`.
- In `ConvertTo-Json`, use `-Compress` and a `-Depth` larger than your nesting (the default depth of 2 cuts off `icon` inside `rows`).

## More examples

Python (`interval`), `tiles\disk\disk.py`, with `"run": "disk.py"` in `tiles\disk\tile.json`:

```python
import json, shutil
total, used, free = shutil.disk_usage("C:\\")
share = used / total
print(json.dumps({
    "title": "C:",
    "subtitle": f"{free / 2**30:.0f} GB free",
    "progress": share,
    "icon": {"glyph": "info", "color": "warn" if share > 0.9 else "ok"},
}))
```

A command line instead of a script file, as the whole `tiles\hello\tile.json`:

```jsonc
{ "name": "Hello", "connector": "script", "settings": { "hooks": {
    "startup": "echo {\"title\":\"Hello\",\"icon\":\"info\"}" } } }
```

A `watch` script that pushes an update every 5 seconds, `tiles\clock\watch-time.ps1`, with `"watch": { "run": "watch-time.ps1" }`:

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
while ($true) {
    @{ title = (Get-Date).ToString('HH\:mm\:ss'); icon = 'clock' } | ConvertTo-Json -Compress
    Start-Sleep -Seconds 5
}
```
