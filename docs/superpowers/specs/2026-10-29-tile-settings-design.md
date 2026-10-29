# Tile settings dialog — design

Date: 2026-10-29. Replaces "Generated settings dialog" in the v1 out-of-scope list.

## Goal

Change a tile's settings (ping's host, media's app, battery's interval) without editing config.json: right-click the tile → **Settings…**.

## Decisions (user)

- The dialog edits only the tile's own `"settings"` in config.json. Layout (pin, stacks, sides) stays in edit mode.
- Fields come only from a `"fields"` list in the kind's tile.json. A kind without one has no **Settings…** entry; nothing is guessed from the defaults.

## Field list

```jsonc
"fields": [
  { "key": "host", "label": "Host", "type": "text", "help": "Name or IP address to ping" },
  { "key": "interval", "label": "Read every", "type": "duration", "min": "10s", "max": "1h" },
  { "key": "count", "label": "Pings", "type": "number", "min": 1, "max": 10 },
  { "key": "loud", "label": "Sound", "type": "toggle" },
  { "key": "mode", "label": "Mode", "type": "choice", "choices": ["fast", "slow"] }
]
```

- `key` (required): a top-level settings key, never `hooks`, unique in the list. `label` (required). `help` (optional).
- `type` (required): `text` (a string), `number` (a JSON number, optional `min`/`max` numbers), `toggle` (true/false), `choice` (one of `choices`, a non-empty list of strings), `duration` (a string such as `"60s"` readable by `Duration.TryParse`, optional `min`/`max` durations).
- A field's default is the kind's `settings[key]` (may be missing).
- A bad list makes the kind broken, like a bad `"width"`: `tiles\ping\tile.json: field 2 needs a "label".` and so on.
- Bundled kinds: ping **Host** (text); media **App** (text, help: the app whose media controls it follows, matched by name); battery **Read every** (duration 10s–1h). Uptime and claude have none.

## Dialog

- **Settings…** heads the tile menu when the kind declares fields; on a stack it is for the member shown.
- It opens in the popup frame above the tile (like the tile's own popup), titled with the kind's name and the tile id below. Clicking elsewhere closes it without saving, like other popups.
- One row per field: label, input, help line, error line. Text, number and duration use a text box holding the tile's current value; when empty, the kind's default shows as a dim placeholder and the default applies. Toggle is a check box, choice a drop-down, both showing the current value.
- A value that does not check out shows its error under the field and disables **Save**: `Enter a number from 1 to 10.`, `Enter a duration from 10s to 1h, e.g. 30s.`
- Buttons: **Reset to defaults** (fills every input with the default), **Save**. Esc closes without saving.

## Saving

- Writes `tiles.<id>.settings` through the config writer (the same path as edit mode: `config.json.bak`, comments lost). Other keys of the tile entry and settings keys the dialog does not know are kept.
- A field whose value equals the kind's default, or is empty, has its key removed, so the tile follows later changes to the default. Settings left empty are removed from the tile entry.
- The tile restarts with the new settings (its fingerprint changes). If config.json has a mistake, nothing is saved and the usual notice appears.

## Units

- `Plugins/TileFields.cs`: `TileField`, `FieldType`, parsing the list (`TileFields.Parse`) and checking one input (`TileFields.Check(field, input) → (JsonNode? value, string? error)`, null value meaning "use the default").
- `TileJson`/`TileKind` gain `Fields`.
- `Config/ConfigJson.SetTileSettings(root, id, values, fields, defaults)`: the merge above.
- `ILayoutEditor.SettingsFor(tileId) → TileSettingsForm?` and `SaveSettings(tileId, values) → ConfigError?`, implemented by HostController.
- `UI/SettingsPanel.cs`: builds the panel; TileBar shows it with `FlyoutWindow.ShowPanel`.
- `TileMenu`: `TileCommand.Settings` when `SlotMenuState.HasSettings`.

## Testing

- `TileFields.Parse`: every type, every error. `TileFields.Check`: every type, empty input, bounds.
- `SetTileSettings`: default removes, other keys kept, empty settings removed.
- HostController: `SettingsFor` null for kinds without fields; `SaveSettings` writes config and restarts the tile.
- `TileMenu`: **Settings…** only with fields.
