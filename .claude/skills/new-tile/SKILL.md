---
name: new-tile
description: Create a new Taskbar Tiles tile kind (a live tile on the Windows taskbar) from a description such as "a tile that shows my GitHub notifications" or "show the weather". Use when the user wants a new tile, wants to change what a tile shows, or asks how to make their own tile in this repository.
---

# Make a new tile

A tile kind is one folder: a `tile.json` plus the scripts or DLL that feed it. The app picks up a new folder in
`%LOCALAPPDATA%\TaskbarTiles\tiles\` within a second; the folder name is the kind id. Read these before writing
anything; they are the reference and this skill does not repeat them:

- `docs/script-connectors.md`: hooks, environment variables, tile and popup JSON, glyphs, colors, errors.
- `docs/configuration.md`, section "Tile kinds" and "Tile settings": every `tile.json` key and the Settings dialog fields.
- A bundled kind close to what you need, as a model: `tiles/uptime` (simplest, with a popup),
  `tiles/claude-usage` (fields, image, cache, web request); buttons and actions are in `docs/script-connectors.md`.

## 1. Settle what it is

From the user's words, decide and say in one or two lines (ask only what you cannot decide sensibly yourself):

- **What the tile shows** on its two lines, what its icon or color means, and how often it refreshes.
- **What a click does:** a popup (`flyout` hook) with rows and action buttons, or a direct `click` action, or nothing.
- **Settings** the user may want to change without editing files (a host, a city, a refresh interval): put them in
  `settings` with a `fields` entry each, so they appear under right-click › **Settings…**.
- **Script or DLL.** Default to a script tile (PowerShell; Python if the user prefers and has it). Choose a .NET DLL
  only for what a script cannot do well: drawing frames (`TileState.Canvas`), sampling every second or faster,
  Windows APIs with events, or keeping state between updates in memory. See section 5.
- **Personal or bundled.** Personal (the default): it lives in the user's data folder only. Bundled: it ships with
  the app for everyone, which needs the extra steps in section 4; do that only when the user asks to contribute it.

Pick a short lowercase kind id with dashes (`github-notifications`), a `name` for the picker, a one-line
`description`, and a `group` folder for the picker. Reuse a group the bundled kinds use when one fits
(`grep '"group"' tiles/*/tile.json`; for example `"Fun"`, `"System"`); otherwise pick a short plain one
such as `"Network"` or `"Dev"`. A group with only one tile is shown flat, not as a folder.

## 2. Write it in `my-tiles\<id>\`

Build the folder in the repository's `my-tiles\<id>\` (create `my-tiles` if it is missing; `.gitignore` ignores
it), not straight in the data folder: there you can run and preview it freely, and an agent in the Claude desktop
app cannot write to `%LOCALAPPDATA%` reliably anyway (its writes there are redirected to a private copy).

A script tile with a popup, one script serving both hooks, as a starting point:

```jsonc
{
  "name": "Wi-Fi",
  "description": "The network you are on and its signal",
  "group": "Network",
  "connector": "script",
  "width": 200,
  "settings": {
    "hooks": {
      "interval": { "every": "30s", "run": "wifi.ps1" },  // prints the tile
      "flyout":   { "run": "wifi.ps1" }                   // prints the popup when the tile is clicked
    }
  }
}
```

Add `"action:<id>": { "run": "wifi.ps1" }` for each button or popup action, and `"fields"` plus a default in
`settings` for each option.

Script rules that save a round trip:

- One script can serve every hook by switching on `$env:TILE_HOOK`; print exactly one JSON object per run
  (one per line for `watch`). Start PowerShell scripts with `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`
  and keep the file ASCII (write `[char]0x00B7` for a middle dot) so Windows PowerShell 5.1 reads it too.
- Read options from `$env:TILE_SETTINGS_JSON | ConvertFrom-Json`; never hard-code what a field can change.
- The title and subtitle are short: a tile is 132 DIP wide unless `"width"` says otherwise (up to 640), and users can
  resize it in edit mode unless `tile.json` says `"resizable": false` (for a layout that only works at one width);
  `"minWidth"`/`"maxWidth"` bound the range instead, for a layout that needs some room.
  Put the most important part first; the end is cut off.
- Failures: let the script exit non-zero with a short one-line message on stderr; the app shows the error look and
  the message. Do not print an error as a normal tile. A state that is normal, such as "not connected" or "no
  meetings today", is a normal tile, not a failure.
- Every popup needs a `title`, also in its empty or "not connected" form.
- Output of Windows tools (`netsh`, `ipconfig`, `systeminfo`) is translated on non-English Windows: parse
  PowerShell objects, CIM classes or a tool's machine-readable output (`--json`, `/fo csv`) instead of English labels.
- `click`, `flyout` and `action:` runs must finish within 10 s; do slow work (web requests) in `interval` and cache
  it in a file in the kind folder (writes there do not restart the tile).
- Secrets (tokens, API keys): read them from where the user already keeps them (an environment variable, the
  credential the tool already stores); never write them into `tile.json`, the script or a log.

## 3. Check it, then install it

Run the preview from the repository root. It loads `tile.json` as the app does, runs the script through the real
script connector, opens the popup, lists every warning (unknown fields, bad values, script errors) and renders the
tile to a PNG:

```
dotnet run --project tools/screenshots -- --preview my-tiles/<id>
```

What it prints and returns:

- Exit code 0: the tile drew and nothing was wrong. It prints the title, subtitle and popup, and
  `Picture: %TEMP%\tile-preview-<id>.png` (or the path given after the folder).
- Exit code 1 with `Warning:` lines: it drew, but the app would log those warnings (a typo such as `tittle`, a value
  of the wrong type). Fix them.
- Exit code 1 with `Error look:` or `tile.json:` and no picture: the script failed or `tile.json` is broken; the
  message is what the user would see.

Run it until it exits 0, then look at the picture (open the PNG with your image-reading tool) and check that the text
fits and reads well.

When the data source is not available here (no Wi-Fi, an offline service, missing hardware), preview the failure
anyway to see that its message is clear, and check the drawing with sample input: environment variables set for the
preview command reach the script, so a script may read a sample file when a variable such as `TILE_SAMPLE` names
one. Keep such a branch tiny, and say that it is only for testing.

The preview opens the popup but does not press its buttons. Run each `action:` hook directly, from PowerShell:

```powershell
$env:TILE_HOOK = 'action'; $env:TILE_ACTION = '<id>'
$env:TILE_INPUT = '<text>'   # only for an input action: the text typed in the popup
$env:TILE_SETTINGS_JSON = (Get-Content my-tiles\<id>\tile.json -Raw | ConvertFrom-Json).settings | ConvertTo-Json -Depth 10
powershell -NoProfile -ExecutionPolicy Bypass -File my-tiles\<id>\<script>.ps1
```

Install it by copying the folder into the data folder. Do it yourself unless you are in the Claude desktop app, in
which case give the user this command to run:

```
powershell -NoProfile -Command "Copy-Item -Recurse -Force my-tiles\<id> $env:LOCALAPPDATA\TaskbarTiles\tiles\"
```

Then tell the user to add it: the **+** button at the end of their tiles, in the folder named by `group`. Editing
the files in the data folder later restarts the tile by itself.

## 4. Bundled: ship it with the app

Only when the user wants it in the repository for everyone:

1. Move the folder to `tiles/<id>/`. The host project copies everything under `tiles/` into the build.
2. `tools/install.ps1`: add the id to `$bundledKinds` (install replaces bundled kinds on every run), its
   `tile.json` (and DLL, if any) to the files checked in `foreach ($required in ...)`, and the id to the kind lists
   in the script's header comment.
3. `tests/TaskbarTiles.Tests/BundledKindsTests.cs`: add the id to the expected list.
4. Docs: a short section for it in `docs/configuration.md` (after the other bundled kinds), a row in the README's
   "Tiles" table, and a sample state in `tools/screenshots/Program.cs`; then run
   `dotnet run --project tools/screenshots` to draw `docs/images/tile-<id>.png`.
5. `dotnet test --disable-build-servers` must pass before you commit.

## 5. A DLL tile

A DLL kind is a class library with a connector:

- Copy the shape of `src/TaskbarTiles.Plugins.Clipboard` or `src/TaskbarTiles.Plugins.SystemMonitor`: a project
  referencing `src/TaskbarTiles.Contracts` with `Private="false"` (the host provides it), a connector class marked
  `[TileConnector("<connector-id>", Width = 200)]` implementing `ITileConnector`, and the logic that turns data into
  a `TileState` and `FlyoutState` in a pure static class (`ClipboardView`, `SystemView`) so it can be unit-tested.
- A connector that draws to fit the tile (a `TileState.Canvas`) reads `ctx.Width` (DIP) and redraws for the new
  width on `ctx.Resized`, as `src/TaskbarTiles.Plugins.Scenes/SceneConnector.cs` does.
- Call `ctx.Update` from any thread; it is cheap and only the latest state is drawn. Throw from connector methods
  only for fatal failures (the host restarts the connector with backoff); use `ctx.ReportError` or `ctx.Log.Warn`
  for the rest. Dispose everything in `DisposeAsync`.
- `tile.json` names the connector id in `"connector"`.
- Bundled: add the project to `TaskbarTiles.sln`, and a `ProjectReference` plus `PluginProject ... Kind="<id>"` pair
  to both `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj` and `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`;
  the build copies the DLL into `tiles\<id>\`. Write tests for the view class in `tests/TaskbarTiles.Tests`, then do
  section 4.
- Personal: `dotnet build -c Release --disable-build-servers` the project and copy its DLL next to `tile.json` in the
  kind folder. The app loads DLLs only when it starts: after installing or changing one, the user exits Taskbar
  Tiles (right-click a tile › **More** › **Exit Taskbar Tiles**) and starts it again.

The preview runs script kinds only; check a DLL kind with its tests, then in the app.

## Done means

- The preview runs clean (script kinds) or the tests pass (DLL kinds), and you looked at the picture.
- The user knows where the folder is, how to add the tile with **+**, and which settings it has.
- Always pass `--disable-build-servers` to `dotnet` in this repository.
