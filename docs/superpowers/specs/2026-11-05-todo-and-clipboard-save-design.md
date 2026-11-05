# TODO tile, Clipboard save and Linear tile: design

Three additions:

1. A TODO tile kept in a Markdown file.
2. A **Save** button in the Clipboard peek popup that writes the clipboard to a file.
3. A Linear tile listing your issues in In Progress and Todo.

The TODO tile needs to add text from a popup and to stay open while you tick items. That takes three new popup features, which the host gains for every tile, script or DLL.

## 1. Popup features (host, Contracts, script connector)

### Text box: `input`

Popup JSON gains `"input": { "placeholder": "Add a task", "action": "add" }`.

- The popup shows a single-line text box above the rows card. The placeholder text is shown while the box is empty.
- The box has focus when the popup opens.
- Enter runs the action with the box's text, trimmed. Empty text does nothing. The box is cleared after the action runs.
- Script tiles: the `action:<id>` hook runs with the text in the environment variable `TILE_INPUT`. `TILE_INPUT` is empty for every other run.
- Contracts:
  - `FlyoutState` gains `FlyoutInput? Input`, with `record FlyoutInput(string Action, string? Placeholder)`.
  - `ITileConnector` gains a default interface method, `Task OnInputAsync(string actionId, string text, CancellationToken ct)`. By default it calls `OnActionAsync(actionId, ct)`, so existing connectors still build.
- `action` is required; an `input` without it is dropped with a warning. Ids starting with `host:` are refused, the same rule as for actions.
- The text is at most 1000 characters. The box stops accepting input at that length.
- `"secret": true` (default false) draws the box as a password box: what you type shows as dots. `FlyoutInput` gains `bool Secret`.
- The host never writes the typed text to the log, for any input; it reaches the connector only.

### Popup stays open: `stayOpen`

Popup JSON gains `"stayOpen": true` (default false). `FlyoutState` gains `bool StayOpen`.

- With `stayOpen`, a row click, an action button or the text box runs its action without closing the popup.
- When the action finishes, the host asks for the popup again (the `flyout` hook for scripts, `GetFlyoutAsync` for DLLs) and rebuilds it in place. The popup keeps its position; its height may change.
- Host actions (ids starting with `host:`) still close the popup.
- While an action runs, the popup ignores further clicks and Enter. It does not queue them.
- If rebuilding fails (an error or no popup returned), the popup closes. The tile shows the error as it does today.
- An action hook that prints nothing still makes the tile refresh, as it does today.

### Checkbox rows: `check`

A popup row gains `"check": true | false`. `FlyoutRow` gains `bool? Check`.

- With `check` set, the row shows a checkbox in place of the colored dot: an empty box for false, a ticked box for true. The row's `icon` is then ignored.
- A row with `check: true` draws its text dim and struck through.
- The checkbox is only drawn; the click is the row's `action`, as for any other row.

### Docs

The new fields go in `docs/script-connectors.md`, in the Popup JSON table, and in the Environment table (`TILE_INPUT`).

## 2. TODO tile (`tiles/todo`)

A bundled script tile:

- **Picker:** group `Productivity`, name "To do", width 200.
- **Script:** `todo.ps1`, serving every hook.
- **Hooks:** `interval` (every 10 s), `flyout`, and action hooks for `add`, `toggle`, `clear` and `open`.

### Settings

| Key | Field | Default | Meaning |
|---|---|---|---|
| `file` | text, `"browse": "file"` | `%USERPROFILE%\Documents\todo.md` | The Markdown file. Environment variables are expanded. |
| `showDone` | bool | `true` | List done tasks in the popup. |

### File format

- **Task lines:** a task is a line matching `^\s*[-*] \[( |x|X)\] (.+)$`. Space means open; `x` or `X` means done.
- **Other lines:** headings, notes and blank lines are not tasks. They are never changed, and they are not shown.
- **Encoding:** the file is read as UTF-8, with or without a BOM, and written back as it was found: BOM kept if present, line endings (CRLF or LF) kept from the first line break, last-line newline kept as found.
- **Writes:** every write goes to a temporary file next to the original, which then replaces it, so a crash never leaves half a file.
- **Missing file:** the tile shows 0 tasks. The first **add** creates the file, and its folder if needed.

### Tile

- **Title:** "3 to do" (the number of open tasks), or "All done" when there are none.
- **Subtitle:**
  - With open tasks: the first open task in file order.
  - With no open tasks: "Nothing open". With no file yet: "Add a task".
- **Icon:** glyph `check`. Colour `accent` while tasks are open, `ok` when there are none.
- **Tooltip:** the file path, plus up to 5 open tasks.

### Popup

The popup has `stayOpen: true`.

- **Title:** "To do". **Subtitle:** the file name.
- **Input:** `{ "placeholder": "Add a task", "action": "add" }`.
- **Rows:**
  - Every open task, in file order, as `check: false`.
  - Then, if `showDone` is on, at most 10 done tasks, in file order, as `check: true`.
  - Each task row's action is `toggle:<line>:<hash>`. `<line>` is the 1-based line number. `<hash>` is the first 8 hex digits of the SHA-1 of the task's text, after the checkbox.
  - With no tasks at all, one plain row reads "No tasks yet".
- **Actions:** `clear`, labelled "Clear done", shown only when there are done tasks. `open`, labelled "Open file", primary.

### Actions

- **`add`:** `TILE_INPUT` becomes `- [ ] <text>`.
  - The line goes right after the last task line, or at the end of the file if it has none.
  - Line breaks in the text are replaced by spaces.
- **`toggle:<line>:<hash>`:**
  - If line `<line>` is a task whose text hashes to `<hash>`, it flips between `[ ]` and `[x]`.
  - Otherwise the first task line whose text hashes to `<hash>` flips (the file was edited after the popup opened).
  - If no line matches, nothing changes.
- **`clear`:** removes every done task line. Other lines stay.
- **`open`:** opens the file with its default app (`Start-Process`). If the file does not exist yet, it is created empty first.
- **Output:** every action prints nothing, so the host refreshes the tile and, with `stayOpen`, rebuilds the popup.

### Errors

- The file cannot be read (locked, no permission): the script exits 1 with "Cannot read <path>: <reason>".
- A write fails: the script exits 1 with "Cannot save <path>: <reason>".

### Bundling

Per the new-tile skill, section 4:

- `tools/install.ps1`: add the kind to `$bundledKinds` and add its required files.
- `BundledKindsTests`: add the kind to the expected list.
- `docs/configuration.md`: a "To do" section.
- README: a row in the tiles table.
- Screenshots: a sample in `tools/screenshots/Program.cs`, which draws `docs/images/tile-todo.png`.

## 3. Clipboard peek: Save

The bundled `clipboard` kind (DLL connector) gains a **Save** popup action.

- **What is saved:** whatever is on the clipboard when you press Save, read again at that moment, not the history.
  - An image becomes a PNG.
  - Text becomes a UTF-8 .txt without a BOM.
  - When it holds both, the image wins.
- **What is not saved:**
  - A copied file list, an empty clipboard, and a copy marked private (excluded from clipboard history) are not saved.
  - The popup's last row then reads "Nothing to save", and that row has no action.
- **Folder:** new setting `saveFolder`, a text field with `"browse": "folder"`. Default `%USERPROFILE%\Pictures\Clipboard`. Environment variables are expanded, and the folder is created when needed.
- **Name:** `clipboard-yyyy-MM-dd-HHmmss.png` (or `.txt`), in local time. If the name is taken, `-2`, `-3` and so on are added before the extension.
- **After a save:**
  - The connector remembers the saved path, in memory only.
  - The popup's last row reads "Saved <name>". Clicking it opens Explorer with the file selected (`explorer.exe /select,"<path>"`).
  - A failed save shows "Could not save: <reason>" there, and is logged without the clipboard's content.
- **Pure helper:** naming, and the choice of format from what the clipboard holds, live in a pure helper class (`ClipboardSave`), so they can be unit-tested.
- **Docs:** the `docs/configuration.md` Clipboard peek section changes "Copies stay in memory only, never on disk" to say a copy reaches disk only when you press Save, in the folder you set. It also documents `saveFolder`.

## 4. Linear tile (`tiles/linear`)

A bundled script tile:

- **Picker:** group `Productivity`, name "Linear", width 200.
- **Script:** `linear.ps1`, serving every hook.
- **Hooks:** `interval` (every `$refresh`), `flyout`, and action hooks for `open`, `setkey` and `forget`.

### Settings

| Key | Field | Default | Meaning |
|---|---|---|---|
| `refresh` | duration, 1m to 30m | `2m` | How often the issues are fetched. |

### Which issues

Issues assigned to you whose state type is `started` (In Progress) or `unstarted` (Todo), any team, at most 50.

- **Order:** started before unstarted; then by priority (Urgent 1, High 2, Medium 3, Low 4, No priority last); then most recently updated first.

### API key

- **Where:** Windows Credential Manager, a generic credential with target `TaskbarTiles-Linear` (user name `api`), read and written through Win32 `CredReadW`, `CredWriteW` and `CredDeleteW` from PowerShell.
- **Use:** sent only as the `Authorization` header to `https://api.linear.app/graphql`. Never printed, logged, cached, or put in an error message.
- **Setting it from the popup:** with no key, the popup has a secret input. Enter runs `action:setkey` with the key in `TILE_INPUT`:
  - The script fetches the issues with the new key first.
  - Accepted: the key is stored, the issues are cached, and the popup rebuilds (`stayOpen`) into the issue list.
  - Rejected or unreachable: nothing is stored. The rebuilt popup's subtitle says "Key rejected by Linear", or "Could not reach Linear: <reason>".
  - That message is kept for the next `flyout` run in a small note file in `%TEMP%` (the message only, never the key), which that run deletes.
- **By hand:** `cmdkey /generic:TaskbarTiles-Linear /user:api /pass:<key>` stores the same credential; documented as an alternative.
- **Forget key:** removes the credential and the cache.

### Tile

- **No key:** title "Linear", subtitle "Click to set up", icon `activity` in `neutral`. This is a normal tile, not an error.
- **Key rejected** (on a fetch with the stored key): title "Linear", subtitle "Key rejected", the same icon; the popup is the setup popup with subtitle "Linear rejected the saved key".
- **With issues:**
  - Title: "2 in progress"; with none in progress, "3 to do"; with none at all, "All clear".
  - Subtitle: the first issue in order as `ENG-142 Fix login redirect`; with none, "Nothing assigned".
  - Icon: glyph `activity`, colour `accent` with issues, `ok` without.
  - Tooltip: "2 in progress · 3 to do", then up to 5 issues; plus "Stale: <reason>" when showing the cache after a failed fetch.

### Popup

- **Setup popup** (no key, or key rejected): `stayOpen: true`.
  - Title "Linear". Subtitle: the note message if one is waiting, else "Paste your personal API key".
  - Input: `{ "placeholder": "Personal API key", "action": "setkey", "secret": true }`.
  - One plain row: "Create one in Linear: Settings › Security & access › Personal API keys".
  - Action `open:https://linear.app/settings/account/security`, labelled "Get a key", primary.
- **Issue popup:**
  - Title "Linear". Subtitle "2 in progress · 3 to do".
  - Rows: up to 15 issues in order. Text `ENG-142 Fix login redirect`, value the state name, icon dot `accent` for started and `neutral` for unstarted. Action `open:<issue url>`.
  - With no issues, one plain row: "Nothing in In Progress or Todo".
  - Actions: `forget`, labelled "Forget key"; `open:https://linear.app/<urlKey>/my-issues/assigned`, labelled "Open Linear", primary.

### Actions

- **`open:<url>`:** runs through the action prefix fallback. Opens the URL with `Start-Process` only when it starts with `https://linear.app/`; anything else exits 1 with "Not a Linear link". Prints nothing.
- **`setkey`:** as above. Empty input does nothing. Prints nothing.
- **`forget`:** as above. Prints nothing.

### Data and errors

- **Request:** one GraphQL POST: `organization { urlKey }` and `viewer.assignedIssues(first: 50, filter: state type in [started, unstarted])` with `identifier`, `title`, `url`, `priority`, `updatedAt`, `state { name type }`. Timeout 8 s.
- **Cache:** `%TEMP%\TaskbarTiles-linear-<tileId>.json` holds the last answer and its time. `interval` always fetches. `flyout` uses the cache while it is under 2 minutes old, and fetches otherwise.
- **Rejected key:** HTTP 401, or a GraphQL error with code `AUTHENTICATION_ERROR`.
- **Other failures** (network, rate limit, other GraphQL errors): with a cache, show it as stale; with none, exit 1 with "Could not reach Linear: <reason>" or "Linear: <GraphQL message>".
- **Test seams**, both tiny:
  - `TILE_LINEAR_RESPONSE` names a file read instead of calling the API. With it set, the cache is neither read nor written.
  - `TILE_LINEAR_KEYFILE` names a file used instead of Credential Manager. The note file goes next to it.

### Bundling

As for the To do tile: `tools/install.ps1`, `BundledKindsTests`, a "Linear" section in `docs/configuration.md`, a README row, and a screenshot sample drawing `docs/images/tile-linear.png`.

## Testing

**Unit tests:**

- Script output parsing for `input`, `stayOpen` and `check`, including the warnings for a missing `action` and a `host:` id.
- `ClipboardSave`: names, collisions, and the choice of format.
- `todo.ps1`, run through the script connector on temporary files:
  - Count and subtitle.
  - Add to an empty or missing file, and after the last task.
  - Toggle by line, by hash after an edit, and a hash that no longer exists.
  - Clear done.
  - Headings and notes kept; CRLF and LF kept; a BOM kept.
  - Popup rows, their `check` values, and the done limit.
- `secret` input parsing.
- `linear.ps1`, with both test seams:
  - Title, subtitle and issue order.
  - Popup rows and their `open:` links.
  - No key: setup tile and setup popup.
  - `setkey` stores an accepted key, refuses a rejected one and shows why, and never prints the key.
  - `forget` removes the key.
  - `open:` refuses a link outside Linear.
  - A rejected stored key shows setup; another error without a cache fails with the message.

**Checked by hand in the app:**

- Typing in the text box and pressing Enter.
- The popup staying open and rebuilding.
- The checkbox look.
- The Save button: an image and a text copy.
- The secret input showing dots.
- Linear: pasting a real key, the issue list, opening an issue, Forget key.

## Out of scope

- Due dates, priorities, reordering and subtasks.
- More than one file per tile; add a second To do tile instead.
- Sync with To Do services.
- A live file watcher. The 10 s interval and the refresh after each action are enough.
- Saving copied file lists, or items from the history.
- Linear: changing an issue's state, filters, notifications, OAuth sign-in.
