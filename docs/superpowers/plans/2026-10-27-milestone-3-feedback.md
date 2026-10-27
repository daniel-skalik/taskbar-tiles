# Taskbar Tiles — Milestone 3 Feedback Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the user's on-screen feedback on milestone 3: a tile menu that creates stacks by picking a tile, `×` that deletes definitions nobody places any more, dragging tiles from one monitor's bar to another's, and drag visuals (a ghost that follows the pointer and tiles that slide apart where the drop lands).

**Architecture:** Every decision stays in pure, unit-tested code: `TileMenu` (menu entries, the stack pickers' submenu contents and labels), `LayoutEdits` (`RemovedTileIds`, `DropIncoming` for a slot arriving from another display), `ConfigJson.RemoveUnplacedTiles` (pruning against the file being saved), `DropGeometry` (drop spot, gap and slide offsets from slot positions) and `HostController` (`RemoveTile`, `MoveToDisplay`, each one save). The WPF side stays thin: `TileBarRegistry` lets the bar that holds the mouse capture find the bar under the pointer by screen position, `TileBar` asks that bar for its drop spot and preview, animates slide transforms (never layout widths), and `DragGhost` is a topmost, never-activated, click-through window showing a snapshot of the dragged slot.

**Tech Stack:** .NET 9, WPF, C# latest, xUnit 2.9, System.Text.Json (`JsonNode`).

Feedback source: the user's test of milestone 3 on screen (2026-10-27). Earlier plan: `docs/superpowers/plans/2026-10-23-milestone-3-layout.md` (its Global Constraints and Decisions still apply except where this plan says otherwise). The code on `feat/milestone-3-layout` (`00b4939`) is the truth where it differs from that plan; every "Edit" below is written against that code.

## Global Constraints

- Target frameworks: host and tests `net9.0-windows10.0.19041.0`; contracts and the script plugin `net9.0`. No new NuGet packages. No change to `TaskbarTiles.Contracts`.
- Plugins never run on the UI thread. Layout code never calls a connector.
- `config.json` writes go through `IConfigWriter.Save` (whole-file rewrite, `config.json.tmp` + `File.Replace`, `.bak`, validated with `ConfigParser` before writing). One user action is one save.
- Edit mode writes the display key from `DisplayResolver.EditKey`: the device id if it has its own entry, else `primary` on the primary display, else the device id. `*` is never written.
- Tile widths, gaps and insets are unchanged (gap 6 DIP, inset 4 DIP, compact and `»` 40 DIP). A drop preview never changes layout widths: no reflow while dragging.
- The drag ghost never takes focus or mouse input: `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, topmost, `ShowActivated = false`.
- Only slots on a bar can be dragged, not slots inside the `»` overflow popup.
- WPF projects (Host, Tests) do not get `System.IO` from implicit usings: add `using System.IO;` explicitly where needed.
- Tests are xUnit in `tests/TaskbarTiles.Tests`. WPF controls are tested on an STA thread with `Sta.Run` and never show a window.
- Run every `dotnet build` and `dotnet test` with `--disable-build-servers` (build nodes left running from agent sessions break the user's own builds). `dotnet build TaskbarTiles.sln --disable-build-servers` stays at 0 warnings.
- Agent sessions never run `tools\install.ps1` against `%LOCALAPPDATA%` and never start or stop `TaskbarTiles.exe`; the user installs from their own terminal.
- Every commit message ends with a trailer naming the model that wrote the commit. The commands below show `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; use your own model name if it differs.

## File Structure

```
docs/configuration.md                           tile menu, × deletes unused definitions, drag across monitors, drag visuals
docs/superpowers/plans/2026-10-23-milestone-3-layout.md   three superseded decisions marked
src/TaskbarTiles.Host/
  App.xaml.cs                                   one TileBarRegistry, passed to every TileBar
  HostController.cs                             ILayoutEditor.RemoveTile, ILayoutEditor.MoveToDisplay; Editable helper; Saved(what, error)
  Config/LayoutEdits.cs                         RemovedTileIds, DropIncoming; AddToNearestStack removed
  Config/ConfigJson.cs                          RemoveUnplacedTiles
  Layout/DropGeometry.cs                        SlotSpan, DropSpot, SpotAt, ToDrop, OpensGap, Shifts (new)
  Taskbar/PixelRect.cs                          Contains
  Taskbar/NativeMethods.cs                      WS_EX_TRANSPARENT, SWP_NOSIZE
  UI/TileCommands.cs                            LayoutCommand.CreateStack; MenuEntry.Choice/Submenu; MenuSlot, StackChoice; SlotMenuState reshaped; TileMenu.StackChoices
  UI/TileBar.cs                                 stack pickers, RemoveTile, drop on any bar, drop preview with slide transforms, ghost
  UI/TileBarRegistry.cs                         every bar, BarAt(screen point) (new)
  UI/DragGhost.cs                               click-through snapshot window that follows the pointer (new)
tests/TaskbarTiles.Tests/
  DropGeometryTests.cs, PixelRectTests.cs, DragGhostTests.cs (new)
  TileCommandsTests.cs, LayoutEditsTests.cs, ConfigJsonTests.cs, HostControllerLayoutTests.cs (modified)
  TileBarHitTestTests.cs (deleted: TileBar.Inside is replaced by PixelRect.Contains)
```

### API changes

| Change | Why |
|---|---|
| `LayoutCommand { MoveLeft, MoveRight, AddToStack, … }` → `{ CreateStack, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }` | Move left/right removed; stacks are made by picking a tile. |
| `MenuEntry` + `StackChoice? Choice = null`, `IReadOnlyList<MenuEntry>? Submenu = null`; `IsSeparator` also requires no submenu | "Create stack with ▸" and "Add to stack ▸" are submenus. |
| `SlotMenuState(bool IsFirst, bool IsLast, bool IsStack, bool RotationPaused, bool CanStack)` → `SlotMenuState(bool IsStack, bool RotationPaused, IReadOnlyList<StackChoice> StackChoices)` | Position is no longer needed; the pickers need their choices. |
| `sealed record MenuSlot(int Index, bool IsStack, IReadOnlyList<string> TileIds, string? Title)`, `sealed record StackChoice(int SlotIndex, string TileId, string Label)`, `static IReadOnlyList<StackChoice> TileMenu.StackChoices(IReadOnlyList<MenuSlot> slots, int clicked)` | Pure submenu contents. |
| `LayoutEdits.AddToNearestStack` removed; `+ IReadOnlyList<string> RemovedTileIds(DisplayLayout before, DisplayLayout after)`, `+ DisplayLayout DropIncoming(DisplayLayout layout, Slot slot, int? target, DropPosition position)` | Pruning and cross-display drops. |
| `+ static void ConfigJson.RemoveUnplacedTiles(JsonObject root, IEnumerable<string> candidates)` | Deletes definitions in the same save. |
| `ILayoutEditor` + `ConfigError? RemoveTile(string displayId, int index, int? member)`, + `ConfigError? MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position)` | × / Remove tile prune; one save for a move between displays. |
| `Layout/DropGeometry.cs`: `readonly record struct SlotSpan(double Left, double Width)`, `readonly record struct DropSpot(int Index, bool Into)`, `DropGeometry.SpotAt`, `ToDrop`, `OpensGap`, `Shifts` | Pure drop spot and gap preview. |
| `PixelRect.Contains(int x, int y)` | Which bar's window holds the pointer. |
| `TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)` → `TileBar(…, ILayoutEditor editor, TileBarRegistry bars)`; internal `HoldsPoint`, `DropAt`, `ShowDropPreview`, `ClearDropPreview`; `TileBar.Inside` removed | Drops on other displays' bars. |
| `sealed class TileBarRegistry { Add, Remove, BarAt(Point screen) }` | Host-wide list of bars, hit-tested by screen point. |
| `sealed class DragGhost : Window { DragGhost(FrameworkElement slot, Point grab); MoveTo(Point screen); static long ExStyle(long); static BitmapSource Snapshot(FrameworkElement, double scale) }` | The ghost. |

### Decisions

| Decision | Why |
|---|---|
| The submenu headers are "Create stack with" and "Add to stack"; WPF draws the ▸ itself. A picker with no choices is disabled (WPF then shows no arrow). | The user's wording; no hand-drawn arrow next to WPF's own. |
| "Create stack with ▸" lists every other plain tile of this display (overflowed ones included), never the clicked tile's own id; "Add to stack ▸" lists every plain tile of this display not already in the stack. Pinned tiles are listed; stacking drops the pin, as dragging into a stack already does. | "Other plain tiles on this display"; no choice that would do nothing. |
| A choice is labelled with the title its tile shows when that title has a letter or digit, otherwise with the tile id ("…" while starting, empty titles). Labels that would repeat get " (id)" appended. Underscores are escaped so WPF does not read them as access keys. | The user's "shown title or the tile id"; two ping tiles stay distinguishable. |
| Create stack makes `[clicked, chosen]` where the clicked tile was, with the default rotate (5 s); Add to stack appends the chosen tile to the clicked stack and keeps its rotate. Both are the existing `LayoutEdits.StackInto(chosen → clicked)`. `AddToNearestStack` is deleted. | Same rule as dropping onto a tile's middle; no new edit to test. |
| The pickers and the other layout items stay in the menu outside edit mode, as before. | The feedback only removes Move left/right. |
| Pruning runs only for × and "Remove tile" (`ILayoutEditor.RemoveTile`). Candidates are the ids the edit took off this display; one is deleted when no display entry in the file being saved (including `*` and entries of monitors not connected now) places it after the change. Placement is read with `ConfigParser`, so all slot forms count; a file that would not parse is not pruned (the save refuses it anyway). | Exactly the feedback; hand-added unplaced tiles and edits that remove nothing never prune; `config.json.bak` holds the previous file. |
| A move between displays is one `ILayoutEditor.MoveToDisplay` call and one save writing both edit keys. Within one display it is the existing `LayoutEdits.Drop`. | One action, one save; no half-done move. |
| An arriving slot keeps its pin (plain tile) or its rotate (stack). Stack members the target display already places are left out; a stack left with one member arrives as a plain tile; with nothing left only the source changes. | "No duplicate"; same collapse rule as everywhere else. |
| The drop spot is computed from the layout positions of the target bar's shown slots (the dragged one excluded on its own bar): the middle 40 % of a slot is "into", otherwise the gap before or after it. A gap lands before the slot after it; past the last shown slot (including `»`, `+` and Done) lands after the last shown slot; a bar with no shown slot appends. | The drop lands where the gap opened. Without overflowed tiles "after the last shown slot" is the end of the layout, which is what the feedback asks for. |
| On the dragging bar a release between two tiles or over `+`/Done now moves the tile too (before: only a release on a tile did). Releasing outside every bar cancels. | One rule for every bar. |
| `TileBarRegistry` holds every bar; `BarAt` returns the first bar in edit mode and on screen whose window rectangle (`GetWindowRect`, physical pixels) holds the pointer. The pointer comes from the capturing bar's `PointToScreen(e.GetPosition(this))`. | The source bar keeps the capture, so other bars get no mouse events. |
| The gap preview slides the slots after the gap and the `»`, `+` and Done buttons right by a fixed 40 DIP (150 ms, ease-out) with a `TranslateTransform`; slots before it stay. In edit mode the bar keeps 40 DIP of transparent room after Done so nothing slides off the window. On the dragging bar the dragged slot's own place opens no gap. | The bar window is exactly as wide as its content; a fixed reserve, taken once when edit mode starts, keeps layout widths unchanged during the drag. |
| On drop or cancel the slide animations are removed at once (no reverse animation) and the highlight is cleared. | "Everything snaps back". |
| The ghost is a 70 % opaque snapshot of the slot (`VisualBrush` into a `RenderTargetBitmap` at the source bar's DPI), taken before the slot fades, positioned so the point the user grabbed stays under the pointer. If it cannot be created the drag goes on without it (logged). | Visual only; it must never block a drag. |

---

### Task 1: Tile menu: Create stack with ▸ and Add to stack ▸, no Move left/right

**Files:**
- Modify: `src/TaskbarTiles.Host/UI/TileCommands.cs`, `src/TaskbarTiles.Host/UI/TileBar.cs`, `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, `docs/configuration.md`, `docs/superpowers/plans/2026-10-23-milestone-3-layout.md`
- Test: `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`

**Interfaces:**
- Consumes: `LayoutEdits.StackInto(DisplayLayout layout, int from, int target)`, `BarSlot` (`Index`, `IsStack`, `Members`), `TileRuntime.State.Title`
- Produces (namespace `TaskbarTiles.Host.UI`):
  - `enum LayoutCommand { CreateStack, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }`
  - `sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null, bool Enabled = true, StackChoice? Choice = null, IReadOnlyList<MenuEntry>? Submenu = null)`
  - `sealed record MenuSlot(int Index, bool IsStack, IReadOnlyList<string> TileIds, string? Title)`
  - `sealed record StackChoice(int SlotIndex, string TileId, string Label)`
  - `sealed record SlotMenuState(bool IsStack, bool RotationPaused, IReadOnlyList<StackChoice> StackChoices)`
  - `static IReadOnlyList<StackChoice> TileMenu.StackChoices(IReadOnlyList<MenuSlot> slots, int clicked)`
  - `LayoutEdits.AddToNearestStack` no longer exists.

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, find:
```csharp
    static readonly SlotMenuState MiddleTile = new(IsFirst: false, IsLast: false, IsStack: false, RotationPaused: false, CanStack: true);

    [Fact]
    public void Tile_menu_lists_tile_commands_layout_commands_then_bar_commands() =>
        Assert.Equal(
            new[]
            {
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
                "Move left", "Move right", "Add to stack", "", "Edit layout", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile, editing: false).Select(e => e.Header));

    [Fact]
    public void Stack_menu_offers_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true }, editing: false);
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true }, editing: false);

        Assert.Equal(new[] { "Remove from stack", "Pause rotation" }, running.Skip(7).Take(2).Select(e => e.Header));
        Assert.Equal(LayoutCommand.PauseRotation, running[8].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[8].Layout);
        Assert.DoesNotContain(running, e => e.Layout == LayoutCommand.AddToStack);
    }

    [Fact]
    public void Moves_and_add_to_stack_are_disabled_where_they_cannot_apply()
    {
        var only = TileMenu.Entries(new SlotMenuState(IsFirst: true, IsLast: true, IsStack: false, RotationPaused: false, CanStack: false), editing: false);

        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveLeft).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveRight).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.AddToStack).Enabled);
        Assert.True(only.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }
```
Replace with:
```csharp
    static readonly SlotMenuState MiddleTile = new(IsStack: false, RotationPaused: false, StackChoices: [new StackChoice(2, "b", "Bee")]);

    [Fact]
    public void Tile_menu_lists_tile_commands_layout_commands_then_bar_commands() =>
        Assert.Equal(
            new[]
            {
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
                "Create stack with", "", "Edit layout", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile, editing: false).Select(e => e.Header));

    [Fact]
    public void Create_stack_with_lists_the_choices_in_a_submenu()
    {
        var entry = TileMenu.Entries(MiddleTile, editing: false).Single(e => e.Header == "Create stack with");

        Assert.True(entry.Enabled);
        Assert.False(entry.IsSeparator);
        Assert.Null(entry.Layout);
        var choice = Assert.Single(entry.Submenu!);
        Assert.Equal("Bee", choice.Header);
        Assert.Equal(LayoutCommand.CreateStack, choice.Layout);
        Assert.Equal(new StackChoice(2, "b", "Bee"), choice.Choice);
    }

    [Fact]
    public void Stack_menu_offers_add_to_stack_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true }, editing: false);
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true }, editing: false);

        Assert.Equal(new[] { "Add to stack", "Remove from stack", "Pause rotation" }, running.Skip(5).Take(3).Select(e => e.Header));
        Assert.Equal(LayoutCommand.AddToStack, Assert.Single(running[5].Submenu!).Layout);
        Assert.Equal(LayoutCommand.PauseRotation, running[7].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[7].Layout);
        Assert.DoesNotContain(running, e => e.Header == "Create stack with");
    }

    [Fact]
    public void Stack_pickers_without_choices_are_disabled()
    {
        var plain = TileMenu.Entries(new SlotMenuState(IsStack: false, RotationPaused: false, StackChoices: []), editing: false);
        var stack = TileMenu.Entries(new SlotMenuState(IsStack: true, RotationPaused: false, StackChoices: []), editing: false);

        Assert.False(plain.Single(e => e.Header == "Create stack with").Enabled);
        Assert.False(stack.Single(e => e.Header == "Add to stack").Enabled);
        Assert.True(plain.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }

    static MenuSlot Plain(int index, string id, string? title = null) => new(index, false, [id], title);
    static MenuSlot Stack(int index, params string[] ids) => new(index, true, ids, null);

    [Fact]
    public void Create_stack_choices_are_the_other_plain_tiles()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Stack(1, "s1", "s2"), Plain(2, "b", "Bravo"), Plain(4, "a", "Alpha") };

        Assert.Equal(new[] { new StackChoice(2, "b", "Bravo") }, TileMenu.StackChoices(slots, clicked: 0));
    }

    [Fact]
    public void Add_to_stack_choices_are_the_plain_tiles_not_in_the_stack()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Stack(1, "a", "b"), Plain(2, "c", "Charlie") };

        Assert.Equal(new[] { new StackChoice(2, "c", "Charlie") }, TileMenu.StackChoices(slots, clicked: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("…")]
    public void A_title_without_letters_or_digits_shows_the_tile_id(string? title) =>
        Assert.Equal("ping", TileMenu.StackChoices([Plain(0, "a", "Alpha"), Plain(1, "ping", title)], clicked: 0).Single().Label);

    [Fact]
    public void Repeated_labels_get_the_tile_id()
    {
        var slots = new[] { Plain(0, "a", "Alpha"), Plain(1, "ping", "Ping"), Plain(2, "ping-2", "Ping"), Plain(3, "up", " Uptime ") };

        Assert.Equal(new[] { "Ping (ping)", "Ping (ping-2)", "Uptime" }, TileMenu.StackChoices(slots, clicked: 0).Select(c => c.Label));
    }

    [Fact]
    public void An_unknown_clicked_slot_has_no_choices() =>
        Assert.Empty(TileMenu.StackChoices([Plain(0, "a"), Plain(1, "b")], clicked: 7));
```

**Edit** `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`, find:
```csharp
    [Fact]
    public void Move_left_and_right_are_drops_before_and_after_the_neighbor()
    {
        var layout = L(T("a"), S(Five, "b", "c"), T("d"));
        Same(L(S(Five, "b", "c"), T("a"), T("d")), LayoutEdits.Drop(layout, 1, 0, DropPosition.Before));
        Same(L(T("a"), T("d"), S(Five, "b", "c")), LayoutEdits.Drop(layout, 1, 2, DropPosition.After));
    }

    [Fact]
    public void Add_to_stack_joins_the_nearest_stack_left_first_on_a_tie()
    {
        var layout = L(S(Five, "s1", "s2"), T("a"), S(Eight, "s3", "s4"));
        Same(L(S(Five, "s1", "s2", "a"), S(Eight, "s3", "s4")), LayoutEdits.AddToNearestStack(layout, 1));
    }

    [Fact]
    public void Add_to_stack_prefers_a_closer_stack_on_the_right()
    {
        var layout = L(S(Five, "s1", "s2"), T("a"), T("b"), S(Eight, "s3", "s4"));
        Same(L(S(Five, "s1", "s2"), T("a"), S(Eight, "s3", "s4", "b")), LayoutEdits.AddToNearestStack(layout, 2));
    }

    [Fact]
    public void Add_to_stack_without_a_stack_pairs_with_the_right_neighbor_or_the_left_one_when_last()
    {
        Same(L(S(Five, "b", "a"), T("c")), LayoutEdits.AddToNearestStack(L(T("a"), T("b"), T("c")), 0));
        Same(L(T("a"), S(Five, "b", "c")), LayoutEdits.AddToNearestStack(L(T("a"), T("b"), T("c")), 2));
    }

    [Fact]
    public void Add_to_stack_leaves_a_stack_or_a_lone_tile_alone()
    {
        var withStack = L(S(Five, "a", "b"), T("c"));
        Same(withStack, LayoutEdits.AddToNearestStack(withStack, 0));
        var lone = L(T("a"));
        Same(lone, LayoutEdits.AddToNearestStack(lone, 0));
    }
```
Replace with:
```csharp
    [Fact]
    public void Drops_before_and_after_a_neighbor_move_a_stack_as_one_slot()
    {
        var layout = L(T("a"), S(Five, "b", "c"), T("d"));
        Same(L(S(Five, "b", "c"), T("a"), T("d")), LayoutEdits.Drop(layout, 1, 0, DropPosition.Before));
        Same(L(T("a"), T("d"), S(Five, "b", "c")), LayoutEdits.Drop(layout, 1, 2, DropPosition.After));
    }

    [Fact]
    public void Stacking_a_chosen_tile_into_the_clicked_one_keeps_the_clicked_place()
    {
        // "Create stack with ▸": the clicked tile (index 2) stays where it is and comes first.
        Same(L(T("b"), S(Five, "c", "a")), LayoutEdits.StackInto(L(T("a"), T("b"), T("c")), from: 0, target: 2));
        // "Add to stack ▸": the chosen tile goes to the end of the clicked stack, which keeps its rotate.
        Same(L(S(Eight, "s1", "s2", "x")), LayoutEdits.StackInto(L(S(Eight, "s1", "s2"), T("x")), from: 1, target: 0));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~LayoutEditsTests"`
Expected: build FAIL: `SlotMenuState` has no `StackChoices`, `MenuSlot`, `StackChoice` and `TileMenu.StackChoices` do not exist.

- [ ] **Step 3: Implement the menu**

**Edit** `src/TaskbarTiles.Host/UI/TileCommands.cs`, find:
```csharp
/// <summary>Commands that change the layout (saved to config.json) or a stack's rotation (not saved).</summary>
public enum LayoutCommand { MoveLeft, MoveRight, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }

/// <summary>One context-menu line; an entry with no command is a separator.</summary>
public sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null,
    bool Enabled = true)
{
    public static MenuEntry Separator { get; } = new("");
    public bool IsSeparator => Tile is null && Bar is null && Layout is null;
}

/// <summary>What the menu needs to know about the right-clicked slot.</summary>
public sealed record SlotMenuState(bool IsFirst, bool IsLast, bool IsStack, bool RotationPaused, bool CanStack);
```
Replace with:
```csharp
/// <summary>Commands that change the layout (saved to config.json) or a stack's rotation (not saved).</summary>
public enum LayoutCommand { CreateStack, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }

/// <summary>
/// One context-menu line. An entry with a submenu opens it; an entry with neither a command nor a submenu is a separator.
/// <see cref="Choice"/> is the tile a stack picker's line stands for.
/// </summary>
public sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null,
    bool Enabled = true, StackChoice? Choice = null, IReadOnlyList<MenuEntry>? Submenu = null)
{
    public static MenuEntry Separator { get; } = new("");
    public bool IsSeparator => Tile is null && Bar is null && Layout is null && Submenu is null;
}

/// <summary>A slot of this display as the menu sees it: config index, tile ids and the title its first member shows.</summary>
public sealed record MenuSlot(int Index, bool IsStack, IReadOnlyList<string> TileIds, string? Title);

/// <summary>A plain tile a stack picker offers: its slot's config index, its id and the label shown.</summary>
public sealed record StackChoice(int SlotIndex, string TileId, string Label);

/// <summary>What the menu needs to know about the right-clicked slot.</summary>
public sealed record SlotMenuState(bool IsStack, bool RotationPaused, IReadOnlyList<StackChoice> StackChoices);
```

**Edit** `src/TaskbarTiles.Host/UI/TileCommands.cs`, find:
```csharp
            entries.Add(MenuEntry.Separator);
            entries.Add(new("Move left", Layout: LayoutCommand.MoveLeft, Enabled: !slot.IsFirst));
            entries.Add(new("Move right", Layout: LayoutCommand.MoveRight, Enabled: !slot.IsLast));
            if (slot.IsStack)
            {
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
```
Replace with:
```csharp
            entries.Add(MenuEntry.Separator);
            if (slot.IsStack)
            {
                entries.Add(Picker("Add to stack", LayoutCommand.AddToStack, slot.StackChoices));
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
```

**Edit** `src/TaskbarTiles.Host/UI/TileCommands.cs`, find:
```csharp
            else
            {
                entries.Add(new("Add to stack", Layout: LayoutCommand.AddToStack, Enabled: slot.CanStack));
            }
```
Replace with:
```csharp
            else
            {
                entries.Add(Picker("Create stack with", LayoutCommand.CreateStack, slot.StackChoices));
            }
```

**Edit** `src/TaskbarTiles.Host/UI/TileCommands.cs`, find:
```csharp
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }
}
```
Replace with:
```csharp
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }

    /// <summary>
    /// The plain tiles of this display a stack picker offers for the clicked slot: for a plain tile every other plain tile
    /// (never one with the clicked tile's id), for a stack every plain tile not in it yet. Each is labelled with the title
    /// its tile shows, or its id when that title has no letter or digit; labels that would repeat get the id appended.
    /// </summary>
    public static IReadOnlyList<StackChoice> StackChoices(IReadOnlyList<MenuSlot> slots, int clicked)
    {
        var target = slots.FirstOrDefault(s => s.Index == clicked);
        if (target is null) return [];
        var candidates = slots
            .Where(s => !s.IsStack && s.TileIds.Count == 1 && s.Index != clicked && !target.TileIds.Contains(s.TileIds[0]))
            .ToList();
        var labels = candidates.Select(Label).ToList();
        return candidates.Select((s, i) =>
        {
            var id = s.TileIds[0];
            var repeated = labels[i] != id && labels.Count(l => l == labels[i]) > 1;
            return new StackChoice(s.Index, id, repeated ? $"{labels[i]} ({id})" : labels[i]);
        }).ToList();
    }

    static string Label(MenuSlot slot) =>
        slot.Title is { } title && title.Any(char.IsLetterOrDigit) ? title.Trim() : slot.TileIds[0];

    /// <summary>A submenu with one line per choice; disabled when there is nothing to pick.</summary>
    static MenuEntry Picker(string header, LayoutCommand command, IReadOnlyList<StackChoice> choices) =>
        new(header, Enabled: choices.Count > 0,
            Submenu: choices.Select(c => new MenuEntry(c.Label, Layout: command, Choice: c)).ToList());
}
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        var position = _views.IndexOf(view);
        var state = new SlotMenuState(
            IsFirst: position <= 0,
            IsLast: position == _views.Count - 1,
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            CanStack: !view.Slot.IsStack && _views.Count > 1);
```
Replace with:
```csharp
    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        // Every slot of this display, overflowed ones included: the stack pickers offer all its plain tiles.
        var slots = _slots
            .Select(s => new MenuSlot(s.Index, s.IsStack, s.Members.Select(m => m.TileId).ToList(), s.Members.FirstOrDefault()?.State.Title))
            .ToList();
        var state = new SlotMenuState(
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            StackChoices: TileMenu.StackChoices(slots, view.Slot.Index));
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
        if (entry.Tile is { } command && view is not null)
```
Replace with:
```csharp
        // Tile titles may hold "_", which a menu header would take as an access key marker.
        var item = new MenuItem { Header = entry.Header.Replace("_", "__"), IsEnabled = entry.Enabled };
        if (entry.Submenu is { } submenu)
        {
            foreach (var child in submenu) item.Items.Add(Item(child, view, member));
        }
        else if (entry.Tile is { } command && view is not null)
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
            item.Click += (_, _) => Run(layout, view, member);
```
Replace with:
```csharp
            item.Click += (_, _) => Run(layout, view, member, entry.Choice);
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    void Run(LayoutCommand command, SlotView view, int member)
    {
        var slot = view.Slot;
        var position = _views.IndexOf(view);
        switch (command)
        {
            case LayoutCommand.PauseRotation when view.Rotation is { } rotation:
                rotation.Paused = true;
                break;
            case LayoutCommand.ResumeRotation when view.Rotation is { } rotation:
                rotation.Paused = false;
                break;
            case LayoutCommand.MoveLeft when position > 0:
                var left = _views[position - 1].Slot.Index;
                Edit(l => LayoutEdits.Drop(l, slot.Index, left, DropPosition.Before));
                break;
            case LayoutCommand.MoveRight when position >= 0 && position < _views.Count - 1:
                var right = _views[position + 1].Slot.Index;
                Edit(l => LayoutEdits.Drop(l, slot.Index, right, DropPosition.After));
                break;
            case LayoutCommand.AddToStack:
                Edit(l => LayoutEdits.AddToNearestStack(l, slot.Index));
                break;
```
Replace with:
```csharp
    void Run(LayoutCommand command, SlotView view, int member, StackChoice? choice)
    {
        var slot = view.Slot;
        switch (command)
        {
            case LayoutCommand.PauseRotation when view.Rotation is { } rotation:
                rotation.Paused = true;
                break;
            case LayoutCommand.ResumeRotation when view.Rotation is { } rotation:
                rotation.Paused = false;
                break;
            case LayoutCommand.CreateStack or LayoutCommand.AddToStack when choice is not null:
                // The chosen tile joins the clicked slot: a new stack [clicked, chosen] where the clicked tile is,
                // or the end of the clicked stack.
                Edit(l => LayoutEdits.StackInto(l, choice.SlotIndex, slot.Index));
                break;
```

**Edit** `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, find:
```csharp
    /// <summary>
    /// "Add to stack": joins the nearest stack (the left one on a tie). Without a stack, it forms a new stack with
    /// the slot to its right (or to its left when it is last). A stack itself, or the only slot, is left alone.
    /// </summary>
    public static DisplayLayout AddToNearestStack(DisplayLayout layout, int index)
    {
        if (!Valid(layout, index) || layout.Slots[index] is StackSlot) return layout;

        var nearest = -1;
        for (var distance = 1; distance < layout.Slots.Count && nearest < 0; distance++)
        {
            if (Valid(layout, index - distance) && layout.Slots[index - distance] is StackSlot) nearest = index - distance;
            else if (Valid(layout, index + distance) && layout.Slots[index + distance] is StackSlot) nearest = index + distance;
        }
        if (nearest < 0) nearest = Valid(layout, index + 1) ? index + 1 : index - 1;
        return Valid(layout, nearest) ? StackInto(layout, index, nearest) : layout;
    }

    /// <summary>"Remove from stack": the member becomes a plain tile right after the stack.</summary>
```
Replace with:
```csharp
    /// <summary>"Remove from stack": the member becomes a plain tile right after the stack.</summary>
```

**Edit** `docs/configuration.md`, find:
````markdown
Outside edit mode the right-click menu also has **Move left**, **Move right**, **Add to stack** (joins the nearest stack, or makes a stack with the neighbor), **Remove from stack** and **Remove tile**.
````
Replace with:
````markdown
The right-click menu changes the layout too, in and outside edit mode:

- On a tile, **Create stack with ▸** lists the other tiles on this display that are not in a stack (by the title they show, or their id). Pick one to make a stack of the two where the tile you clicked is.
- On a stack, **Add to stack ▸** lists this display's tiles that are not in a stack; the one you pick moves into this stack. **Remove from stack** takes the member shown out, right after the stack; **Pause rotation** stops it.
- **Remove tile** works like `×`.

To move a tile, drag it in edit mode.
````

**Edit** `docs/superpowers/plans/2026-10-23-milestone-3-layout.md`, find:
````markdown
| "Add to stack" joins the nearest stack (left wins a tie); without a stack it forms one with the right neighbor (left when last). | The menu has no target picker; nearest is what the user sees. |
| "Move left/right" are drops before/after the neighboring bar slot. | Correct even when the config holds an empty (invisible) stack between them. |
````
Replace with:
````markdown
| Superseded by `2026-10-27-milestone-3-feedback.md`: a plain tile's menu has "Create stack with ▸" and a stack's menu "Add to stack ▸", each listing this display's plain tiles. (Originally "Add to stack" joined the nearest stack.) | The user wants to pick the tile, and no one-member stacks. |
| Superseded by `2026-10-27-milestone-3-feedback.md`: "Move left/right" were removed; dragging in edit mode moves tiles. (Originally they were drops before/after the neighboring bar slot.) | User feedback after testing on screen. |
````

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~LayoutEditsTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln --disable-build-servers` then `dotnet test TaskbarTiles.sln --disable-build-servers`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 435`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/UI/TileCommands.cs src/TaskbarTiles.Host/UI/TileBar.cs src/TaskbarTiles.Host/Config/LayoutEdits.cs tests/TaskbarTiles.Tests/TileCommandsTests.cs tests/TaskbarTiles.Tests/LayoutEditsTests.cs docs/configuration.md docs/superpowers/plans/2026-10-23-milestone-3-layout.md
git commit -m "feat(ui): tile menu picks the tile to stack with; Move left/right removed" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: × and Remove tile delete definitions no display places

**Files:**
- Modify: `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, `src/TaskbarTiles.Host/Config/ConfigJson.cs`, `src/TaskbarTiles.Host/HostController.cs`, `src/TaskbarTiles.Host/UI/TileBar.cs`, `docs/configuration.md`, `docs/superpowers/plans/2026-10-23-milestone-3-layout.md`
- Test: `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`, `tests/TaskbarTiles.Tests/ConfigJsonTests.cs`, `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`

**Interfaces:**
- Consumes: `LayoutEdits.Remove(DisplayLayout, int index, int? member)`, `DisplayResolver.PlacedTileIds(DisplayLayout)`, `ConfigParser.Parse(string)`, `IConfigWriter.Save(Action<JsonObject>)`
- Produces:
  - `static IReadOnlyList<string> LayoutEdits.RemovedTileIds(DisplayLayout before, DisplayLayout after)`
  - `static void ConfigJson.RemoveUnplacedTiles(JsonObject root, IEnumerable<string> candidates)`
  - `ConfigError? ILayoutEditor.RemoveTile(string displayId, int index, int? member)` (implemented by `HostController`)
  - private `HostController.Editable(string displayId)` returning `((DisplayLayout Layout, string Key)? Target, ConfigError? Refused)`; private `HostController.Saved(string what, ConfigError? error)`

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`, find:
```csharp
    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
```
Replace with:
```csharp
    [Fact]
    public void Removed_tile_ids_are_the_ones_the_edit_took_off_the_display()
    {
        var before = L(T("a"), S(Five, "b", "c"), T("c"));

        Assert.Equal(new[] { "a" }, LayoutEdits.RemovedTileIds(before, LayoutEdits.Remove(before, 0)));
        Assert.Empty(LayoutEdits.RemovedTileIds(before, LayoutEdits.Remove(before, 1, member: 1))); // c is still placed
        Assert.Equal(new[] { "b", "c" }, LayoutEdits.RemovedTileIds(before, L(T("a"))));
    }

    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
```

**Edit** `tests/TaskbarTiles.Tests/ConfigJsonTests.cs`, find:
```csharp
    [Fact]
    public void Set_display_keeps_other_keys_and_their_order()
```
Replace with:
```csharp
    [Fact]
    public void Remove_unplaced_tiles_deletes_only_candidates_no_display_places()
    {
        var root = ConfigJson.ParseRoot("""
        { "tiles": { "a": { "connector": "x" }, "b": { "connector": "x" }, "c": { "connector": "x" }, "spare": { "connector": "x" } },
          "displays": { "primary": { "slots": ["a"] }, "*": { "slots": [ { "stack": ["b"] } ] } } }
        """);

        ConfigJson.RemoveUnplacedTiles(root, ["b", "c"]);

        Assert.Equal(new[] { "a", "b", "spare" }, root["tiles"]!.AsObject().Select(t => t.Key));
    }

    [Fact]
    public void Remove_unplaced_tiles_leaves_a_config_that_does_not_parse_alone()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "connector": "x" } }, "displays": { "primary": { "slots": ["ghost"] } } }""");

        ConfigJson.RemoveUnplacedTiles(root, ["a"]);

        Assert.NotNull(root["tiles"]!["a"]);
    }

    [Fact]
    public void Set_display_keeps_other_keys_and_their_order()
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, find:
```csharp
        public JsonElement Displays => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("displays");
    }
```
Replace with:
```csharp
        public JsonElement Displays => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("displays");

        public string[] TileIds => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("tiles").EnumerateObject().Select(p => p.Name).ToArray();
    }

    /// <summary>Config plus a tile defined by hand but not placed on any display.</summary>
    static readonly string WithSpare = Config.Replace(
        "\"c\": { \"connector\": \"ghost\" } }", "\"c\": { \"connector\": \"ghost\" }, \"spare\": { \"connector\": \"ghost\" } }");
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, find:
```csharp
    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
```
Replace with:
```csharp
    [Fact]
    public void Removing_the_last_placement_of_a_tile_deletes_its_definition_in_the_same_save()
    {
        var f = new Fixture(WithSpare);

        Assert.Null(f.Host.RemoveTile("MAIN", 0, null));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c", "spare" }, f.TileIds);
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.False(f.Host.Runtimes.ContainsKey("a"));
    }

    [Fact]
    public void Removing_a_stack_member_nothing_else_places_deletes_its_definition()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.RemoveTile("MAIN", 1, 0)); // b

        Assert.Equal(new[] { "a", "c" }, f.TileIds);
        Assert.Equal(new[] { "a", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void A_tile_another_display_entry_places_keeps_its_definition()
    {
        var f = new Fixture(Config); // only MAIN is connected; "*" still places c

        Assert.Null(f.Host.RemoveTile("MAIN", 1, 1));

        Assert.Equal(new[] { "a", "b", "c" }, f.TileIds);
    }

    [Fact]
    public void Edits_that_remove_no_tile_never_prune_unplaced_definitions()
    {
        var f = new Fixture(WithSpare);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After)));
        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.RemoveFromStack(l, 0, 0)));

        Assert.Equal(2, f.Writer.Saves);
        Assert.Contains("spare", f.TileIds);
    }

    [Fact]
    public void Removing_with_a_stale_index_saves_nothing()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.RemoveTile("MAIN", 9, null));
        Assert.NotNull(f.Host.RemoveTile("GONE", 0, null));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~LayoutEditsTests|FullyQualifiedName~ConfigJsonTests|FullyQualifiedName~HostControllerLayoutTests"`
Expected: build FAIL: `LayoutEdits.RemovedTileIds`, `ConfigJson.RemoveUnplacedTiles` and `HostController.RemoveTile` do not exist.

- [ ] **Step 3: Implement pruning**

**Edit** `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, find:
```csharp
    /// <summary>True when both layouts hold equal slots in the same order.</summary>
```
Replace with:
```csharp
    /// <summary>Tile ids <paramref name="before"/> places that <paramref name="after"/> no longer places, in order.</summary>
    public static IReadOnlyList<string> RemovedTileIds(DisplayLayout before, DisplayLayout after) =>
        DisplayResolver.PlacedTileIds(before).Except(DisplayResolver.PlacedTileIds(after), StringComparer.Ordinal).ToList();

    /// <summary>True when both layouts hold equal slots in the same order.</summary>
```

**Edit** `src/TaskbarTiles.Host/Config/ConfigJson.cs`, find:
```csharp
    /// <summary>A plain tile is its id, a pinned tile { "tile", "pin" }, a stack { "stack", "rotate" }.</summary>
```
Replace with:
```csharp
    /// <summary>
    /// Deletes tiles[id] for every candidate no display entry places (including "*" and entries of displays not connected
    /// now). Call it after the layout change, with the ids that change took off a display. Placement is read with
    /// ConfigParser, so every slot form counts; a tree that does not parse is left alone (saving refuses it anyway).
    /// </summary>
    public static void RemoveUnplacedTiles(JsonObject root, IEnumerable<string> candidates)
    {
        if (root["tiles"] is not JsonObject tiles) return;
        var parsed = ConfigParser.Parse(Serialize(root));
        if (!parsed.Success) return;
        var placed = parsed.Config!.Displays.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal);
        foreach (var id in candidates.Distinct(StringComparer.Ordinal))
            if (!placed.Contains(id)) tiles.Remove(id);
    }

    /// <summary>A plain tile is its id, a pinned tile { "tile", "pin" }, a stack { "stack", "rotate" }.</summary>
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
    /// <summary>Places the gallery item at the end of the display's layout, adding its tile definition when it is new.</summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item);
}
```
Replace with:
```csharp
    /// <summary>Places the gallery item at the end of the display's layout, adding its tile definition when it is new.</summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item);

    /// <summary>
    /// × and "Remove tile": removes slot <paramref name="index"/>, or with <paramref name="member"/> one member of a stack,
    /// from the display's layout. A tile no display entry (including "*") places afterwards loses its definition under
    /// "tiles" in the same save; other unplaced definitions are kept.
    /// </summary>
    ConfigError? RemoveTile(string displayId, int index, int? member);
}
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
    public ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit)
    {
        var (display, refused) = EditTarget(displayId);
        if (display is null) return refused;
        var cfg = config();
        var layout = DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        var changed = edit(layout);
        if (LayoutEdits.SameSlots(layout, changed)) return null;

        var key = DisplayResolver.EditKey(cfg, displayId, display.IsPrimary);
        return Saved(displayId, key, writer!.Save(root => ConfigJson.SetDisplay(root, key, changed)));
    }
```
Replace with:
```csharp
    public ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit)
    {
        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        var changed = edit(t.Layout);
        if (LayoutEdits.SameSlots(t.Layout, changed)) return null;
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root => ConfigJson.SetDisplay(root, t.Key, changed)));
    }

    public ConfigError? RemoveTile(string displayId, int index, int? member)
    {
        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        var changed = LayoutEdits.Remove(t.Layout, index, member);
        if (LayoutEdits.SameSlots(t.Layout, changed)) return null;
        var removed = LayoutEdits.RemovedTileIds(t.Layout, changed);
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root =>
        {
            ConfigJson.SetDisplay(root, t.Key, changed);
            ConfigJson.RemoveUnplacedTiles(root, removed); // after the change, so this display no longer counts
        }));
    }
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
        var (display, refused) = EditTarget(displayId);
        if (display is null) return refused;
        var cfg = config();
        var layout = DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        var key = DisplayResolver.EditKey(cfg, displayId, display.IsPrimary);
        return Saved(displayId, key, writer!.Save(root =>
        {
            // Pick the id from the file being saved, so a tile added by hand but not reloaded yet is never replaced.
            var taken = root["tiles"] is System.Text.Json.Nodes.JsonObject tiles ? tiles.Select(t => t.Key) : [];
            var id = TileGallery.UniqueId(item.TileId, taken);
            ConfigJson.SetTile(root, id, definition);
            ConfigJson.SetDisplay(root, key, LayoutEdits.Append(layout, id));
        }));
    }
```
Replace with:
```csharp
        var (target, refused) = Editable(displayId);
        if (target is not { } t) return refused;
        return Saved(LayoutOf(displayId, t.Key), writer!.Save(root =>
        {
            // Pick the id from the file being saved, so a tile added by hand but not reloaded yet is never replaced.
            var taken = root["tiles"] is System.Text.Json.Nodes.JsonObject tiles ? tiles.Select(tile => tile.Key) : [];
            var id = TileGallery.UniqueId(item.TileId, taken);
            ConfigJson.SetTile(root, id, definition);
            ConfigJson.SetDisplay(root, t.Key, LayoutEdits.Append(t.Layout, id));
        }));
    }

    /// <summary>The layout the display shows and the key its edits are saved under, or (null, why it cannot be edited).</summary>
    ((DisplayLayout Layout, string Key)? Target, ConfigError? Refused) Editable(string displayId)
    {
        var (display, refused) = EditTarget(displayId);
        if (display is null) return (null, refused);
        var cfg = config();
        return ((DisplayResolver.Resolve(cfg, displayId, display.IsPrimary), DisplayResolver.EditKey(cfg, displayId, display.IsPrimary)), null);
    }

    static string LayoutOf(string displayId, string key) => $"the layout of display '{displayId}' under \"{key}\"";
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
    ConfigError? Saved(string displayId, string key, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved the layout of display '{displayId}' under \"{key}\"");
```
Replace with:
```csharp
    /// <summary>After a save: logs what was saved and rebuilds the bars, or returns why nothing was saved.</summary>
    ConfigError? Saved(string what, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved {what}");
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
            Edit(l => LayoutEdits.Remove(l, current.Index, current.IsStack ? member : null));
```
Replace with:
```csharp
            Save(() => _editor.RemoveTile(DisplayId, current.Index, current.IsStack ? member : null));
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
            case LayoutCommand.RemoveTile:
                Edit(l => LayoutEdits.Remove(l, slot.Index, slot.IsStack ? member : null));
                break;
```
Replace with:
```csharp
            case LayoutCommand.RemoveTile:
                Save(() => _editor.RemoveTile(DisplayId, slot.Index, slot.IsStack ? member : null));
                break;
```

**Edit** `docs/configuration.md`, find:
````markdown
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. The tile's definition stays under `tiles`, so `+` can bring it back.
````
Replace with:
````markdown
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its definition under `tiles` is deleted in the same save; `config.json.bak` still has it. Tiles you defined by hand but have not placed anywhere yet are never deleted, and `+` lists them first.
````

**Edit** `docs/superpowers/plans/2026-10-23-milestone-3-layout.md`, find:
````markdown
| × removes the tile from this display only; the definition stays under `tiles` even if no display uses it. `+` lists such unplaced tiles first. | Removing is reversible without hand-editing, and hand-written settings (script hooks) are never lost. |
````
Replace with:
````markdown
| × removes the tile from this display only. Changed by `2026-10-27-milestone-3-feedback.md`: when no display entry (including `*`) places the tile any more, its definition under `tiles` is deleted in the same save (originally the definition always stayed). Edits that remove no tile never prune; `+` lists unplaced tiles first. | The user expects × to delete a tile nothing uses; `config.json.bak` keeps the previous file, and hand-added tiles not placed yet survive. |
````

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~LayoutEditsTests|FullyQualifiedName~ConfigJsonTests|FullyQualifiedName~HostControllerLayoutTests|FullyQualifiedName~TileGalleryTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln --disable-build-servers` then `dotnet test TaskbarTiles.sln --disable-build-servers`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 443`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config/LayoutEdits.cs src/TaskbarTiles.Host/Config/ConfigJson.cs src/TaskbarTiles.Host/HostController.cs src/TaskbarTiles.Host/UI/TileBar.cs tests/TaskbarTiles.Tests/LayoutEditsTests.cs tests/TaskbarTiles.Tests/ConfigJsonTests.cs tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs docs/configuration.md docs/superpowers/plans/2026-10-23-milestone-3-layout.md
git commit -m "feat(layout): removing a tile deletes its definition when no display places it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Moving a slot to another display (layout edit and host)

**Files:**
- Modify: `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, `src/TaskbarTiles.Host/HostController.cs`
- Test: `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`, `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`

**Interfaces:**
- Consumes: `LayoutEdits.Drop`, `LayoutEdits.Remove`, `DisplayResolver.PlacedTileIds`, `HostController.Editable`, `HostController.Saved(string what, ConfigError?)` (Task 2), `ConfigJson.SetDisplay`
- Produces:
  - `static DisplayLayout LayoutEdits.DropIncoming(DisplayLayout layout, Slot slot, int? target, DropPosition position)`
  - `ConfigError? ILayoutEditor.MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position)` (implemented by `HostController`): `target` is a config slot index of the target display, null to append; the same display is a `LayoutEdits.Drop` (null target: no change).

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`, find:
```csharp
    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
```
Replace with:
```csharp
    [Fact]
    public void An_incoming_slot_lands_before_into_or_after_the_target()
    {
        var layout = L(T("a"), T("b"));

        Same(L(T("a"), T("x"), T("b")), LayoutEdits.DropIncoming(layout, T("x"), 1, DropPosition.Before));
        Same(L(T("a"), T("x"), T("b")), LayoutEdits.DropIncoming(layout, T("x"), 0, DropPosition.After));
        Same(L(T("a"), S(Five, "b", "x")), LayoutEdits.DropIncoming(layout, T("x"), 1, DropPosition.Into));
        Same(L(S(Eight, "a", "x", "y"), T("b")), LayoutEdits.DropIncoming(layout, S(Eight, "x", "y"), 0, DropPosition.Into));
    }

    [Fact]
    public void An_incoming_slot_without_a_fitting_target_is_appended_with_its_pin()
    {
        Same(L(T("a"), T("x", pin: true)), LayoutEdits.DropIncoming(L(T("a")), T("x", pin: true), null, DropPosition.After));
        Same(L(T("a"), T("x")), LayoutEdits.DropIncoming(L(T("a")), T("x"), 7, DropPosition.Before));
        Same(L(T("x")), LayoutEdits.DropIncoming(DisplayLayout.Empty, T("x"), null, DropPosition.After));
    }

    [Fact]
    public void An_incoming_tile_the_layout_already_places_changes_nothing()
    {
        var layout = L(T("a"), S(Five, "b", "c"));

        Same(layout, LayoutEdits.DropIncoming(layout, T("c"), 0, DropPosition.Before));
        Same(layout, LayoutEdits.DropIncoming(layout, S(Eight, "a", "b"), null, DropPosition.After));
    }

    [Fact]
    public void An_incoming_stack_leaves_out_members_the_layout_already_places()
    {
        Same(L(T("a"), S(Eight, "b", "c")), LayoutEdits.DropIncoming(L(T("a")), S(Eight, "a", "b", "c"), null, DropPosition.After));
        Same(L(T("a"), T("c")), LayoutEdits.DropIncoming(L(T("a")), S(Eight, "a", "c"), null, DropPosition.After));
    }

    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, find:
```csharp
    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
```
Replace with:
```csharp
    [Fact]
    public void Moving_a_slot_to_another_display_writes_both_entries_in_one_save()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "SECOND", 0, DropPosition.Before));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "a", "c" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.True(f.Bars["SECOND"].Slots[0].Pinned); // the pin moves along
        Assert.Equal(1, f.Displays.GetProperty("primary").GetProperty("slots").GetArrayLength());
        Assert.Equal(2, f.Displays.GetProperty("SECOND").GetProperty("slots").GetArrayLength());
        Assert.Equal(new[] { "c" }, f.Displays.GetProperty("*").GetProperty("slots").EnumerateArray().Select(e => e.GetString())); // never written
    }

    [Fact]
    public void Moving_a_stack_to_a_display_that_places_one_member_moves_only_the_rest()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("MAIN", 1, "SECOND", null, DropPosition.After));

        Assert.Equal(new[] { "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "c", "b" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.False(f.Bars["SECOND"].Slots[1].IsStack);
    }

    [Fact]
    public void Moving_a_tile_to_a_display_that_already_places_it_only_removes_it()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.Null(f.Host.MoveToDisplay("SECOND", 0, "MAIN", null, DropPosition.After));

        Assert.Equal(1, f.Writer.Saves);
        Assert.Empty(f.Bars["SECOND"].Tiles);
        Assert.Equal(new[] { "a", "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal(0, f.Displays.GetProperty("SECOND").GetProperty("slots").GetArrayLength());
        Assert.Contains("c", f.TileIds);
    }

    [Fact]
    public void A_display_without_tiles_accepts_a_move()
    {
        var f = new Fixture(Config.Replace("\"*\": { \"slots\": [\"c\"] }", "\"*\": { \"slots\": [] }"), ("MAIN", true), ("SECOND", false));
        Assert.Empty(f.Bars["SECOND"].Slots);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "SECOND", null, DropPosition.After));

        Assert.Equal(new[] { "a" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Moving_within_one_display_is_a_drop()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "MAIN", 1, DropPosition.After));
        Assert.Null(f.Host.MoveToDisplay("MAIN", 0, "MAIN", null, DropPosition.After)); // nothing to drop by

        Assert.Equal(1, f.Writer.Saves);
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Moving_from_or_to_an_unknown_display_or_a_stale_slot_saves_nothing()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        Assert.NotNull(f.Host.MoveToDisplay("GONE", 0, "SECOND", null, DropPosition.After));
        Assert.NotNull(f.Host.MoveToDisplay("MAIN", 0, "GONE", null, DropPosition.After));
        Assert.Null(f.Host.MoveToDisplay("MAIN", 9, "SECOND", null, DropPosition.After));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~LayoutEditsTests|FullyQualifiedName~HostControllerLayoutTests"`
Expected: build FAIL: `LayoutEdits.DropIncoming` and `HostController.MoveToDisplay` do not exist.

- [ ] **Step 3: Implement the move**

**Edit** `src/TaskbarTiles.Host/Config/LayoutEdits.cs`, find:
```csharp
    /// <summary>Adds a plain tile at the end.</summary>
```
Replace with:
```csharp
    /// <summary>
    /// A slot dragged here from another display. Tiles this layout already places are left out (a stack keeps the rest
    /// and becomes a plain tile with one left; with none left nothing changes). With a <paramref name="target"/> that
    /// fits it lands before, into or after that slot like <see cref="Drop"/>; otherwise it is appended. A plain tile keeps
    /// its pin, a stack its rotate.
    /// </summary>
    public static DisplayLayout DropIncoming(DisplayLayout layout, Slot slot, int? target, DropPosition position)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        Slot? incoming = slot switch
        {
            TileSlot t => placed.Contains(t.TileId) ? null : t,
            StackSlot s => Collapse(new StackSlot(s.TileIds.Where(id => !placed.Contains(id)).ToList(), s.Rotate)),
            _ => null,
        };
        if (incoming is null) return layout;

        var appended = new DisplayLayout(layout.Slots.Append(incoming).ToList());
        return target is { } t2 && Valid(layout, t2) ? Drop(appended, appended.Slots.Count - 1, t2, position) : appended;
    }

    /// <summary>Adds a plain tile at the end.</summary>
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
    ConfigError? RemoveTile(string displayId, int index, int? member);
}
```
Replace with:
```csharp
    ConfigError? RemoveTile(string displayId, int index, int? member);

    /// <summary>
    /// Drag and drop between displays' bars. Slot <paramref name="fromIndex"/> of the source display lands before, into or
    /// after slot <paramref name="target"/> of the target display, or at its end when target is null; tiles the target
    /// display already places only leave the source. One save writes both displays' edit keys. On the same display this
    /// is <see cref="LayoutEdits.Drop"/> (a null target changes nothing).
    /// </summary>
    ConfigError? MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position);
}
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
```
Replace with:
```csharp
    public ConfigError? MoveToDisplay(string fromDisplayId, int fromIndex, string toDisplayId, int? target, DropPosition position)
    {
        if (fromDisplayId == toDisplayId)
            return target is { } to ? EditLayout(fromDisplayId, l => LayoutEdits.Drop(l, fromIndex, to, position)) : null;

        var (source, refused) = Editable(fromDisplayId);
        if (source is not { } s) return refused;
        var (destination, refusedTarget) = Editable(toDisplayId);
        if (destination is not { } d) return refusedTarget;
        if (s.Layout.Slots.ElementAtOrDefault(fromIndex) is not { } slot) return null; // a stale index: nothing to move

        var newSource = LayoutEdits.Remove(s.Layout, fromIndex);
        var newTarget = LayoutEdits.DropIncoming(d.Layout, slot, target, position);
        return Saved($"a move from display '{fromDisplayId}' (\"{s.Key}\") to display '{toDisplayId}' (\"{d.Key}\")",
            writer!.Save(root =>
            {
                ConfigJson.SetDisplay(root, s.Key, newSource);
                ConfigJson.SetDisplay(root, d.Key, newTarget);
            }));
    }

    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~LayoutEditsTests|FullyQualifiedName~HostControllerLayoutTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln --disable-build-servers` then `dotnet test TaskbarTiles.sln --disable-build-servers`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 453`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config/LayoutEdits.cs src/TaskbarTiles.Host/HostController.cs tests/TaskbarTiles.Tests/LayoutEditsTests.cs tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs
git commit -m "feat(layout): move a slot to another display in one save" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Dropping on any display's bar

**Files:**
- Create: `src/TaskbarTiles.Host/Layout/DropGeometry.cs`, `src/TaskbarTiles.Host/UI/TileBarRegistry.cs`, `tests/TaskbarTiles.Tests/DropGeometryTests.cs`, `tests/TaskbarTiles.Tests/PixelRectTests.cs`
- Modify: `src/TaskbarTiles.Host/Taskbar/PixelRect.cs`, `src/TaskbarTiles.Host/UI/TileBar.cs`, `src/TaskbarTiles.Host/App.xaml.cs`, `docs/configuration.md`
- Delete: `tests/TaskbarTiles.Tests/TileBarHitTestTests.cs`

**Interfaces:**
- Consumes: `LayoutEdits.DropPositionAt(double)`, `DropPosition`, `ILayoutEditor.MoveToDisplay` (Task 3), `NativeMethods.Rect(IntPtr)`
- Produces:
  - namespace `TaskbarTiles.Host.Layout`: `readonly record struct SlotSpan(double Left, double Width)`; `readonly record struct DropSpot(int Index, bool Into)`; `static class DropGeometry { DropSpot SpotAt(IReadOnlyList<SlotSpan> slots, double x); (int? Target, DropPosition Position) ToDrop(DropSpot spot, IReadOnlyList<int> slotIndexes); }`
  - `bool PixelRect.Contains(int x, int y)` (left/top inclusive, right/bottom exclusive)
  - namespace `TaskbarTiles.Host.UI`: `sealed class TileBarRegistry { void Add(TileBar); void Remove(TileBar); TileBar? BarAt(Point screen); }`
  - `TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor, TileBarRegistry bars)`; internal `bool HoldsPoint(Point screen)`, `(int? Target, DropPosition Position) DropAt(Point screen, SlotView? dragged)`, `void ShowDropPreview(Point screen, SlotView? dragged)`, `void ClearDropPreview()`; `TileBar.Inside` removed. Screen points are physical pixels.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/DropGeometryTests.cs`:
```csharp
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;

namespace TaskbarTiles.Tests;

public class DropGeometryTests
{
    static readonly SlotSpan[] TwoSlots = [new(0, 100), new(106, 100)];

    [Theory]
    [InlineData(-5, 0, false)]  // left of the first slot
    [InlineData(10, 0, false)]  // left part of the first slot
    [InlineData(50, 0, true)]   // middle of the first slot
    [InlineData(90, 1, false)]  // right part of the first slot
    [InlineData(103, 1, false)] // the 6 DIP between the slots
    [InlineData(150, 1, true)]  // middle of the second slot
    [InlineData(300, 2, false)] // past the last slot: », + and Done
    public void Spot_at_a_point(double x, int index, bool into) =>
        Assert.Equal(new DropSpot(index, into), DropGeometry.SpotAt(TwoSlots, x));

    [Fact]
    public void A_bar_without_slots_has_only_the_first_gap() =>
        Assert.Equal(new DropSpot(0, false), DropGeometry.SpotAt([], 25));

    [Fact]
    public void Into_a_slot_drops_into_its_config_index() =>
        Assert.Equal(((int?)5, DropPosition.Into), DropGeometry.ToDrop(new DropSpot(1, true), [2, 5]));

    [Fact]
    public void A_gap_drops_before_the_slot_after_it()
    {
        Assert.Equal(((int?)2, DropPosition.Before), DropGeometry.ToDrop(new DropSpot(0, false), [2, 5]));
        Assert.Equal(((int?)5, DropPosition.Before), DropGeometry.ToDrop(new DropSpot(1, false), [2, 5]));
    }

    [Fact]
    public void The_gap_after_the_last_slot_drops_after_it() =>
        Assert.Equal(((int?)5, DropPosition.After), DropGeometry.ToDrop(new DropSpot(2, false), [2, 5]));

    [Fact]
    public void A_bar_without_slots_appends() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(0, false), []));
}
```

**Create** `tests/TaskbarTiles.Tests/PixelRectTests.cs`:
```csharp
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Tests;

public class PixelRectTests
{
    [Theory]
    [InlineData(10, 20, true)]   // top-left pixel
    [InlineData(109, 59, true)]  // bottom-right pixel
    [InlineData(110, 30, false)] // right edge is outside
    [InlineData(50, 60, false)]  // bottom edge is outside (released below the bar)
    [InlineData(9, 30, false)]
    [InlineData(50, 19, false)]  // released above the bar
    public void Contains_includes_left_and_top_but_not_right_and_bottom(int x, int y, bool expected) =>
        Assert.Equal(expected, new PixelRect(10, 20, 110, 60).Contains(x, y));
}
```

**Delete** `tests/TaskbarTiles.Tests/TileBarHitTestTests.cs`

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~DropGeometryTests|FullyQualifiedName~PixelRectTests"`
Expected: build FAIL: `SlotSpan`, `DropSpot`, `DropGeometry` and `PixelRect.Contains` do not exist.

- [ ] **Step 3: Implement the drop geometry, the registry and drops on any bar**

**Create** `src/TaskbarTiles.Host/Layout/DropGeometry.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Layout;

/// <summary>Where a slot sits on a bar: DIP from the left of the bar's tile row, and its width.</summary>
public readonly record struct SlotSpan(double Left, double Width);

/// <summary>
/// A drop spot among a bar's slots: into the slot at <see cref="Index"/>, or (Into false) the gap before the slot at
/// Index. Index equal to the slot count is the gap after the last slot.
/// </summary>
public readonly record struct DropSpot(int Index, bool Into);

/// <summary>Pure drag-and-drop geometry for a bar's slots, in the order they are shown.</summary>
public static class DropGeometry
{
    /// <summary>
    /// The spot under <paramref name="x"/>: the middle 40 % of a slot is "into"; its outer parts and the space between
    /// slots are the gap on that side; left of the first slot is gap 0, right of the last the gap after it.
    /// </summary>
    public static DropSpot SpotAt(IReadOnlyList<SlotSpan> slots, double x)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (x < s.Left) return new DropSpot(i, false);
            if (x >= s.Left + s.Width) continue;
            return LayoutEdits.DropPositionAt((x - s.Left) / s.Width) switch
            {
                DropPosition.Into => new DropSpot(i, true),
                DropPosition.Before => new DropSpot(i, false),
                _ => new DropSpot(i + 1, false),
            };
        }
        return new DropSpot(slots.Count, false);
    }

    /// <summary>
    /// The layout drop for a spot, given the config indexes of the slots: into a slot, before the slot after a gap, after
    /// the last slot for the gap behind it, or (null, After) to append when the bar shows no slot.
    /// </summary>
    public static (int? Target, DropPosition Position) ToDrop(DropSpot spot, IReadOnlyList<int> slotIndexes)
    {
        if (spot.Index >= 0 && spot.Index < slotIndexes.Count)
            return (slotIndexes[spot.Index], spot.Into ? DropPosition.Into : DropPosition.Before);
        return slotIndexes.Count > 0 ? (slotIndexes[^1], DropPosition.After) : (null, DropPosition.After);
    }
}
```

**Edit** `src/TaskbarTiles.Host/Taskbar/PixelRect.cs`, find:
```csharp
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
```
Replace with:
```csharp
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>True for a pixel inside: the left and top edges belong to the rectangle, the right and bottom ones do not.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
```

**Create** `src/TaskbarTiles.Host/UI/TileBarRegistry.cs`:
```csharp
using System.Windows;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// Every display's bar, so a drag that starts on one bar can preview and drop on another display's bar. The dragging bar
/// keeps the mouse capture, so the bar under the pointer is found by screen position. UI thread only.
/// </summary>
public sealed class TileBarRegistry
{
    readonly List<TileBar> _bars = [];

    public void Add(TileBar bar)
    {
        if (!_bars.Contains(bar)) _bars.Add(bar);
    }

    public void Remove(TileBar bar) => _bars.Remove(bar);

    /// <summary>The bar in edit mode, on screen, whose window holds the screen point (physical pixels); null if none.</summary>
    public TileBar? BarAt(Point screen) => _bars.FirstOrDefault(b => b.HoldsPoint(screen));
}
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    readonly ILayoutEditor _editor;
```
Replace with:
```csharp
    readonly ILayoutEditor _editor;
    readonly TileBarRegistry _bars;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    SlotView? _dragSource;
    SlotView? _dropTarget;
    Point _dragStart;
    bool _dragging;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
```
Replace with:
```csharp
    SlotView? _dragSource;
    SlotView? _dropTarget; // the slot of this bar a drop would stack onto
    TileBar? _hoverBar; // while this bar drags: the bar under the pointer, which shows the drop preview
    Point _dragStart;
    bool _dragging;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor, TileBarRegistry bars)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
        _bars = bars;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }
```
Replace with:
```csharp
        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        bars.Add(this);
    }
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        _slots = slots.ToList();
        EndDrag();
```
Replace with:
```csharp
        _slots = slots.ToList();
        EndDrag();
        ClearDropPreview(); // another bar may be dragging over this one
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        _editing = editing;
        EndDrag();
```
Replace with:
```csharp
        _editing = editing;
        EndDrag();
        ClearDropPreview();
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        if (!_dragging) return;
        var (target, position) = DropTargetAt(e);
        SetDropTarget(position == DropPosition.Into ? target : null);
    }
```
Replace with:
```csharp
        if (_dragging) HoverAt(PointToScreen(p));
    }
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        var source = _dragSource;
        var dragged = _dragging;
        var (target, position) = DropTargetAt(e);
        EndDrag();
        if (!dragged || target is null) return;
        var from = source.Slot.Index;
        var to = target.Slot.Index;
        Edit(l => LayoutEdits.Drop(l, from, to, position));
    }
```
Replace with:
```csharp
        var dragged = _dragging ? _dragSource : null;
        var screen = PointToScreen(e.GetPosition(this));
        var bar = dragged is null ? null : _bars.BarAt(screen);
        var drop = bar?.DropAt(screen, ReferenceEquals(bar, this) ? dragged : null);
        EndDrag();
        if (dragged is null || bar is null || drop is not { } d) return; // a click, or released outside every bar: cancel
        var from = dragged.Slot.Index;
        var toDisplay = bar.DisplayId;
        Save(() => _editor.MoveToDisplay(DisplayId, from, toDisplay, d.Target, d.Position));
    }
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    /// <summary>The bar slot under the pointer (not the dragged one) and where on it: the middle 40 % means "into".</summary>
    (SlotView? Target, DropPosition Position) DropTargetAt(MouseEventArgs e)
    {
        foreach (var view in _views)
        {
            if (ReferenceEquals(view, _dragSource) || !ReferenceEquals(view.Parent, _panel) || view.ActualWidth <= 0) continue;
            var p = e.GetPosition(view);
            if (Inside(p, view.ActualWidth, view.ActualHeight)) return (view, LayoutEdits.DropPositionAt(p.X / view.ActualWidth));
        }
        return (null, DropPosition.Before);
    }

    /// <summary>True when the point lies within a slot of the given size; releasing outside the bar cancels a drag.</summary>
    internal static bool Inside(Point p, double width, double height) => p.X >= 0 && p.X < width && p.Y >= 0 && p.Y < height;
```
Replace with:
```csharp
    /// <summary>Shows where the slot would land on the bar under the pointer: this one or another display's.</summary>
    void HoverAt(Point screen)
    {
        var bar = _bars.BarAt(screen);
        if (!ReferenceEquals(bar, _hoverBar))
        {
            _hoverBar?.ClearDropPreview();
            _hoverBar = bar;
        }
        bar?.ShowDropPreview(screen, ReferenceEquals(bar, this) ? _dragSource : null);
    }

    /// <summary>True when this bar is in edit mode, on screen, and its window holds the screen point (physical pixels).</summary>
    internal bool HoldsPoint(Point screen) =>
        _editing && IsVisible && NativeMethods.Rect(_hwnd) is { } rect
        && rect.Contains((int)Math.Floor(screen.X), (int)Math.Floor(screen.Y));

    /// <summary>
    /// Where a slot released at the screen point lands on this bar: before, into or after one of its shown slots, or
    /// (null, After) to append when it shows none. <paramref name="dragged"/> is the slot itself when the drag started here.
    /// </summary>
    internal (int? Target, DropPosition Position) DropAt(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        return DropGeometry.ToDrop(spot, candidates.Select(v => v.Slot.Index).ToList());
    }

    /// <summary>While a drag (from this bar or another) is over this bar: highlights the slot a drop would stack onto.</summary>
    internal void ShowDropPreview(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        SetDropTarget(spot.Into ? candidates[spot.Index] : null);
    }

    internal void ClearDropPreview() => SetDropTarget(null);

    /// <summary>The shown slots a drop can land by (all but the dragged one) and the spot under the screen point.</summary>
    (List<SlotView> Candidates, DropSpot Spot) SpotAt(Point screen, SlotView? dragged)
    {
        var candidates = _views.Where(v => ReferenceEquals(v.Parent, _panel) && !ReferenceEquals(v, dragged)).ToList();
        // Layout offsets, not rendered positions: render transforms must not move the drop spots.
        var spans = candidates.Select(v => new SlotSpan(VisualTreeHelper.GetOffset(v).X, v.ActualWidth)).ToList();
        return (candidates, DropGeometry.SpotAt(spans, _panel.PointFromScreen(screen).X));
    }
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        if (source is not null) source.Opacity = 1;
        SetDropTarget(null);
        if (IsMouseCaptured) ReleaseMouseCapture();
```
Replace with:
```csharp
        if (source is not null) source.Opacity = 1;
        var hover = _hoverBar;
        _hoverBar = null;
        hover?.ClearDropPreview();
        if (IsMouseCaptured) ReleaseMouseCapture();
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    protected override void OnClosed(EventArgs e)
    {
        CloseOverflow();
```
Replace with:
```csharp
    protected override void OnClosed(EventArgs e)
    {
        EndDrag();
        ClearDropPreview();
        _bars.Remove(this);
        CloseOverflow();
```

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
    TileCommands? _commands;
```
Replace with:
```csharp
    TileCommands? _commands;
    readonly TileBarRegistry _bars = new();
```

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!, _host!);
```
Replace with:
```csharp
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!, _host!, _bars);
```

**Edit** `docs/configuration.md`, find:
````markdown
Right-click a tile or the bar → **Edit layout**. All taskbars switch to edit mode at once (so a display without tiles can get some), but each one edits only its own display's tiles.
````
Replace with:
````markdown
Right-click a tile or the bar → **Edit layout**. All taskbars switch to edit mode at once (so a display without tiles can get some). Each one edits its own display's tiles; dragging a tile onto another taskbar moves it to that display.
````

**Edit** `docs/configuration.md`, find:
````markdown
- Drag a tile onto the left or right part of another tile to move it there.
````
Replace with:
````markdown
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there.
- Drag a tile onto another monitor's taskbar to move it to that display: before, into or after the tile under the pointer, as on its own taskbar, or at the end when you release it anywhere else on that taskbar (`+`, **Done**, or a taskbar without tiles). If that display already shows the tile, it only leaves this one. Both displays' entries are saved together. Releasing outside every taskbar cancels the drag. Tiles inside the `»` popup cannot be dragged.
````

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~DropGeometryTests|FullyQualifiedName~PixelRectTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln --disable-build-servers` then `dotnet test TaskbarTiles.sln --disable-build-servers`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 463`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Layout/DropGeometry.cs src/TaskbarTiles.Host/UI/TileBarRegistry.cs src/TaskbarTiles.Host/Taskbar/PixelRect.cs src/TaskbarTiles.Host/UI/TileBar.cs src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/DropGeometryTests.cs tests/TaskbarTiles.Tests/PixelRectTests.cs tests/TaskbarTiles.Tests/TileBarHitTestTests.cs docs/configuration.md
git commit -m "feat(ui): drop a dragged tile on any display's bar" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Drag visuals: ghost and sliding gap

**Files:**
- Create: `src/TaskbarTiles.Host/UI/DragGhost.cs`, `tests/TaskbarTiles.Tests/DragGhostTests.cs`
- Modify: `src/TaskbarTiles.Host/Layout/DropGeometry.cs`, `src/TaskbarTiles.Host/Taskbar/NativeMethods.cs`, `src/TaskbarTiles.Host/UI/TileBar.cs`, `docs/configuration.md`
- Test: `tests/TaskbarTiles.Tests/DropGeometryTests.cs`

**Interfaces:**
- Consumes: `DropSpot`, `DropGeometry.SpotAt` (Task 4), `TileBar.ShowDropPreview`, `ClearDropPreview`, `HoverAt`, `EndDrag` (Task 4)
- Produces:
  - `static bool DropGeometry.OpensGap(DropSpot? spot, int dragged)`; `static IReadOnlyList<double> DropGeometry.Shifts(int count, int dragged, DropSpot? spot, double gap)` (`dragged` is the dragged slot's position among the bar's `count` shown slots, -1 when the drag started on another bar; spot indexes skip the dragged slot)
  - `NativeMethods.WS_EX_TRANSPARENT` (0x20), `NativeMethods.SWP_NOSIZE` (0x1)
  - `sealed class DragGhost : Window { DragGhost(FrameworkElement slot, Point grab); void MoveTo(Point screen); static long ExStyle(long current); static BitmapSource Snapshot(FrameworkElement element, double scale); }`
  - `TileBar`: `DropGapDip` = 40; in edit mode the bar keeps 40 DIP of transparent room after Done.

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/DropGeometryTests.cs`, find:
```csharp
    [Fact]
    public void A_bar_without_slots_appends() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(0, false), []));
}
```
Replace with:
```csharp
    [Fact]
    public void A_bar_without_slots_appends() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(0, false), []));

    [Fact]
    public void On_another_bar_the_slots_after_the_gap_slide_right()
    {
        Assert.True(DropGeometry.OpensGap(new DropSpot(1, false), dragged: -1));
        Assert.Equal(new double[] { 0, 40, 40 }, DropGeometry.Shifts(3, dragged: -1, new DropSpot(1, false), 40));
    }

    [Fact]
    public void Stacking_or_no_spot_opens_no_gap()
    {
        Assert.False(DropGeometry.OpensGap(new DropSpot(1, true), -1));
        Assert.False(DropGeometry.OpensGap(null, -1));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, -1, new DropSpot(1, true), 40));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, -1, null, 40));
    }

    [Fact]
    public void The_dragged_slots_own_place_opens_no_gap()
    {
        Assert.False(DropGeometry.OpensGap(new DropSpot(1, false), dragged: 1));
        Assert.Equal(new double[] { 0, 0, 0 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(1, false), 40));
    }

    [Fact]
    public void On_the_source_bar_the_dragged_slot_slides_with_the_slots_after_the_gap()
    {
        Assert.Equal(new double[] { 40, 40, 40 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(0, false), 40));
        Assert.Equal(new double[] { 0, 0, 40 }, DropGeometry.Shifts(3, dragged: 0, new DropSpot(1, false), 40));
    }

    [Fact]
    public void A_gap_after_the_last_slot_moves_no_slot()
    {
        Assert.True(DropGeometry.OpensGap(new DropSpot(2, false), dragged: 1)); // », + and Done still slide
        Assert.Equal(new double[] { 0, 0, 0 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(2, false), 40));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, dragged: -1, new DropSpot(2, false), 40));
    }
}
```

**Create** `tests/TaskbarTiles.Tests/DragGhostTests.cs`:
```csharp
using System.Text.Json;
using System.Windows;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class DragGhostTests
{
    [Fact]
    public void Ghost_window_is_click_through_and_never_activates()
    {
        const long layered = 0x00080000;

        var style = DragGhost.ExStyle(layered);

        Assert.Equal(layered, style & layered);            // keeps what WPF set
        Assert.Equal(0x20L, style & 0x20L);                // WS_EX_TRANSPARENT: mouse input passes through
        Assert.Equal(0x08000000L, style & 0x08000000L);    // WS_EX_NOACTIVATE: never takes focus
        Assert.Equal(0x80L, style & 0x80L);                // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry
    }

    [Fact]
    public void Snapshot_renders_the_slot_at_the_display_scale() => Sta.Run(() =>
    {
        var runtime = new TileRuntime("a", "fake", 132, () => new FakeConnector(), JsonDocument.Parse("{}").RootElement.Clone(),
            new ImmediateDispatcher(), new CollectingLog());
        var commands = new TileCommands(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());
        var view = new SlotView(new BarSlot(0, [runtime], false, null), () => false, commands, new CollectingLog());
        view.Measure(new Size(132, 40));
        view.Arrange(new Rect(0, 0, 132, 40));

        var bitmap = DragGhost.Snapshot(view, 1.5);

        Assert.Equal(198, bitmap.PixelWidth);
        Assert.Equal(60, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        view.Detach();
    });
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~DropGeometryTests|FullyQualifiedName~DragGhostTests"`
Expected: build FAIL: `DropGeometry.OpensGap`, `DropGeometry.Shifts` and `DragGhost` do not exist.

- [ ] **Step 3: Implement the ghost and the sliding gap**

**Edit** `src/TaskbarTiles.Host/Layout/DropGeometry.cs`, find:
```csharp
        return slotIndexes.Count > 0 ? (slotIndexes[^1], DropPosition.After) : (null, DropPosition.After);
    }
}
```
Replace with:
```csharp
        return slotIndexes.Count > 0 ? (slotIndexes[^1], DropPosition.After) : (null, DropPosition.After);
    }

    /// <summary>
    /// True when the spot opens a gap: it is not "into", and not the dragged slot's own place on its own bar.
    /// <paramref name="dragged"/> is the dragged slot's position among the bar's shown slots, or -1 on another bar.
    /// </summary>
    public static bool OpensGap(DropSpot? spot, int dragged) => spot is { Into: false } s && s.Index != dragged;

    /// <summary>
    /// How far each of a bar's <paramref name="count"/> shown slots slides right to open the gap: <paramref name="gap"/>
    /// for the slots after it, 0 for the ones before. Spot indexes skip the dragged slot (<paramref name="dragged"/>, or -1
    /// when the drag started on another bar); the dragged slot itself slides when the gap is left of it.
    /// </summary>
    public static IReadOnlyList<double> Shifts(int count, int dragged, DropSpot? spot, double gap)
    {
        var shifts = new double[count];
        if (!OpensGap(spot, dragged)) return shifts;
        var at = spot!.Value.Index;
        for (var j = 0; j < count; j++)
        {
            var position = dragged >= 0 && j > dragged ? j - 1 : j;
            if (position >= at) shifts[j] = gap;
        }
        return shifts;
    }
}
```

**Edit** `src/TaskbarTiles.Host/Taskbar/NativeMethods.cs`, find:
```csharp
    public const long WS_EX_NOACTIVATE = 0x08000000;
```
Replace with:
```csharp
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const uint SWP_NOSIZE = 0x0001;
```

**Create** `src/TaskbarTiles.Host/UI/DragGhost.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// A see-through picture of the dragged slot that follows the pointer across displays. Topmost, never activated and
/// click-through (WS_EX_TRANSPARENT), so it never takes focus or mouse input from the bar that holds the capture.
/// </summary>
public sealed class DragGhost : Window
{
    const double GhostOpacity = 0.7;
    readonly Point _grab; // where the pointer holds the slot, in DIP from the slot's top-left corner
    readonly IntPtr _hwnd;

    public DragGhost(FrameworkElement slot, Point grab)
    {
        _grab = grab;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Math.Max(1, slot.ActualWidth);
        Height = Math.Max(1, slot.ActualHeight);
        Opacity = GhostOpacity;
        Content = new Image { Source = Snapshot(slot, VisualTreeHelper.GetDpi(slot).DpiScaleX), Stretch = Stretch.Fill };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ExStyle(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64())));
    }

    /// <summary>The ghost's extended styles: click-through, never activated, no taskbar button.</summary>
    public static long ExStyle(long current) => current | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

    /// <summary>The element as it looks now, at <paramref name="scale"/> physical pixels per DIP.</summary>
    public static BitmapSource Snapshot(FrameworkElement element, double scale)
    {
        var width = Math.Max(1, element.ActualWidth);
        var height = Math.Max(1, element.ActualHeight);
        // Through a VisualBrush, so the element's offset in its parent does not shift the picture.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element)
            {
                Viewbox = new Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
            }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Moves the ghost so the grabbed point sits under <paramref name="screen"/> (physical pixels); shows it the first time.</summary>
    public void MoveTo(Point screen)
    {
        var first = !IsVisible;
        Place(screen);
        if (!first) return;
        Show(); // ShowActivated is false and the window is WS_EX_NOACTIVATE: it never takes activation
        Place(screen); // again, now at the DPI of the monitor it landed on
    }

    void Place(Point screen)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        SetWindowPos(_hwnd, HWND_TOPMOST, (int)Math.Round(screen.X - _grab.X * scale), (int)Math.Round(screen.Y - _grab.Y * scale),
            0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
using System.Windows.Media;
using System.Windows.Threading;
```
Replace with:
```csharp
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    const double DragThreshold = 4;
```
Replace with:
```csharp
    const double DragThreshold = 4;
    const double DropGapDip = 40; // how far a drop preview slides tiles apart; edit mode keeps this much room after Done
    static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(150);
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    TileBar? _hoverBar; // while this bar drags: the bar under the pointer, which shows the drop preview
```
Replace with:
```csharp
    TileBar? _hoverBar; // while this bar drags: the bar under the pointer, which shows the drop preview
    DragGhost? _ghost;
    Point _grab; // where the pointer holds the dragged slot, in DIP from its top-left corner
    readonly Dictionary<FrameworkElement, double> _slides = []; // elements of this bar slid by a drop preview, and how far
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        _dragStart = e.GetPosition(this);
```
Replace with:
```csharp
        _dragStart = e.GetPosition(this);
        _grab = e.GetPosition(view);
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
            _dragging = true;
            _dragSource.Opacity = 0.35;
```
Replace with:
```csharp
            _dragging = true;
            _ghost = CreateGhost(_dragSource); // before the fade, so the ghost shows the tile at full strength
            _dragSource.Opacity = 0.35;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    void HoverAt(Point screen)
    {
        var bar = _bars.BarAt(screen);
```
Replace with:
```csharp
    void HoverAt(Point screen)
    {
        _ghost?.MoveTo(screen);
        var bar = _bars.BarAt(screen);
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    /// <summary>While a drag (from this bar or another) is over this bar: highlights the slot a drop would stack onto.</summary>
    internal void ShowDropPreview(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        SetDropTarget(spot.Into ? candidates[spot.Index] : null);
    }

    internal void ClearDropPreview() => SetDropTarget(null);
```
Replace with:
```csharp
    /// <summary>
    /// While a drag (from this bar or another) is over this bar: highlights the slot a drop would stack onto, or slides
    /// the slots after the drop gap (and », + and Done) apart. Render transforms only, so nothing reflows.
    /// </summary>
    internal void ShowDropPreview(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        SetDropTarget(spot.Into ? candidates[spot.Index] : null);

        var shown = _panel.Children.OfType<FrameworkElement>().ToList();
        var slots = shown.OfType<SlotView>().ToList();
        var draggedAt = dragged is null ? -1 : slots.IndexOf(dragged);
        var shifts = DropGeometry.Shifts(slots.Count, draggedAt, spot, DropGapDip);
        var chromeShift = DropGeometry.OpensGap(spot, draggedAt) ? DropGapDip : 0;
        foreach (var element in shown)
            SlideTo(element, element is SlotView view ? shifts[slots.IndexOf(view)] : chromeShift);
    }

    /// <summary>Ends a drop preview on this bar: no highlight, and every slid element snaps back without animation.</summary>
    internal void ClearDropPreview()
    {
        SetDropTarget(null);
        foreach (var element in _slides.Keys)
        {
            if (element.RenderTransform is not TranslateTransform slide) continue;
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = 0;
        }
        _slides.Clear();
    }

    /// <summary>Animates the element's horizontal slide to <paramref name="x"/> DIP over 150 ms, unless it is already headed there.</summary>
    void SlideTo(FrameworkElement element, double x)
    {
        if ((_slides.TryGetValue(element, out var current) ? current : 0) == x) return;
        _slides[element] = x;
        if (element.RenderTransform is not TranslateTransform slide) element.RenderTransform = slide = new TranslateTransform();
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, SlideDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    DragGhost? CreateGhost(SlotView view)
    {
        try { return new DragGhost(view, _grab); }
        catch (Exception ex)
        {
            _log.Error("Could not create the drag ghost; dragging without it", ex);
            return null;
        }
    }
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        var hover = _hoverBar;
        _hoverBar = null;
        hover?.ClearDropPreview();
```
Replace with:
```csharp
        var ghost = _ghost;
        _ghost = null;
        ghost?.Close();
        var hover = _hoverBar;
        _hoverBar = null;
        hover?.ClearDropPreview();
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        var chrome = _editing ? AddWidth + GapDip + DoneWidth : 0;
```
Replace with:
```csharp
        // In edit mode the bar keeps DropGapDip of transparent room after Done, so a drop preview can slide tiles apart
        // without resizing the bar or reflowing its tiles.
        var chrome = _editing ? AddWidth + GapDip + DoneWidth + DropGapDip : 0;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
        if (showChrome)
        {
            shown.Add(_addButton);
            shown.Add(_doneButton);
        }
```
Replace with:
```csharp
        if (showChrome)
        {
            shown.Add(_addButton);
            shown.Add(_doneButton);
        }
        _panel.Margin = new Thickness(0, InsetDip, showChrome ? DropGapDip : 0, InsetDip);
```

**Edit** `docs/configuration.md`, find:
````markdown
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there.
````
Replace with:
````markdown
- Drag a tile onto the left or right part of another tile, or between two tiles, to move it there. While you drag, a see-through copy of the tile follows the pointer (across monitors too), the tile itself fades, and the tiles of the taskbar under the pointer slide apart where it will land. Over the middle of a tile nothing slides; that tile lights up instead.
````

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --disable-build-servers --filter "FullyQualifiedName~DropGeometryTests|FullyQualifiedName~DragGhostTests|FullyQualifiedName~SlotViewTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln --disable-build-servers` then `dotnet test TaskbarTiles.sln --disable-build-servers`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 470`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/UI/DragGhost.cs src/TaskbarTiles.Host/Layout/DropGeometry.cs src/TaskbarTiles.Host/Taskbar/NativeMethods.cs src/TaskbarTiles.Host/UI/TileBar.cs tests/TaskbarTiles.Tests/DropGeometryTests.cs tests/TaskbarTiles.Tests/DragGhostTests.cs docs/configuration.md
git commit -m "feat(ui): drag ghost across monitors; tiles slide apart where the drop lands" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Verify on screen with the user

**Files:**
- Outside the repo (user only): `%LOCALAPPDATA%\TaskbarTiles\app\`, `%LOCALAPPDATA%\TaskbarTiles\config.json`, `config.json.bak`

**Interfaces:**
- Consumes: everything above
- Produces: the user's confirmation that the feedback round works on the real taskbars

The agent prepares; the user installs and checks (Steps 2-6) and reports back. Do not edit the user's `config.json`, do not run `tools\install.ps1` against `%LOCALAPPDATA%` and do not start or stop `TaskbarTiles.exe` from an agent session.

- [ ] **Step 1 (agent): Final local check**

```powershell
dotnet build TaskbarTiles.sln --disable-build-servers
dotnet test TaskbarTiles.sln --disable-build-servers
```
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 470`.

- [ ] **Step 2 (user): Back up and install**

In the user's own terminal, from the repo root:
```powershell
Copy-Item "$env:LOCALAPPDATA\TaskbarTiles\config.json" "$env:LOCALAPPDATA\TaskbarTiles\config.before-feedback.json"
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```
Expected: the app restarts with the same tiles.

- [ ] **Step 3 (user): Tile menu**

Right-click a plain tile. Expected: no Move left / Move right; **Create stack with ▸** lists the other plain tiles of this display by title (or id). Pick one: the two become a stack where the clicked tile was, the clicked tile shown first. Right-click the stack: **Add to stack ▸** lists the remaining plain tiles; pick one and it joins the stack. With no other plain tile on the display the item is greyed out. Do the same on a tile inside the `»` popup (make space tight as in milestone 3): same menu, same result.

- [ ] **Step 4 (user): × deletes unused definitions**

In edit mode press × on a tile that only this display places. Open `config.json`: the tile is gone from `"tiles"` as well; `config.json.bak` still has it. Press × on a tile that `"*"` (or another monitor's entry) also places: its definition stays. A tile added by hand under `"tiles"` but not placed anywhere is still there after both.

- [ ] **Step 5 (user, two monitors): Drag across monitors**

In edit mode drag a tile from one monitor's taskbar to the other's:
- A see-through copy follows the pointer across the monitor boundary; the original fades; nothing takes focus (the window you were in stays active after Done).
- Over the other taskbar the tiles slide apart (about 150 ms) where it will land; over a tile's middle nothing slides and the tile lights up.
- Release between two tiles: it lands there; on a tile's middle: it joins/forms a stack; on `+`/Done or empty taskbar space: at the end. A monitor that showed only `+` and Done accepts it.
- Dragging a tile the other monitor already shows only removes it from this one.
- Releasing outside every taskbar, or pressing Esc, puts everything back without a change. `config.json` gets one save per drop (one `.bak` step), with the other monitor's device id or `primary` as the key; `"*"` is unchanged.

- [ ] **Step 6 (user): Record**

Ask the user to confirm Steps 2-5 (5 only with two monitors) and note anything that failed in `.superpowers/sdd/progress.md` for a fix round. The user may restore `config.before-feedback.json` or keep the new layout.

---

## Self-review notes

- Feedback coverage:
  1. Tile menu: Move left/right removed, "Create stack with ▸" on plain tiles (disabled without choices, never a one-member stack), "Add to stack ▸" on stacks, Remove from stack / Pause-Resume / Remove tile kept, overflow-popup slots use the same menu builder (`FillSlotMenu` reads all of `_slots`): Task 1.
  2. × / Remove tile prune definitions nothing places, in the same save; other edits never prune; gallery unchanged; `docs/configuration.md` and the earlier plan's decision updated: Task 2.
  3. Drag across monitors: one save via `MoveToDisplay` with `EditKey` rules and no `*` write (Task 3); registry, screen-point hit test, drops on empty bars, before/into/after and append rules, only bar slots draggable (Task 4).
  4. Drag visuals: ghost window (topmost, no-activate, click-through, snapshot via `VisualBrush` + `RenderTargetBitmap`), fade kept, 150 ms `TranslateTransform` slide, no gap over a slot's middle 40 %, snap back on drop and cancel, no layout width change during the drag: Task 5.
- Pure and unit-tested: `TileMenu.Entries`/`StackChoices` (Task 1), `LayoutEdits.RemovedTileIds`, `ConfigJson.RemoveUnplacedTiles`, `HostController.RemoveTile` (Task 2), `LayoutEdits.DropIncoming`, `HostController.MoveToDisplay` (Task 3), `DropGeometry.SpotAt`/`ToDrop`, `PixelRect.Contains` (Task 4), `DropGeometry.OpensGap`/`Shifts`, `DragGhost.ExStyle`/`Snapshot` (Task 5). The WPF wiring (menu items, capture, cross-bar hover, animation, ghost window) is covered by Task 6.
- Type consistency checked: `SlotMenuState`, `MenuEntry`, `StackChoice`, `MenuSlot` (Task 1); `ILayoutEditor.RemoveTile` (Task 2) and `MoveToDisplay` (Task 3) as used by `TileBar` (Tasks 2, 4); `DropSpot`, `SlotSpan` (Task 4) as used by `Shifts` (Task 5); `TileBar` constructor (Task 4) as called by `App`.
- Checked while writing the plan: every code block, applied in order to a fresh clone of `feat/milestone-3-layout` (`00b4939`, 429 tests), builds with 0 warnings after each task and passes `dotnet test TaskbarTiles.sln --disable-build-servers` with the counts given in each task (435, 443, 453, 463, 470; 23 of them are the integration tests from milestones 1 and 2).
