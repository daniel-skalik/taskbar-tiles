# To do Tile and Clipboard Save Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a To do tile backed by a Markdown file, and a Save button in Clipboard peek that writes the clipboard to a file. Both rest on three new popup features: a text box, a popup that stays open, and checkbox rows.

**Architecture:**
- The popup features go into the contracts (`FlyoutState`, `FlyoutRow`, `ITileConnector`), then the script output parser, then the script connector (`TILE_INPUT`, action-id prefix fallback), and finally the host's `FlyoutWindow`.
- The To do tile is a bundled PowerShell script tile.
- Clipboard Save adds a pure `ClipboardSave` helper, a raw clipboard read for saving, and PNG encoding through WinRT `Windows.Graphics.Imaging`. The plugin stays free of WPF and WinForms.

**Tech Stack:** .NET 9 WPF host, C# plugins, xUnit tests, PowerShell 5.1/7 scripts, WinRT imaging (`net9.0-windows10.0.19041.0`).

**Spec:** `docs/superpowers/specs/2026-11-05-todo-and-clipboard-save-design.md`

## Global Constraints

**Build and commits**
- Work on branch `feat/todo-clipboard-save`, which already exists and holds the spec.
- Always pass `--disable-build-servers` to `dotnet`.
- `dotnet test --disable-build-servers` must pass before every commit.
- Commit messages end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never install into `%LOCALAPPDATA%`, and never start or stop the installed `TaskbarTiles.exe`.

**Scripts**
- PowerShell files stay ASCII, so Windows PowerShell 5.1 reads them. Write `[char]0x00B7` for non-ASCII characters.
- Each script starts with `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`.

**Clipboard**
- Clipboard content is never written to the log.
- Clipboard content reaches disk only through the Save action, into the user's chosen folder.

**Popup ids and limits**
- Popup ids starting with `host:` are reserved for the host.
- The text box holds at most 1000 characters.
- The To do popup lists at most 10 done tasks.

**Glyphs and colours**
- Checkbox glyphs (Segoe Fluent Icons): empty ``, ticked ``.
- The To do tile uses glyph `check`: colour `accent` while tasks are open, `ok` when there are none.

**Defaults**
- To do file: `%USERPROFILE%\Documents\todo.md`, via `[Environment]::GetFolderPath('MyDocuments')`.
- Clipboard save folder: `Pictures\Clipboard`, via `Environment.SpecialFolder.MyPictures`.
- Saved file name: `clipboard-yyyy-MM-dd-HHmmss` plus `.png` or `.txt`. On a clash, `-2`, `-3`… go before the extension.

**Copy and style**
- Sentence case everywhere, no exclamation marks.
- Follow the existing code style: file-scoped namespaces, XML doc comments on public members.

---

## File Structure

**Contracts**
- Modify `src/TaskbarTiles.Contracts/FlyoutState.cs`:
  - Add `FlyoutInput`.
  - Add `FlyoutState.Input` and `FlyoutState.StayOpen`.
  - Add `FlyoutRow.Check`.
- Modify `src/TaskbarTiles.Contracts/ITileConnector.cs`: add a default `OnInputAsync`.

**Script plugin**
- Modify `src/TaskbarTiles.Plugins.Script/ScriptOutput.cs`: parse `input`, `stayOpen` and row `check`.
- Modify `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`: make `Action()` fall back from `toggle:5:ab12cd34` to the hook `action:toggle`.
- Modify `src/TaskbarTiles.Plugins.Script/ScriptConnector.cs`:
  - Implement `OnInputAsync`.
  - Set `TILE_INPUT` in the environment.

**Host**
- Modify `src/TaskbarTiles.Host/Runtime/TileRuntime.cs`: add `InvokeInputAsync`.
- Modify `src/TaskbarTiles.Host/UI/TileCommands.cs`: add `InvokeInputAsync`.
- Modify `src/TaskbarTiles.Host/UI/FlyoutWindow.cs`: add the text box, the rebuild for `stayOpen`, and checkbox rows.

**To do tile**
- Create `tiles/todo/tile.json` and `tiles/todo/todo.ps1`.

**Clipboard plugin**
- Create `src/TaskbarTiles.Plugins.Clipboard/ClipboardSave.cs`: pure helpers for the folder, the file name, BMP from DIB, the alpha fix, and choosing what to save.
- Create `src/TaskbarTiles.Plugins.Clipboard/PngEncoder.cs`: BMP to PNG through WinRT.
- Modify these clipboard files:
  - `ClipboardModel.cs`: `ClipSaveRead` and `IClipboardAccess.ReadForSave`.
  - `Win32Clipboard.cs`: implement `ReadForSave`.
  - `ClipboardView.cs`: the Save action, the result row, and `StayOpen`.
  - `ClipboardConnector.cs`: the save and show-saved actions.
- Modify `tiles/clipboard/tile.json`: add the `saveFolder` field.

**Tests**
- `tests/TaskbarTiles.Tests/ScriptOutputTests.cs`
- `ScriptSettingsTests.cs`
- `ScriptConnectorTests.cs`
- `BundledKindsTests.cs`
- `ClipboardTests.cs`

**Docs and tools**
- `docs/script-connectors.md` and `docs/configuration.md`
- `README.md`
- `tools/install.ps1`
- `tools/screenshots/Program.cs`

---

### Task 1: Popup data model and script parsing

**Files:**
- Modify: `src/TaskbarTiles.Contracts/FlyoutState.cs`
- Modify: `src/TaskbarTiles.Plugins.Script/ScriptOutput.cs:23-24` (FlyoutFields), `:70-74` (ReadFlyout), `:220-253` (ReadRows)
- Test: `tests/TaskbarTiles.Tests/ScriptOutputTests.cs`

**Interfaces:**
- Produces:
  - `record FlyoutInput(string Action, string? Placeholder)`
  - `FlyoutState(..., byte[]? Image = null, string? Tint = null, FlyoutInput? Input = null, bool StayOpen = false)`
  - `FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null, string? Action = null, bool? Check = null)`

- [ ] **Step 1: Write the failing tests** (append to `ScriptOutputTests`)

```csharp
    [Fact]
    public void Popup_input_stay_open_and_check_rows_are_read()
    {
        var r = ScriptOutput.ParseFlyout("""
            {
              "title": "To do", "stayOpen": true,
              "input": { "placeholder": "Add a task", "action": "add" },
              "rows": [ { "text": "Write spec", "action": "toggle:1:ab12cd34", "check": false },
                        { "text": "Old", "check": true }, "Plain" ]
            }
            """);

        Assert.Null(r.Error);
        Assert.Empty(r.Warnings);
        var f = r.Value!;
        Assert.True(f.StayOpen);
        Assert.Equal(new FlyoutInput("add", "Add a task"), f.Input);
        Assert.Equal(new bool?[] { false, true, null }, f.Rows.Select(x => x.Check));
    }

    [Fact]
    public void A_popup_without_the_new_fields_keeps_the_old_defaults()
    {
        var f = ScriptOutput.ParseFlyout("""{ "title": "T" }""").Value!;

        Assert.False(f.StayOpen);
        Assert.Null(f.Input);
    }

    [Theory]
    [InlineData("""{ "title": "T", "input": { "placeholder": "x" } }""", "\"input.action\" is missing")]
    [InlineData("""{ "title": "T", "input": { "action": "host:restart" } }""", "reserved")]
    [InlineData("""{ "title": "T", "input": "add" }""", "\"input\" must be an object")]
    [InlineData("""{ "title": "T", "stayOpen": "yes" }""", "\"stayOpen\" must be true or false")]
    [InlineData("""{ "title": "T", "rows": [ { "text": "a", "check": 1 } ] }""", "\"rows[0].check\" must be true or false")]
    public void Bad_new_popup_fields_are_dropped_with_a_warning(string json, string warning)
    {
        var r = ScriptOutput.ParseFlyout(json);

        Assert.Null(r.Error);
        Assert.Contains(r.Warnings, w => w.Contains(warning));
        Assert.Null(r.Value!.Input);
        Assert.False(r.Value.StayOpen);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~ScriptOutputTests"`
Expected: build error. `FlyoutInput`, `StayOpen` and `Check` do not exist yet.

- [ ] **Step 3: Implement the contracts.** Replace the whole of `src/TaskbarTiles.Contracts/FlyoutState.cs` with:

```csharp
namespace TaskbarTiles.Contracts;

/// <summary>
/// A popup line; with <paramref name="Action"/> it is clickable and sends that action id to the connector.
/// <paramref name="Check"/>: draw a checkbox (ticked when true, the text dim and struck through) instead of the icon dot.
/// </summary>
public sealed record FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null, string? Action = null, bool? Check = null);

public sealed record FlyoutAction(string Id, string Label, bool Primary = false);

/// <summary>A one-line text box in the popup; Enter sends the typed text to <see cref="ITileConnector.OnInputAsync"/> with <paramref name="Action"/>.</summary>
public sealed record FlyoutInput(string Action, string? Placeholder);

/// <summary>
/// A tile's popup. <paramref name="StayOpen"/>: a row, button or input action leaves the popup open and the host asks
/// for the popup again when the action is done, instead of closing it.
/// </summary>
public sealed record FlyoutState(
    string Title,
    string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows,
    IReadOnlyList<FlyoutAction> Actions,
    byte[]? Image = null,
    string? Tint = null, // "#RRGGBB" the popup is tinted with; null keeps the plain backdrop
    FlyoutInput? Input = null,
    bool StayOpen = false);
```

- [ ] **Step 4: Implement the parsing** in `ScriptOutput.cs`.

  1. In the `FlyoutFields` set, add `"input"` and `"stayOpen"`:

```csharp
    static readonly HashSet<string> FlyoutFields = new(StringComparer.Ordinal)
        { "title", "subtitle", "rows", "actions", "image", "tint", "input", "stayOpen" };
```

  2. Replace the `return new FlyoutState(...)` line in `ReadFlyout` with:

```csharp
        return new FlyoutState(title ?? "", Text(o, "subtitle", w), ReadRows(o, w), ReadActions(o, w), ReadImage(o, w), ReadTint(o, w),
            ReadInput(o, w), Bool(o, "stayOpen", w) ?? false);
```

  3. Add this method after `ReadTint`:

```csharp
    /// <summary>"input": { "action", "placeholder" }. Without a usable action the box is dropped: it could not send anything.</summary>
    static FlyoutInput? ReadInput(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("input", out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Object)
        {
            w.Add($"\"input\" must be an object; got {Kind(v)}.");
            return null;
        }
        var action = Text(v, "action", w, "input")?.Trim();
        if (string.IsNullOrEmpty(action))
        {
            w.Add("\"input.action\" is missing; the input box is dropped.");
            return null;
        }
        if (action.StartsWith("host:", StringComparison.Ordinal))
        {
            w.Add("\"input.action\" uses a reserved id; ids starting with \"host:\" are reserved for the host. The input box is dropped.");
            return null;
        }
        return new FlyoutInput(action, Text(v, "placeholder", w, "input"));
    }
```

  4. In `ReadRows`, replace the last `list.Add(new FlyoutRow(...))` with:

```csharp
            list.Add(new FlyoutRow(text, Text(r, "value", w, where), icon, string.IsNullOrWhiteSpace(action) ? null : action,
                Bool(r, "check", w, where)));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~ScriptOutputTests"`
Expected: PASS.

Then run the full suite: `dotnet test --disable-build-servers`
Expected: PASS. The new constructor parameters are optional, so existing callers still compile.

- [ ] **Step 6: Commit**

```bash
git add src/TaskbarTiles.Contracts/FlyoutState.cs src/TaskbarTiles.Plugins.Script/ScriptOutput.cs tests/TaskbarTiles.Tests/ScriptOutputTests.cs
git commit -m "feat(popup): input box, stayOpen and checkbox rows in the popup model" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Input plumbing: connector, runtime, script environment, action prefixes

**Files:**
- Modify: `src/TaskbarTiles.Contracts/ITileConnector.cs`
- Modify: `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs:43`
- Modify: `src/TaskbarTiles.Plugins.Script/ScriptConnector.cs:83-105`, `:214-251`
- Modify: `src/TaskbarTiles.Host/Runtime/TileRuntime.cs:226-239`
- Modify: `src/TaskbarTiles.Host/UI/TileCommands.cs:120-121`
- Test: `tests/TaskbarTiles.Tests/ScriptSettingsTests.cs`, `tests/TaskbarTiles.Tests/ScriptConnectorTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `Task ITileConnector.OnInputAsync(string actionId, string text, CancellationToken ct)`: a default interface method that calls `OnActionAsync`.
  - `Task TileRuntime.InvokeInputAsync(string actionId, string text)`
  - `Task TileCommands.InvokeInputAsync(TileRuntime runtime, string actionId, string text)`
  - The script environment variable `TILE_INPUT`: the text for an input action, `""` for every other run.
  - `ScriptSettings.Action("toggle:5:ab12cd34")` falls back to the hook `action:toggle` when there is no exact `action:toggle:5:ab12cd34` hook.

- [ ] **Step 1: Write the failing tests**

  1. Append to `ScriptSettingsTests`:

```csharp
    [Fact]
    public void An_action_id_with_a_colon_falls_back_to_the_hook_for_its_first_part()
    {
        var s = Parse("""{ "hooks": { "action:toggle": "t.ps1", "action:toggle:7": "seven.ps1" } }""");

        Assert.Equal("t.ps1", s.Action("toggle:5:ab12cd34")!.Run);
        Assert.Equal("seven.ps1", s.Action("toggle:7")!.Run); // an exact hook wins
        Assert.Null(s.Action("other:1"));
        Assert.Null(s.Action(":x"));
    }
```

  2. Append to `ScriptConnectorTests`:

```csharp
    [Fact]
    public async Task Input_runs_the_action_hook_with_the_text_in_TILE_INPUT()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "action:add": { "run": "add.ps1" } } }""");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        await ((ITileConnector)c).OnInputAsync("add", "Buy milk", CancellationToken.None);
        await c.OnActionAsync("add", CancellationToken.None);

        var calls = runner.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal("action", calls[0].Env["TILE_HOOK"]);
        Assert.Equal("add", calls[0].Env["TILE_ACTION"]);
        Assert.Equal("Buy milk", calls[0].Env["TILE_INPUT"]);
        Assert.Equal("", calls[1].Env["TILE_INPUT"]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~ScriptSettingsTests|FullyQualifiedName~ScriptConnectorTests"`
Expected: build error, because `OnInputAsync` does not exist.

- [ ] **Step 3: Add the default interface method.** In `ITileConnector.cs`, after `OnActionAsync`, add:

```csharp
    /// <summary>
    /// Text typed into the popup's input box (<see cref="FlyoutState.Input"/>), with that box's action id. The default
    /// drops the text and runs the action as a plain action, so connectors without input boxes need nothing.
    /// </summary>
    Task OnInputAsync(string actionId, string text, CancellationToken ct) => OnActionAsync(actionId, ct);
```

- [ ] **Step 4: Add the prefix fallback.** In `ScriptSettings.cs`, replace line 43 with:

```csharp
    /// <summary>
    /// The hook for an action id: "action:&lt;id&gt;", else the hook for the id's part before its first colon, so one
    /// "action:toggle" hook serves "toggle:5:ab12cd34" (TILE_ACTION still holds the whole id).
    /// </summary>
    public HookSpec? Action(string actionId)
    {
        if (Hooks.TryGetValue(ActionPrefix + actionId, out var exact)) return exact;
        var colon = actionId.IndexOf(':');
        return colon > 0 ? Hooks.GetValueOrDefault(ActionPrefix + actionId[..colon]) : null;
    }
```

- [ ] **Step 5: Wire up the script connector** in `ScriptConnector.cs`.

  1. Replace the tail of `OnActionAsync` (from `if (_settings.Action(actionId) is not { } action)` to the end of the method) with:

```csharp
        await RunActionAsync(actionId, null, ct);
    }

    public async Task OnInputAsync(string actionId, string text, CancellationToken ct)
    {
        if (_settings is null) return;
        await RunActionAsync(actionId, text, ct);
    }

    /// <summary>The action's hook; <paramref name="input"/> is the popup text box's text (TILE_INPUT), null for buttons and rows.</summary>
    async Task RunActionAsync(string actionId, string? input, CancellationToken ct)
    {
        if (_settings!.Action(actionId) is not { } action)
        {
            Ctx.Log.Warn($"No hook for action '{actionId}'; add \"action:{actionId}\" to settings.hooks.");
            return;
        }
        // An action that prints nothing usually changed what the status shows: refresh it.
        if (await Interactive("action", ct, t => RunTileHookAsync("action", action, actionId, t, input)) == HookRun.NoOutput)
            _interval?.Trigger(_cts.Token);
    }
```

  2. Add an optional `string? input = null` last parameter to three methods and thread it through:
     - `RunTileHookAsync(string hook, HookSpec spec, string? actionId, CancellationToken ct, string? input = null)` passes `input` on to `RunScriptAsync`.
     - `RunScriptAsync(string hook, HookSpec spec, string? actionId, TimeSpan timeout, CancellationToken ct, string? input = null)` passes `input` on to `EnvFor`.
     - `EnvFor(string hook, string? actionId, string? input = null)`.

     Existing callers stay as they are. In `EnvFor`, add one entry:

```csharp
        ["TILE_INPUT"] = input ?? "",
```

- [ ] **Step 6: Add the runtime and command methods.**

  1. In `TileRuntime.cs`, after `InvokeActionAsync`, add:

```csharp
    /// <summary>Text from a popup input box; like <see cref="InvokeActionAsync"/>, one call per action id at a time.</summary>
    public async Task InvokeInputAsync(string actionId, string text)
    {
        if (IsDisposed || Volatile.Read(ref _current) is not { } instance) return;
        if (!_actionsInFlight.TryAdd(actionId, 0)) return;
        try
        {
            await RunWithTimeout(instance, async ct => { await instance.Connector.OnInputAsync(actionId, text, ct); return true; });
        }
        catch (TimeoutException) { ShowNotResponding(); }
        catch (OperationCanceledException) when (instance.Cts.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex, instance.Generation); }
        finally { _actionsInFlight.TryRemove(actionId, out _); }
    }
```

  2. In `TileCommands.cs`, after `InvokeAsync`, add:

```csharp
    /// <summary>Text typed into a popup input box: always goes to the connector (input actions are never host ids).</summary>
    public Task InvokeInputAsync(TileRuntime runtime, string actionId, string text) => runtime.InvokeInputAsync(actionId, text);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/TaskbarTiles.Contracts/ITileConnector.cs src/TaskbarTiles.Plugins.Script src/TaskbarTiles.Host/Runtime/TileRuntime.cs src/TaskbarTiles.Host/UI/TileCommands.cs tests/TaskbarTiles.Tests/ScriptSettingsTests.cs tests/TaskbarTiles.Tests/ScriptConnectorTests.cs
git commit -m "feat(popup): send input text to connectors (TILE_INPUT) and let action ids share a hook by prefix" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: FlyoutWindow: text box, stay open and rebuild, checkbox rows; script docs

**Files:**
- Modify: `src/TaskbarTiles.Host/UI/FlyoutWindow.cs`. Change `ShowAbove` (lines 46-48), move `Build` (lines 121-214) into a new nested class, and change `Row` (lines 218-265).
- Modify: `docs/script-connectors.md`: the Hooks table row for `action:<id>`, the Environment table, and the Popup JSON table.

**Interfaces:**
- Consumes from Task 1: `FlyoutState.Input`, `FlyoutState.StayOpen`, `FlyoutRow.Check`, `FlyoutInput`.
- Consumes from Task 2: `TileCommands.InvokeInputAsync(TileRuntime, string, string)`.
- Unchanged:
  - The public signature of `FlyoutWindow.ShowAbove`, used by `TileView.xaml.cs:297`.
  - `FlyoutWindow.Text`, `ShowPanel`, `CloseOpen` and `HoldOpenWhile`.

This task is UI with no automated test. It is checked by building, running the full suite, and the manual check in Step 6.

- [ ] **Step 1: Add the usings and the glyph font.**

  1. At the top of `FlyoutWindow.cs`, add:

```csharp
using System.Windows.Input;
using System.Windows.Threading;
```

  2. Inside the class, next to `const double WidthDip = 320;`, add:

```csharp
    static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    const int MaxInputChars = 1000;
```

- [ ] **Step 2: Replace `ShowAbove` and `Build`.**

  1. Replace the `ShowAbove` method with:

```csharp
    /// <summary>A tile's popup; <paramref name="openSettings"/> (null when the tile has no settings) runs from the gear.</summary>
    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log, Action? openSettings = null)
    {
        var popup = new TilePopup(anchor, runtime, commands, log, openSettings);
        ShowPanel(anchor, popup.Build(state), light, log, WidthDip, tint: state.Tint);
        popup.Window = _open;
        popup.FocusInput();
    }

    /// <summary>
    /// Puts new content into an open popup's frame (and its tint), then places it again above its anchor: the height
    /// may have changed, and the popup grows upward from the taskbar.
    /// </summary>
    static void Replace(Window window, FrameworkElement anchor, UIElement content, string? tint)
    {
        if (window.Content is not Border { Child: Border inner }) return;
        inner.Child = content;
        inner.Background = TintBrush(tint) ?? Brushes.Transparent;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_open != window) return;
            try { Position(window, anchor); }
            catch (Exception) { /* keeps the old position; the next open places it again */ }
        });
    }
```

  2. Delete the old `static UIElement Build(FlyoutState state, TileRuntime runtime, TileCommands commands, ITileLog log, Action? openSettings)` method entirely.

  3. Add this nested class inside `FlyoutWindow`:

```csharp
    /// <summary>
    /// One tile popup: builds its content and runs its actions. Without <see cref="FlyoutState.StayOpen"/> an action
    /// closes the popup first, as always. With it the popup stays, ignores clicks and Enter until the action is done,
    /// then asks the tile for its popup again and shows that in place; it closes if there is none.
    /// </summary>
    sealed class TilePopup(FrameworkElement anchor, TileRuntime runtime, TileCommands commands, ITileLog log, Action? openSettings)
    {
        public Window? Window { get; set; }
        TextBox? _input;
        bool _busy;

        public void FocusInput()
        {
            var box = _input;
            box?.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
        }

        Task Send(string id, string? input) =>
            input is null ? commands.InvokeAsync(runtime, id) : commands.InvokeInputAsync(runtime, id, input);

        async void Run(FlyoutState state, string id, string? input)
        {
            if (!state.StayOpen || id.StartsWith("host:", StringComparison.Ordinal))
            {
                try { CloseOpen(); await Send(id, input); }
                catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
                return;
            }
            if (_busy) return;
            _busy = true;
            var window = Window;
            try
            {
                await Send(id, input);
                if (window is null || _open != window) return;
                var next = await runtime.GetFlyoutAsync();
                if (_open != window) return;
                if (next.Outcome == FlyoutOutcome.Popup && next.Flyout is { } fresh)
                {
                    Replace(window, anchor, Build(fresh), fresh.Tint);
                    FocusInput();
                }
                else CloseOpen();
            }
            catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
            finally { _busy = false; }
        }

        public UIElement Build(FlyoutState state)
        {
            _input = null;
            var panel = new StackPanel();

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (Theme.Decode(state.Image, 112) is { } img)
                header.Children.Add(new Border
                {
                    Width = 56, Height = 56, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0),
                    Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
                });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Text(state.Title, 15, FontWeights.SemiBold, "TileForeground");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            titles.Children.Add(title);
            if (!string.IsNullOrEmpty(state.Subtitle))
            {
                var subtitle = Text(state.Subtitle, 12, FontWeights.Normal, "TileDim");
                subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
                titles.Children.Add(subtitle);
            }
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);
            if (openSettings is not null)
            {
                var gear = new Button
                {
                    Content = "", // Settings
                    ToolTip = "Settings",
                    Style = (Style)Application.Current.FindResource("IconButton"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, -6, -6, 0),
                };
                gear.Click += (_, _) =>
                {
                    try { CloseOpen(); openSettings(); }
                    catch (Exception ex) { log.Error($"Opening the settings of tile '{runtime.TileId}' failed", ex); }
                };
                Grid.SetColumn(gear, 2);
                header.Children.Add(gear);
            }
            panel.Children.Add(header);

            if (state.Input is { } input)
            {
                // Tag is the FlyoutTextBox style's placeholder.
                var box = new TextBox
                {
                    Tag = input.Placeholder ?? "", MaxLength = MaxInputChars, Margin = new Thickness(0, 14, 0, 0),
                    Style = (Style)Application.Current.FindResource("FlyoutTextBox"),
                };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    e.Handled = true;
                    var text = box.Text.Trim();
                    if (text.Length == 0 || _busy) return; // keep the text while an action runs
                    box.Clear();
                    Run(state, input.Action, text);
                };
                _input = box;
                panel.Children.Add(box);
            }

            if (state.Rows.Count > 0)
            {
                var rows = new StackPanel();
                for (var i = 0; i < state.Rows.Count; i++) rows.Children.Add(Row(state.Rows[i], first: i == 0, id => Run(state, id, null)));
                var card = new Border
                {
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 14, 0, 0),
                    Child = rows,
                };
                card.SetResourceReference(Border.BackgroundProperty, "FlyoutCard");
                panel.Children.Add(card);
            }

            if (state.Actions.Count > 0)
            {
                // Buttons share the width: one button spans the popup.
                var actions = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
                for (var i = 0; i < state.Actions.Count; i++)
                {
                    var a = state.Actions[i];
                    var button = new Button
                    {
                        Content = a.Label,
                        Style = (Style)Application.Current.FindResource(a.Primary ? "FlyoutPrimaryButton" : "FlyoutButton"),
                        Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                        Padding = new Thickness(12, 6, 12, 6),
                    };
                    var id = a.Id;
                    // Host ids (Restart tile, Open log) become commands; the rest go to the connector.
                    button.Click += (_, _) => Run(state, id, null);
                    actions.Children.Add(button);
                }
                panel.Children.Add(actions);
            }

            return panel;
        }
    }
```

Note: copy the gear `Content` from the existing code exactly. It is a private-use Segoe glyph in the current source; `` is its escape. If the current source holds a different character, keep that one.

- [ ] **Step 3: Checkbox rows.** In `Row`:

  1. Replace the `if (row.Icon is { } icon) grid.Children.Add(new Border {...});` block with:

```csharp
        if (row.Check is { } check)
        {
            var box = new TextBlock
            {
                Text = check ? "" : "", // ticked / empty checkbox
                FontFamily = GlyphFont, FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            box.SetResourceReference(TextBlock.ForegroundProperty, check ? "TileDim" : "TileForeground");
            grid.Children.Add(box);
        }
        else if (row.Icon is { } icon)
            grid.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                Background = Theme.ColorFor(icon.Color), VerticalAlignment = VerticalAlignment.Center,
            });
```

  2. After `text.TextTrimming = TextTrimming.CharacterEllipsis;`, add:

```csharp
        if (row.Check == true)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");
        }
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build --disable-build-servers src/TaskbarTiles.Host` then `dotnet test --disable-build-servers`
Expected: build succeeds; tests PASS.

- [ ] **Step 5: Update the docs** in `docs/script-connectors.md`.

  1. In the Hooks table, change the `action:<id>` row's "When" cell to:

     > When a tile button or popup action with that `id` is pressed. `TILE_ACTION` holds the id. An id with a colon, such as `toggle:5:ab12cd34`, runs the `action:toggle` hook when there is no hook for the whole id, so one hook can serve ids that carry data.

  2. In the Environment table, add a row after `TILE_ACTION`:

     `| `TILE_INPUT` | For an action sent by the popup's input box: the text typed, trimmed (at most 1000 characters). Empty for every other run. |`

  3. In the Popup JSON table, add a sentence to the `rows` cell:

     > Add `"check": false` or `true` to draw a checkbox instead of the dot (ticked when true, with the text dim and struck through); the click is still the row's `action`.

  4. Add two rows after `tint`:

```markdown
| `input` | object | A text box above the rows: `{ "placeholder": "Add a task", "action": "add" }`. Enter runs the `action:<action>` hook with the text in `TILE_INPUT`; empty text does nothing. `action` is required and may not start with `host:`. |
| `stayOpen` | boolean | `true` keeps the popup open after a row, button or input action: when the action is done the `flyout` hook runs again and the popup is redrawn in place. Default `false`: an action closes the popup. |
```

- [ ] **Step 6: Manual check, with a temporary kind outside the repository** (nothing is committed for it). Have the user check this in the app later, or check it yourself if the app is running from a dev build. Do not touch the installed app.

The behaviour to confirm:
- The text box takes focus.
- Enter runs the action.
- The popup stays open and redraws.
- Checkbox rows look right.

If this can't be run here, write it in the task report as not checked.

- [ ] **Step 7: Commit**

```bash
git add src/TaskbarTiles.Host/UI/FlyoutWindow.cs docs/script-connectors.md
git commit -m "feat(popup): draw the input box and checkbox rows; keep stayOpen popups open and redraw them" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: To do tile

**Files:**
- Create: `tiles/todo/tile.json`, `tiles/todo/todo.ps1`
- Modify: `tools/install.ps1` (line 62 `$bundledKinds`, the required-files list at lines 92-105, and the header comment's kind list if it has one; `grep -n "claude-usage" tools/install.ps1` finds every list)
- Modify: `tests/TaskbarTiles.Tests/BundledKindsTests.cs`: the line-20 expected list, plus new tests
- Modify: `docs/configuration.md` (a new `### To do` section after `### Git repository`), `README.md` (the tiles table), and `tools/screenshots/Program.cs` (a sample, plus "12 tiles" becomes "13 tiles" on line 89)
- Create: `docs/images/tile-todo.png`, drawn by the screenshots tool

**Interfaces:**
- Consumes from Tasks 1 and 2:
  - The popup JSON fields `input`, `stayOpen` and `check`.
  - `TILE_INPUT`.
  - The action prefix fallback, which lets `action:toggle` serve `toggle:<line>:<hash>`.
- Produces: the bundled kind `todo`.

- [ ] **Step 1: Write the failing tests** (in `BundledKindsTests.cs`)

  1. Add `"todo"` to the line-20 expected list, keeping it in ordinal order: `..., "system", "todo", "uptime" }`.

  2. Add these usings at the top if they are missing: `using System.Security.Cryptography;`, `using System.Text;`.

  3. Add these tests:

```csharp
    /// <summary>Runs todo.ps1 on <paramref name="file"/>; action hooks get TILE_ACTION and TILE_INPUT.</summary>
    static Task<ScriptResult> RunTodo(string hook, string file, string action = "", string input = "", bool showDone = true) =>
        RunBundled("todo", "todo.ps1", hook,
            settingsJson: $$"""{ "file": "{{file.Replace('\\', '/')}}", "showDone": {{(showDone ? "true" : "false")}} }""",
            extraEnv: new Dictionary<string, string> { ["TILE_ACTION"] = action, ["TILE_INPUT"] = input });

    static string TodoHash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    static async Task TodoAction(string file, string action, string input = "")
    {
        var result = await RunTodo("action", file, action, input);
        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("", result.StdOut.Trim()); // prints nothing, so the host refreshes
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_counts_open_tasks_and_shows_the_first()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "# Work\r\n- [ ] Write spec\r\n- [x] Old\r\n* [ ] Ship\r\n");

        var outcome = HookResults.ForTile("interval", await RunTodo("interval", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("2 to do", outcome.Value!.Title);
        Assert.Equal("Write spec", outcome.Value.Subtitle);
        Assert.Equal(new TileIcon("check", TileColor.Accent), outcome.Value.Icon);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_without_a_file_asks_for_a_task()
    {
        using var dir = new TempDir();

        var outcome = HookResults.ForTile("interval", await RunTodo("interval", dir.File("none.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Equal("All done", outcome.Value!.Title);
        Assert.Equal("Add a task", outcome.Value.Subtitle);
        Assert.Equal(TileColor.Ok, outcome.Value.Icon!.Color);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_add_creates_the_file_and_its_folder()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.File("sub"), "deeper", "todo.md");

        await TodoAction(file, "add", "  Buy\nmilk  ");

        Assert.Equal("- [ ] Buy milk\r\n", File.ReadAllText(file));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_add_goes_after_the_last_task_and_keeps_LF()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "# A\n- [ ] One\n\nNotes\n");

        await TodoAction(dir.File("todo.md"), "add", "Two");

        Assert.Equal("# A\n- [ ] One\n- [ ] Two\n\nNotes\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_toggle_flips_the_line_named_by_number_and_hash()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "- [ ] One\n- [ ] Two\n");

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] One\n- [ ] Two\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_toggle_finds_the_task_by_text_after_an_edit_and_ignores_a_gone_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("todo.md"), "- [ ] Zero\n- [ ] One\n- [ ] Two\n"); // "Zero" was added after the popup opened

        await TodoAction(dir.File("todo.md"), $"toggle:2:{TodoHash("Two")}");
        Assert.Equal("- [ ] Zero\n- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));

        await TodoAction(dir.File("todo.md"), $"toggle:1:{TodoHash("Nope")}");
        Assert.Equal("- [ ] Zero\n- [ ] One\n- [x] Two\n", File.ReadAllText(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_clear_removes_done_tasks_and_keeps_the_rest_BOM_and_ending()
    {
        using var dir = new TempDir();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(dir.File("todo.md"), [.. bom, .. Encoding.UTF8.GetBytes("# H\r\n- [x] A\r\n- [ ] B\r\n- [X] C")]);

        await TodoAction(dir.File("todo.md"), "clear");

        Assert.Equal([.. bom, .. Encoding.UTF8.GetBytes("# H\r\n- [ ] B")], File.ReadAllBytes(dir.File("todo.md")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_popup_lists_open_tasks_then_up_to_ten_done()
    {
        using var dir = new TempDir();
        var lines = new List<string> { "- [ ] Open one" };
        for (var i = 1; i <= 12; i++) lines.Add($"- [x] Done {i}");
        File.WriteAllText(dir.File("todo.md"), string.Join("\n", lines) + "\n");

        var outcome = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        var f = outcome.Value!;
        Assert.Equal("To do", f.Title);
        Assert.Equal("todo.md", f.Subtitle);
        Assert.True(f.StayOpen);
        Assert.Equal(new FlyoutInput("add", "Add a task"), f.Input);
        Assert.Equal(11, f.Rows.Count);
        Assert.Equal(("Open one", false, $"toggle:1:{TodoHash("Open one")}"), (f.Rows[0].Text, f.Rows[0].Check!.Value, f.Rows[0].Action!));
        Assert.Equal(("Done 1", true), (f.Rows[1].Text, f.Rows[1].Check!.Value));
        Assert.Equal(new[] { "clear", "open" }, f.Actions.Select(a => a.Id));

        var hidden = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md"), showDone: false), TimeSpan.FromSeconds(30));
        Assert.Single(hidden.Value!.Rows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Todo_popup_with_no_tasks_says_so()
    {
        using var dir = new TempDir();

        var outcome = HookResults.ForFlyout("flyout", await RunTodo("flyout", dir.File("todo.md")), TimeSpan.FromSeconds(30));

        Assert.Equal("No tasks yet", Assert.Single(outcome.Value!.Rows).Text);
        Assert.Null(outcome.Value.Rows[0].Action);
        Assert.Equal(new[] { "open" }, outcome.Value.Actions.Select(a => a.Id));
    }
```

  `TempDir`, `RunBundled` and `HookResults` already exist in this test file or the test project. `ScriptResult` exposes `ExitCode`, `Stdout` and `Stderr`; check the names with `grep -n "record ScriptResult" -r src` and adapt if they differ.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~BundledKindsTests"`
Expected: FAIL. The kind list doesn't match, and `todo.ps1` doesn't exist.

- [ ] **Step 3: Create `tiles/todo/tile.json`**

```json
{
  "name": "To do",
  "description": "Tasks from a Markdown file: tick them off and add new ones from the popup",
  "group": "Productivity",
  "connector": "script",
  "width": 200,
  "fields": [
    { "key": "file", "label": "File", "type": "text", "browse": "file", "help": "A Markdown file with '- [ ] task' lines, for example in OneDrive or an Obsidian vault. Created on the first task you add." },
    { "key": "showDone", "label": "Show done tasks", "type": "toggle", "help": "List the last 10 done tasks under the open ones." }
  ],
  "settings": {
    "file": "%USERPROFILE%\\Documents\\todo.md",
    "showDone": true,
    "hooks": {
      "interval":      { "every": "10s", "run": "todo.ps1" },
      "flyout":        { "run": "todo.ps1" },
      "action:add":    { "run": "todo.ps1" },
      "action:toggle": { "run": "todo.ps1" },
      "action:clear":  { "run": "todo.ps1" },
      "action:open":   { "run": "todo.ps1" }
    }
  }
}
```

- [ ] **Step 4: Create `tiles/todo/todo.ps1`** (ASCII only)

```powershell
# Taskbar Tiles bundled kind "todo": tasks from a Markdown file ("- [ ] task" / "- [x] task" lines), ticked off and
# added from the popup. Hooks (tile.json): interval, flyout, action:add, action:toggle, action:clear, action:open.
# See docs/script-connectors.md. Replaced on every install: copy this folder under a new name before changing it.
# Settings: "file" (the Markdown file; default Documents\todo.md), "showDone" (list done tasks in the popup).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$file = if ($settings -and $settings.file) { [Environment]::ExpandEnvironmentVariables([string]$settings.file).Trim().Trim('"') } else { '' }
if (-not $file) { $file = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'todo.md' }
$file = [IO.Path]::GetFullPath($file)
$showDone = -not ($settings -and $settings.showDone -eq $false)
$taskPattern = '^(\s*[-*] \[)( |x|X)(\] )(.+)$'
$maxDone = 10
$utf8 = New-Object System.Text.UTF8Encoding $false

# The file as lines, plus what writing it back must keep: BOM, line ending, newline after the last line.
function Read-Todo {
    $doc = @{ Exists = $false; Lines = New-Object System.Collections.Generic.List[string]; Bom = $false; Eol = "`r`n"; FinalNewline = $true }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $doc }
    try { $bytes = [IO.File]::ReadAllBytes($file) } catch { [Console]::Error.WriteLine("Cannot read ${file}: $($_.Exception.Message)"); exit 1 }
    $doc.Exists = $true
    $doc.Bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($doc.Bom) { 3 } else { 0 }
    $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $first = $text.IndexOf("`n")
    if ($first -ge 0) { $doc.Eol = if ($first -gt 0 -and $text[$first - 1] -eq "`r") { "`r`n" } else { "`n" } }
    $doc.FinalNewline = $text.Length -eq 0 -or $text.EndsWith("`n")
    if ($text.EndsWith("`r`n")) { $text = $text.Substring(0, $text.Length - 2) } elseif ($text.EndsWith("`n")) { $text = $text.Substring(0, $text.Length - 1) }
    if ($text.Length -gt 0) { foreach ($line in ($text -split "`r?`n")) { $doc.Lines.Add($line) } }
    return $doc
}

# Writes to a temporary file next to the original, then swaps it in: a crash never leaves half a file.
function Write-Todo($doc) {
    $text = [string]::Join($doc.Eol, $doc.Lines)
    if ($doc.FinalNewline -and $doc.Lines.Count -gt 0) { $text += $doc.Eol }
    $body = $utf8.GetBytes($text)
    $bytes = if ($doc.Bom) { [byte[]](0xEF, 0xBB, 0xBF) + $body } else { $body }
    $folder = Split-Path -Parent $file
    $temp = Join-Path $folder (".todo-" + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        if (-not (Test-Path -LiteralPath $folder)) { $null = New-Item -ItemType Directory -Path $folder -Force }
        [IO.File]::WriteAllBytes($temp, [byte[]]$bytes)
        if (Test-Path -LiteralPath $file) { [IO.File]::Replace($temp, $file, $null) } else { [IO.File]::Move($temp, $file) }
    } catch {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        [Console]::Error.WriteLine("Cannot save ${file}: $($_.Exception.Message)"); exit 1
    }
}

# The first 8 hex digits of the SHA-1 of a task's text: names a task in an action id even after lines move.
function Get-Hash([string]$text) {
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return -join ($sha.ComputeHash($utf8.GetBytes($text))[0..3] | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

# Every task line: its 1-based line number, done or not, and its text after the checkbox.
function Get-Tasks($doc) {
    $tasks = @()
    for ($i = 0; $i -lt $doc.Lines.Count; $i++) {
        if ($doc.Lines[$i] -match $taskPattern) { $tasks += @{ Line = $i + 1; Done = $Matches[2] -ne ' '; Text = $Matches[4] } }
    }
    return ,$tasks
}

$doc = Read-Todo

if ($env:TILE_HOOK -eq 'action') {
    $action = [string]$env:TILE_ACTION
    if ($action -eq 'add') {
        $text = (([string]$env:TILE_INPUT) -replace '[\r\n]+', ' ').Trim()
        if (-not $text) { exit 0 }
        $tasks = Get-Tasks $doc
        $at = if ($tasks.Count) { $tasks[-1].Line } else { $doc.Lines.Count } # after the last task, else at the end
        $doc.Lines.Insert($at, "- [ ] $text")
        Write-Todo $doc
    } elseif ($action -match '^toggle:(\d+):([0-9a-f]{8})$') {
        $line = [int]$Matches[1]; $hash = $Matches[2]
        $tasks = Get-Tasks $doc
        $task = $tasks | Where-Object { $_.Line -eq $line -and (Get-Hash $_.Text) -eq $hash } | Select-Object -First 1
        if (-not $task) { $task = $tasks | Where-Object { (Get-Hash $_.Text) -eq $hash } | Select-Object -First 1 } # the file changed since the popup opened
        if (-not $task) { exit 0 }
        $null = $doc.Lines[$task.Line - 1] -match $taskPattern
        $mark = if ($task.Done) { ' ' } else { 'x' }
        $doc.Lines[$task.Line - 1] = $Matches[1] + $mark + $Matches[3] + $Matches[4]
        Write-Todo $doc
    } elseif ($action -eq 'clear') {
        $kept = New-Object System.Collections.Generic.List[string]
        $removed = 0
        foreach ($l in $doc.Lines) { if ($l -match $taskPattern -and $Matches[2] -ne ' ') { $removed++ } else { $kept.Add($l) } }
        if ($removed -eq 0) { exit 0 }
        $doc.Lines = $kept
        Write-Todo $doc
    } elseif ($action -eq 'open') {
        if (-not $doc.Exists) { Write-Todo $doc }
        Start-Process -FilePath $file
    }
    exit 0
}

$tasks = Get-Tasks $doc
$open = @($tasks | Where-Object { -not $_.Done })
$done = @($tasks | Where-Object { $_.Done })

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    foreach ($t in $open) { $rows += @{ text = $t.Text; check = $false; action = "toggle:$($t.Line):$(Get-Hash $t.Text)" } }
    if ($showDone) { foreach ($t in ($done | Select-Object -First $maxDone)) { $rows += @{ text = $t.Text; check = $true; action = "toggle:$($t.Line):$(Get-Hash $t.Text)" } } }
    if (-not $tasks.Count) { $rows += @{ text = 'No tasks yet' } }
    $actions = @()
    if ($done.Count) { $actions += @{ id = 'clear'; label = 'Clear done' } }
    $actions += @{ id = 'open'; label = 'Open file'; primary = $true }
    @{
        title    = 'To do'
        subtitle = (Split-Path -Leaf $file)
        stayOpen = $true
        input    = @{ placeholder = 'Add a task'; action = 'add' }
        rows     = $rows
        actions  = $actions
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval prints the tile.
$subtitle = if ($open.Count) { $open[0].Text } elseif ($doc.Exists -and $tasks.Count) { 'Nothing open' } else { 'Add a task' }
$tip = @($file) + @($open | Select-Object -First 5 | ForEach-Object { "- $($_.Text)" })
@{
    title    = $(if ($open.Count) { "$($open.Count) to do" } else { 'All done' })
    subtitle = $subtitle
    icon     = @{ glyph = 'check'; color = $(if ($open.Count) { 'accent' } else { 'ok' }) }
    tooltip  = $tip -join "`n"
} | ConvertTo-Json -Depth 5 -Compress
```

Notes for the implementer:
- With no file, or a file that has no tasks, the subtitle reads "Add a task". The test `Todo_without_a_file_asks_for_a_task` expects that. "Nothing open" shows only when the file has tasks and all of them are done.
- PowerShell's `-match` sets `$Matches`. In the toggle branch, re-match the line right before rebuilding it, as written, because `Get-Hash` calls in between run no `-match` but `Where-Object` blocks might.
- `ConvertTo-Json` turns one-element arrays into objects under Windows PowerShell 5.1 when they come from a pipeline. `$rows` and `$actions` are built with `+=` on `@()` and nested in a hashtable, so they stay arrays. Verify with the flyout test that `actions` parses as an array when it holds only `open`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~BundledKindsTests"`
Expected: PASS.

If a test fails on PowerShell quirks (single-item arrays, `$Matches`), fix the script, not the test.

- [ ] **Step 6: Bundle it, document it, take a screenshot.**

  1. **`tools/install.ps1`:**
     - Line 62: add `'todo'` to `$bundledKinds` after `'git'`.
     - In the required list, after the git line, add: `(Join-Path $stage 'tiles\todo\tile.json'),`
     - If the header comment lists the kinds, add `todo` there too.
  2. **`tools/screenshots/Program.cs`:**
     - After the `git` sample line, add:

```csharp
        ("todo", 200, Fixed(Script("""{ "title": "3 to do", "subtitle": "Write the release notes", "icon": { "glyph": "check", "color": "accent" } }"""))),
```

     - On line 89, change `12 tiles` to `13 tiles`.
  3. **`README.md`:** after the Git repository row, add:

```markdown
| <img src="docs/images/tile-todo.png" alt="To do tile" height="48"> | **To do**<br>Tasks from a Markdown file; tick them off and add new ones from the popup. |
```

  4. **`docs/configuration.md`:** after the `### Git repository` section, before `### Pixel scenes`, add:

```markdown
### To do

The bundled `todo` kind keeps your tasks in a plain Markdown file: every `- [ ] task` line is an open task and every `- [x] task` line a done one (`*` instead of `-` works too). The tile shows how many are open and the first of them. Click it for the list: type in the box and press Enter to add a task (it goes after the last task in the file), click a task to tick it off or back on, **Clear done** deletes the done lines, and **Open file** opens the file in its usual app. The popup stays open while you work through it.

- Setting `"file"` (also in **Settings…**, with a file picker) is the Markdown file; default `%USERPROFILE%\Documents\todo.md`, created with its folder on the first task you add. Put it in OneDrive, Dropbox or an Obsidian vault to have it elsewhere too.
- Setting `"showDone"` (default `true`) lists the last 10 done tasks under the open ones.
- Headings, notes and blank lines in the file are kept as they are and not shown. The file keeps its line endings and encoding (UTF-8, with or without BOM), and each change replaces it in one step.
- It reads the file every 10 seconds and right after every change, so edits made in another app show up on their own. For a second list, add a second To do tile with another file.
```

  5. Run `dotnet run --disable-build-servers --project tools/screenshots`. Then `git status`: keep only the new `docs/images/tile-todo.png`, and `git checkout -- <file>` any other `docs/images/*.png` the tool changed (the scene tiles re-render with random differences).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test --disable-build-servers`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add tiles/todo tools/install.ps1 tools/screenshots/Program.cs README.md docs/configuration.md docs/images/tile-todo.png tests/TaskbarTiles.Tests/BundledKindsTests.cs
git commit -m "feat(tiles): bundled To do tile backed by a Markdown file" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Clipboard peek: Save

**Files:**
- Create: `src/TaskbarTiles.Plugins.Clipboard/ClipboardSave.cs`, `src/TaskbarTiles.Plugins.Clipboard/PngEncoder.cs`
- Modify these clipboard files:
  - `src/TaskbarTiles.Plugins.Clipboard/ClipboardModel.cs`: add `ClipSaveRead` and `IClipboardAccess.ReadForSave`.
  - `src/TaskbarTiles.Plugins.Clipboard/Win32Clipboard.cs`
  - `src/TaskbarTiles.Plugins.Clipboard/ClipboardView.cs`
  - `src/TaskbarTiles.Plugins.Clipboard/ClipboardConnector.cs`
- Modify: `tiles/clipboard/tile.json`
- Modify: `docs/configuration.md`, the `### Clipboard peek` section
- Test: `tests/TaskbarTiles.Tests/ClipboardTests.cs`. Extend `FakeClipboard` and add tests.

**Interfaces:**
- Consumes from Task 1: `FlyoutState(..., StayOpen: true)`.
- Produces:
  - `readonly record struct ClipSaveRead(byte[]? Dib = null, string? Text = null, bool Excluded = false, bool Retry = false, bool Files = false)`
  - `ClipSaveRead IClipboardAccess.ReadForSave()`
  - `static class ClipboardSave`, with:
    - `SaveAction = "save"` and `ShowSavedAction = "show-saved"`
    - `string Folder(JsonElement settings)`
    - `string FileName(DateTime now, string extension, Func<string, bool> taken)`
    - `byte[]? BmpFromDib(byte[] dib)`
    - `void OpaqueIfNoAlpha(byte[] bgra)`
  - `static class PngEncoder` with `Task<byte[]> FromBmpAsync(byte[] bmp)`
  - `ClipboardView.Flyout(IReadOnlyList<ClipEntry> history, FlyoutRow? saveRow = null)`, which now returns `StayOpen: true` and the actions `[save, clear]`.

Resolution of a spec detail: the spec says the popup's last row shows the save result. That needs the popup to stay open after Save, so the Clipboard peek popup sets `StayOpen = true`. As a result, copying a recent item or pressing **Clear** also leaves the popup open, now updated. **Show in folder** (the "Saved …" row) opens Explorer, which takes the focus and closes the popup, as any popup closes on losing focus.

- [ ] **Step 1: Write the failing tests** (in `ClipboardTests.cs`)

  1. Extend `FakeClipboard`:

```csharp
        public ClipSaveRead ForSave { get; set; }
        public ClipSaveRead ReadForSave() { lock (_lock) return ForSave; }
```

  2. Add `using System.Text;` and `using System.Text.Json;` if they are missing, then add these tests:

```csharp
    [Fact]
    public void Save_names_carry_the_time_and_count_up_when_taken()
    {
        var now = new DateTime(2026, 11, 5, 14, 30, 12);
        var taken = new HashSet<string> { "clipboard-2026-11-05-143012.png", "clipboard-2026-11-05-143012-2.png" };

        Assert.Equal("clipboard-2026-11-05-143012.txt", ClipboardSave.FileName(now, ".txt", taken.Contains));
        Assert.Equal("clipboard-2026-11-05-143012-3.png", ClipboardSave.FileName(now, ".png", taken.Contains));
    }

    [Fact]
    public void Save_folder_comes_from_settings_with_variables_expanded_else_pictures()
    {
        using var doc = JsonDocument.Parse("""{ "saveFolder": " \"%TEMP%\\clips\" " }""");
        using var empty = JsonDocument.Parse("""{ "saveFolder": "" }""");

        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "clips"), ClipboardSave.Folder(doc.RootElement));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Clipboard"), ClipboardSave.Folder(empty.RootElement));
    }

    /// <summary>A 2×1 32-bit BI_RGB DIB: 40-byte header, then two pixels (B, G, R, A) with alpha 0, as most apps copy them.</summary>
    static byte[] TinyDib()
    {
        var dib = new byte[40 + 8];
        BitConverter.GetBytes(40).CopyTo(dib, 0);           // header size
        BitConverter.GetBytes(2).CopyTo(dib, 4);            // width
        BitConverter.GetBytes(1).CopyTo(dib, 8);            // height (bottom-up)
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);   // planes
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);  // bits per pixel
        new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }.CopyTo(dib, 40); // red, blue
        return dib;
    }

    [Fact]
    public void A_DIB_becomes_a_BMP_file_with_the_pixel_offset_after_header_masks_and_palette()
    {
        var bmp = ClipboardSave.BmpFromDib(TinyDib())!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(14 + 48, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(14 + 40, BitConverter.ToInt32(bmp, 10));

        var bitfields = TinyDib();
        BitConverter.GetBytes(3u).CopyTo(bitfields, 16); // BI_BITFIELDS: three masks follow a 40-byte header
        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(ClipboardSave.BmpFromDib(bitfields)!, 10));

        var paletted = TinyDib();
        BitConverter.GetBytes((ushort)8).CopyTo(paletted, 14); // 8 bpp, biClrUsed 0: a 256-entry palette
        Assert.Equal(14 + 40 + 256 * 4, BitConverter.ToInt32(ClipboardSave.BmpFromDib(paletted)!, 10));

        Assert.Null(ClipboardSave.BmpFromDib(new byte[10]));
    }

    [Fact]
    public void Pixels_with_no_alpha_at_all_become_opaque_and_real_alpha_stays()
    {
        var none = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        ClipboardSave.OpaqueIfNoAlpha(none);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, none);

        var some = new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 };
        ClipboardSave.OpaqueIfNoAlpha(some);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 }, some);
    }

    static async Task<(ClipboardConnector Connector, TestTileContext Ctx, FakeClipboard Clip)> SaveSetup(string folder)
    {
        var clip = new FakeClipboard();
        var ctx = new TestTileContext(settingsJson: $$"""{ "saveFolder": "{{folder.Replace('\\', '/')}}" }""");
        var c = new ClipboardConnector(clip, TimeSpan.FromHours(1), () => new DateTime(2026, 11, 5, 14, 30, 12));
        await c.StartAsync(ctx, CancellationToken.None);
        return (c, ctx, clip);
    }

    [Fact]
    public async Task Save_writes_text_as_UTF8_and_the_popup_says_where()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.File("out"), "clips"); // created on save
        var (c, _, clip) = await SaveSetup(folder);
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Text: "héllo");

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        var path = Path.Combine(folder, "clipboard-2026-11-05-143012.txt");
        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), File.ReadAllBytes(path)); // no BOM
        var popup = (await c.GetFlyoutAsync(CancellationToken.None))!;
        Assert.True(popup.StayOpen);
        Assert.Equal(new[] { ClipboardSave.SaveAction, ClipboardView.ClearAction }, popup.Actions.Select(a => a.Id));
        Assert.Equal(("Saved clipboard-2026-11-05-143012.txt", ClipboardSave.ShowSavedAction), (popup.Rows[^1].Text, popup.Rows[^1].Action!));
    }

    [Fact]
    public async Task Save_writes_an_image_as_PNG_when_there_is_also_text()
    {
        using var dir = new TempDir();
        var (c, _, clip) = await SaveSetup(dir.File("clips"));
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Dib: TinyDib(), Text: "also text");

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        var png = File.ReadAllBytes(Path.Combine(dir.File("clips"), "clipboard-2026-11-05-143012.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    [Theory]
    [InlineData(false, true)]  // a copied file list
    [InlineData(true, false)]  // marked private
    [InlineData(false, false)] // empty
    public async Task Nothing_to_save_writes_nothing(bool excluded, bool files)
    {
        using var dir = new TempDir();
        var (c, _, clip) = await SaveSetup(dir.File("clips"));
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Text: excluded ? "secret" : null, Excluded: excluded, Files: files);

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        Assert.False(Directory.Exists(dir.File("clips")));
        var last = (await c.GetFlyoutAsync(CancellationToken.None))!.Rows[^1];
        Assert.Equal("Nothing to save", last.Text);
        Assert.Null(last.Action);
    }
```

`TempDir` and `TestTileContext` already exist in the test project. If `TestTileContext` has no `settingsJson` parameter, use the pattern the other connector tests use.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~ClipboardTests"`
Expected: build error. `ClipSaveRead`, `ClipboardSave` and `ReadForSave` don't exist.

- [ ] **Step 3: Model.** In `ClipboardModel.cs`:

  1. After the `ClipRead` record, add:

```csharp
/// <summary>
/// The clipboard read for Save: raw DIB bytes and/or text as they are now. <paramref name="Files"/>: a copied file list
/// (not saved). <paramref name="Excluded"/> and <paramref name="Retry"/> as in <see cref="ClipRead"/>; nothing was read then.
/// </summary>
public readonly record struct ClipSaveRead(byte[]? Dib = null, string? Text = null, bool Excluded = false, bool Retry = false, bool Files = false);
```

  2. In `IClipboardAccess`, add:

```csharp
    /// <summary>What Save writes: the image as DIB bytes and the text, read now; never recorded in the history.</summary>
    ClipSaveRead ReadForSave();
```

- [ ] **Step 4: Win32.** In `Win32Clipboard.cs`, add (`MaxDibBytes` sits next to `MaxChars`):

```csharp
    const int MaxDibBytes = 256 * 1024 * 1024; // a bigger image is not saved rather than copied around

    public ClipSaveRead ReadForSave()
    {
        if (!Open()) return new ClipSaveRead(Retry: true);
        try
        {
            if (ClipboardPrivacy.IsExcluded(IsClipboardFormatAvailable(ExcludeId), ReadDword(CanIncludeId)))
                return new ClipSaveRead(Excluded: true);
            if (IsClipboardFormatAvailable(CfHdrop)) return new ClipSaveRead(Files: true);
            return new ClipSaveRead(ReadDibBytes(), ReadText()?.Text);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>CF_DIB as bytes (Windows makes it from CF_DIBV5 or CF_BITMAP when only those were copied); null when absent.</summary>
    static byte[]? ReadDibBytes()
    {
        if (!IsClipboardFormatAvailable(CfDib)) return null;
        var handle = GetClipboardData(CfDib);
        if (handle == 0) return null;
        var size = (long)GlobalSize(handle);
        if (size < 40 || size > MaxDibBytes) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }
```

- [ ] **Step 5: Create `ClipboardSave.cs`**

```csharp
using System.Text.Json;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>Where Save writes, the file name, and turning clipboard image bytes into a BMP file. Pure.</summary>
public static class ClipboardSave
{
    public const string SaveAction = "save";
    public const string ShowSavedAction = "show-saved";

    /// <summary>"saveFolder" from settings (environment variables expanded, quotes trimmed), else Pictures\Clipboard.</summary>
    public static string Folder(JsonElement settings)
    {
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("saveFolder", out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Trim('"') is { Length: > 0 } folder)
            return Environment.ExpandEnvironmentVariables(folder);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Clipboard");
    }

    /// <summary>clipboard-2026-11-05-143012.png, or -2, -3, … before the extension while <paramref name="taken"/> says the name is used.</summary>
    public static string FileName(DateTime now, string extension, Func<string, bool> taken)
    {
        var stem = $"clipboard-{now:yyyy-MM-dd-HHmmss}";
        var name = stem + extension;
        for (var n = 2; taken(name); n++) name = $"{stem}-{n}{extension}";
        return name;
    }

    /// <summary>
    /// A BMP file from a packed DIB (CF_DIB: header, masks, palette, pixels): the 14-byte file header in front, pointing
    /// past the header, the three BI_BITFIELDS masks a 40-byte header leaves out, and the palette. Null when too short.
    /// </summary>
    public static byte[]? BmpFromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colorsUsed = BitConverter.ToUInt32(dib, 32);
        var masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0; // BI_BITFIELDS, BI_ALPHABITFIELDS
        var palette = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
        var offset = 14 + headerSize + masks + (int)palette * 4;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    /// <summary>
    /// Most apps copy 32-bit images with every alpha byte 0, meaning "no alpha", not "invisible": then all pixels become
    /// opaque. An image with any alpha set is left as it is. <paramref name="bgra"/> is B, G, R, A per pixel.
    /// </summary>
    public static void OpaqueIfNoAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return;
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }
}
```

- [ ] **Step 6: Create `PngEncoder.cs`**

```csharp
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>BMP file bytes to PNG through Windows' own imaging (WinRT), so the plugin needs neither WPF nor WinForms.</summary>
public static class PngEncoder
{
    public static async Task<byte[]> FromBmpAsync(byte[] bmp)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bmp.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();
        ClipboardSave.OpaqueIfNoAlpha(bgra);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, decoder.PixelWidth, decoder.PixelHeight, 96, 96, bgra);
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
```

- [ ] **Step 7: View.** In `ClipboardView.cs`, replace `Flyout` with:

```csharp
    /// <summary>
    /// The recent copies, then <paramref name="saveRow"/> (the last Save's outcome) when there is one. Stays open, so
    /// Save's outcome, a copy moving up and Clear show in place.
    /// </summary>
    public static FlyoutState Flyout(IReadOnlyList<ClipEntry> history, FlyoutRow? saveRow = null)
    {
        var rows = history.Select(Row).ToList();
        if (saveRow is not null) rows.Add(saveRow);
        return new FlyoutState(
            "Clipboard",
            history.Count switch { 0 => "Nothing copied yet", 1 => "1 recent copy", var n => $"{n} recent copies" },
            rows,
            [new FlyoutAction(ClipboardSave.SaveAction, "Save"), new FlyoutAction(ClearAction, "Clear")],
            StayOpen: true);
    }
```

- [ ] **Step 8: Connector.** In `ClipboardConnector.cs`:

  1. Add fields:

```csharp
    volatile FlyoutRow? _saveRow; // the last Save's outcome, in memory only
    string? _savedPath;           // only touched under the gate
```

  2. Change `GetFlyoutAsync` to:

```csharp
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(ClipboardView.Flyout(_last, _saveRow));
```

  3. At the start of `OnActionAsync`, after the `ctx is null` check, add:

```csharp
        if (actionId == ClipboardSave.SaveAction) { await SaveAsync(ctx, ct); return; }
        if (actionId == ClipboardSave.ShowSavedAction) { ShowSaved(ctx); return; }
```

  4. Add these methods:

```csharp
    /// <summary>
    /// Writes what is on the clipboard now into the save folder: an image as PNG (it wins over text), text as UTF-8.
    /// The outcome becomes the popup's last row. Content never reaches the log.
    /// </summary>
    async Task SaveAsync(ITileContext ctx, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
            ClipSaveRead read;
            try { read = await Task.Run(_clipboard.ReadForSave, CancellationToken.None); }
            catch (Exception ex)
            {
                ctx.Log.Warn($"Could not read the clipboard to save it: {ex.Message}");
                _saveRow = new FlyoutRow("Could not save: the clipboard could not be read");
                return;
            }
            if (read.Retry) { _saveRow = new FlyoutRow("Could not save: another app holds the clipboard"); return; }
            var bmp = read.Dib is { } dib ? ClipboardSave.BmpFromDib(dib) : null;
            if (read.Excluded || read.Files || (bmp is null && string.IsNullOrEmpty(read.Text)))
            {
                _saveRow = new FlyoutRow("Nothing to save");
                return;
            }

            try
            {
                var folder = ClipboardSave.Folder(ctx.Settings);
                Directory.CreateDirectory(folder);
                var name = ClipboardSave.FileName(_now(), bmp is not null ? ".png" : ".txt", n => File.Exists(Path.Combine(folder, n)));
                var path = Path.Combine(folder, name);
                if (bmp is not null) await File.WriteAllBytesAsync(path, await PngEncoder.FromBmpAsync(bmp), CancellationToken.None);
                else await File.WriteAllTextAsync(path, read.Text, new System.Text.UTF8Encoding(false), CancellationToken.None);
                _savedPath = path;
                _saveRow = new FlyoutRow($"Saved {name}", Action: ClipboardSave.ShowSavedAction);
                ctx.Log.Info($"Clipboard saved to {path}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                           or System.Runtime.InteropServices.COMException)
            {
                ctx.Log.Warn($"Could not save the clipboard: {ex.Message}");
                _saveRow = new FlyoutRow($"Could not save: {ex.Message}");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explorer with the last saved file selected.</summary>
    void ShowSaved(ITileContext ctx)
    {
        if (_savedPath is not { } path || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { ctx.Log.Warn($"Could not open Explorer: {ex.Message}"); }
    }
```

  5. The existing early-return line in `OnActionAsync` (`if (actionId != ClipboardView.ClearAction && ClipboardView.CopyId(actionId) is not { } id) return;`) stays as it is, below the two new lines.

- [ ] **Step 9: tile.json and docs.**

  1. Replace `tiles/clipboard/tile.json`'s `fields` and `settings` with:

```json
  "fields": [
    { "key": "history", "label": "Keep", "type": "number", "min": 1, "max": 50, "help": "How many recent copies the popup lists. They stay in memory only and are gone when the app closes." },
    { "key": "saveFolder", "label": "Save to", "type": "text", "browse": "folder", "help": "Where Save in the popup writes the clipboard: an image as PNG, text as a .txt file. Empty: Pictures\\Clipboard." }
  ],
  "settings": { "history": 15, "saveFolder": "" }
```

  2. In `docs/configuration.md` `### Clipboard peek`, replace the sentence "Copies stay in memory only, never on disk or in the log, and are gone when the app closes." with:

     > **Save** writes what is on the clipboard now to a file: an image as PNG (when there is both an image and text, the image), text as a UTF-8 .txt file, named like `clipboard-2026-11-05-143012.png`. They go to the folder in setting `"saveFolder"` (also in **Settings…**, with a folder picker; default `Pictures\Clipboard`, created when needed). The popup then shows the file; click it to see it in Explorer. Copied files and copies marked private are not saved. Apart from Save, copies stay in memory only, never on disk or in the log, and are gone when the app closes.

  3. Update the connector's class doc comment in `ClipboardConnector.cs` to match: "Copies live in memory only: never written to the log, and to disk only when you press Save."

- [ ] **Step 10: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers`
Expected: PASS, including `Save_writes_an_image_as_PNG_when_there_is_also_text`, which uses the real WinRT encoder.

If `AsBuffer` is not found, check the namespace in the existing media plugin: `grep -rn "AsBuffer\|AsStream" src/TaskbarTiles.Plugins.Media`.

- [ ] **Step 11: Commit**

```bash
git add src/TaskbarTiles.Plugins.Clipboard tiles/clipboard/tile.json docs/configuration.md tests/TaskbarTiles.Tests/ClipboardTests.cs
git commit -m "feat(clipboard): Save writes the clipboard to a PNG or text file in a chosen folder" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final check (after all tasks)

- [ ] `dotnet test --disable-build-servers`: all tests pass.
- [ ] `git status` is clean, and `git log --oneline main..` shows the spec commit plus five task commits.
- [ ] Tell the user what to check by hand after `install.cmd`:
  - The To do popup: typing plus Enter, ticking, Clear done, and the popup staying open.
  - The checkbox look.
  - Clipboard Save of a screenshot and of text.
  - Clicking "Saved …" to open Explorer.
