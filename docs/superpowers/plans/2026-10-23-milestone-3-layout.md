# Taskbar Tiles — Milestone 3: Layout (Stacks, Compact and Overflow, Edit Mode) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stacks that rotate one member at a time, a reflow that turns tiles compact and then moves them into a `»` overflow tile when space runs out, and an edit mode on every display that rearranges tiles and writes `config.json`.

**Architecture:** Everything that decides something is pure and unit-tested: `LayoutEdits` (slot list edits), `ConfigJson` + `ConfigStore.Save` (atomic rewrite of `config.json` with a `.bak`), `LayoutEngine` (full → compact → overflow), `StackRotation` (rotation, hover, wheel, urgent takeover), `TileGallery` (what `+` offers) and `TileMenu` (menu entries). `HostController` turns each display's config slots into `BarSlot`s, runs a runtime for every placed tile (stack members included) and implements `ILayoutEditor`, which saves a layout change under the display's edit key. The WPF side is thin: `SlotView` renders one slot (single tile or stack), `TileBar` arranges slot views with `LayoutEngine`, hosts the overflow popup and edit mode, and sends every change through `ILayoutEditor`.

**Tech Stack:** .NET 9, WPF, C# latest, xUnit 2.9, System.Text.Json (`JsonNode`), Windows PowerShell 5.1 for the install script.

Spec: `docs/superpowers/specs/2026-10-22-taskbar-tiles-design.md` (milestone 3 = "Layout. Stacks, compact and overflow, edit mode per display writing config.json."; sections Layout and overflow, Interaction, Configuration). Earlier plans: `docs/superpowers/plans/2026-10-22-milestone-1-host-and-media.md`, `docs/superpowers/plans/2026-10-22-milestone-2-script-plugin.md`. The code on `main` is the truth where it differs from those plans; every "Edit" and "Replace" below is written against the current code.

## Global Constraints

- Target frameworks: host and tests `net9.0-windows10.0.19041.0`; contracts and the script plugin `net9.0`. No new NuGet packages.
- Plugins never run on the UI thread (construction, `StartAsync`, actions, popups, `Resumed`, cancellation callbacks, `DisposeAsync`). Layout code never calls a connector.
- Tile widths: full width per connector (`media` 268, default 132 DIP); compact width 40 DIP (icon only, title in the tooltip); the `»` overflow tile is 40 DIP. Gap between tiles 6 DIP; tiles sit 4 DIP inside the taskbar's top and bottom edges.
- Layout order (spec): everything full if it fits; else compact unpinned tiles from the right end; else move tiles from the right end into the `»` tile, pinned tiles last. Reflow on every free-zone change and every slot change. A stack counts as one tile.
- Stacks: rotate every `rotate`, default `5s`, valid from `1s` to `1h` (anything else is a config error); pause on hover; mouse wheel cycles; position dots; an item with `TileState.Urgent = true` takes over the stack until clicked; every member's runtime runs.
- `config.json` writes (edit mode and the tile menu): the whole file is rewritten (comments and custom formatting are lost; unknown keys and key order are kept), indented, written to `config.json.tmp` and swapped in with `File.Replace`, the previous file kept as `config.json.bak`. A file that does not parse is never written over. The watcher ignores our own write.
- Edit mode writes the display key: the device id if it has its own entry, else `primary` on the primary display, else the device id. `*` is never written by edit mode.
- Data folder: `%LOCALAPPDATA%\TaskbarTiles\` (`config.json`, `config.json.bak`, `scripts\`, `plugins\`, `logs\`); the app is installed in `%LOCALAPPDATA%\TaskbarTiles\app`.
- Agent sessions never install into `%LOCALAPPDATA%` (the Claude desktop app redirects those writes to a private copy). Agents publish into the repo's git-ignored `artifacts\` folder; the user installs with `tools\install.ps1` from their own terminal.
- WPF projects (Host, Tests) do not get `System.IO` from implicit usings: add `using System.IO;` explicitly in every file that needs it.
- Tests are xUnit in `tests/TaskbarTiles.Tests`. WPF controls are tested on an STA thread (`Sta.Run`, Task 8) without showing a window. Tests that start real processes carry `[Trait("Category", "Integration")]`. Run everything with `dotnet test TaskbarTiles.sln`.
- `dotnet build TaskbarTiles.sln` stays at 0 warnings.
- Every commit message ends with a trailer naming the model that wrote the commit. The commands below show `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; use your own model name if it differs.

## File Structure

```
.gitignore                                      + artifacts/
tools/install.ps1                               user-run build + install + Startup shortcut + start (new)
docs/configuration.md                           config.json, stacks, overflow, edit mode, rewrite and .bak (new)
docs/script-connectors.md                       unknown keys inside a hook are errors
src/TaskbarTiles.Host/
  App.xaml.cs                                   passes ConfigStore as the writer; TileBar gets the layout editor
  HostController.cs                             ITileBarView.SetSlots/SetEditing, ILayoutEditor, BarSlots, all stack members run
  Config/ConfigParser.cs                        rotate bounded to 1s..1h; DefaultRotate public
  Config/DisplayResolver.cs                     EditKey, PlacedTileIds (VisibleTileIds removed)
  Config/LayoutEdits.cs                         DropPosition; pure edits of a display's slots (new)
  Config/ConfigJson.cs                          config.json text <-> JsonObject; SetDisplay, SetTile (new)
  Config/ConfigStore.cs                         IConfigWriter; Save (atomic, .bak, no reload echo)
  Layout/LayoutEngine.cs                        SlotMode, LayoutItem, LayoutResult, Compute (new)
  Layout/StackRotation.cs                       which stack member shows (new)
  Layout/BarSlot.cs                             a slot as a bar shows it (new)
  Layout/TileGallery.cs                         GalleryItem, ScriptTemplate(s), TileGallery (new)
  UI/FitTiles.cs                                deleted (replaced by LayoutEngine)
  UI/TileText.cs                                monogram and compact tooltip (new)
  UI/TileView.xaml(.cs)                         compact look, FlyoutAnchor, inline buttons via TileCommands
  UI/FlyoutWindow.cs                            ShowPanel for any content (overflow popup, gallery), public Text
  UI/SlotView.cs                                one slot: tile or stack with dots, rotation, edit look (new)
  UI/TileBar.cs                                 LayoutEngine reflow, overflow popup, layout menu, edit mode
  UI/TileCommands.cs                            LayoutCommand, SlotMenuState, BarCommand.EditLayout/DoneEditing, TileMenu
  UI/ConfigNotice.cs                            text for a layout that could not be saved
  UI/Styles.xaml                                BarButton, RemoveButton styles
src/TaskbarTiles.Plugins.Script/ScriptSettings.cs   unknown keys inside a hook are errors
tests/TaskbarTiles.Tests/
  LayoutEditsTests.cs, ConfigJsonTests.cs, ConfigStoreSaveTests.cs, LayoutEngineTests.cs, StackRotationTests.cs,
  HostControllerLayoutTests.cs, TileGalleryTests.cs, TileTextTests.cs, SlotViewTests.cs (new)
  ConfigParserTests.cs, DisplayResolverTests.cs, TestDoubles.cs, TileCommandsTests.cs, ConfigNoticeTests.cs,
  ScriptSettingsTests.cs (modified); FitTilesTests.cs (deleted)
```

### Contract and API changes

No change to `TaskbarTiles.Contracts`: plugins are unaffected. Host-internal changes other tasks rely on:

| Change | Why |
|---|---|
| `ITileBarView.SetTiles(IReadOnlyList<TileRuntime>)` → `SetSlots(IReadOnlyList<BarSlot>)`, + `SetEditing(bool)` | A bar must know stacks, pins and config indexes, and edit mode is switched by the host. |
| `sealed record BarSlot(int Index, IReadOnlyList<TileRuntime> Members, bool Pinned, TimeSpan? Rotate)` | One slot as a bar shows it; `Index` is what layout edits refer to. |
| `interface ILayoutEditor { bool Editing; void SetEditMode(bool); ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit); IReadOnlyList<GalleryItem> GalleryFor(string displayId); ConfigError? AddFromGallery(string displayId, GalleryItem item); }` implemented by `HostController` | Bars ask; the host resolves the display key and saves. |
| `interface IConfigWriter { ConfigError? Save(Action<JsonObject> edit); }` implemented by `ConfigStore` | The host saves without knowing about files; tests use `FakeConfigWriter`. |
| `HostController(..., string? configDirectory = null, IConfigWriter? writer = null, IReadOnlyList<ScriptTemplate>? templates = null)` | Optional, so existing tests and callers keep compiling. |
| `TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)` | Menu commands and edit mode save through the editor. |
| `DisplayResolver.VisibleTileIds` → `PlacedTileIds`, + `EditKey` | Every stack member runs; edit mode needs the key to write. |
| `TileMenu.Entries(bool onTile)` → `Entries(SlotMenuState? slot, bool editing)`; `MenuEntry` + `LayoutCommand? Layout`, `bool Enabled`; `BarCommand` + `EditLayout`, `DoneEditing` | The deferred menu items. |
| `FlyoutWindow.ShowPanel(FrameworkElement anchor, UIElement content, bool light, ITileLog log, double? width = 320, Action? onClosed = null)`, `FlyoutWindow.Text(...)` public | The overflow popup and the gallery reuse the popup frame and its one-popup-at-a-time rule. |
| `TileView.Compact`, `TileView.FlyoutAnchor` | Compact look; popups of overflowed tiles open above the `»` tile. |

### Decisions where the spec is silent

| Decision | Why |
|---|---|
| A stack is as wide as its widest member. | It never changes width (and never reflows the bar) while it rotates, and every member renders at its full size. |
| Rotation bounds `1s`..`1h`; out of range is a config error. | Faster is unreadable, slower is not rotating; same treatment as other durations. |
| An urgent member holds the stack until the user clicks it; it takes over again only after it stopped being urgent and became urgent anew; the wheel still works meanwhile, and the next tick returns to the urgent member. | "Until clicked" without re-triggering on every repeated state update. |
| Pause/Resume rotation is not saved. | Config holds layout, not transient state; the spec has no field for it. |
| Compact tiles without an icon show the first letter or digit of the title. | Something recognisable in 40 DIP. |
| After compacting all unpinned tiles, overflow takes unpinned tiles right to left, then pinned right to left; already compacted tiles are not re-expanded. | Literal reading of the spec's three steps; simple and predictable. |
| A popup opened from a tile in the overflow popup replaces it and appears above the `»` tile. | One popup at a time (existing rule); the tile itself leaves the screen when the overflow popup closes. |
| Edit mode switches on for every display's bar at once, each bar editing only its own display's layout; Done/Esc on any bar leaves it everywhere. | A display without tiles has no visible bar to right-click; the spec says edit mode "works on each display's bar". |
| Every edit (drag, ×, +, menu item) saves immediately; Done/Esc only leaves edit mode. | Matches the mockup (Done is not Save) and gives menu items outside edit mode the same path. |
| Edit key: device id if it has an entry, else `primary` on the primary display, else the device id (copied from `*`). `*` is never written. | Least surprise: editing a bar changes only that display; `primary` stays the readable key for the main display and only ever affects it. |
| × removes the tile from this display only; the definition stays under `tiles` even if no display uses it. `+` lists such unplaced tiles first. | Removing is reversible without hand-editing, and hand-written settings (script hooks) are never lost. |
| × on a stack removes the member shown; a stack left with one member becomes a plain tile; an emptied stack disappears. | Granular, consistent with "Remove from stack"; invisible one-member stacks would confuse. |
| Drop: middle 40 % of a tile (0.3..0.7) stacks, outer parts reorder before/after. Dropping a stack onto a tile merges members; duplicates are not added twice. | Mockup's thresholds; one rule for tiles and stacks. |
| Superseded by `2026-10-27-milestone-3-feedback.md`: a plain tile's menu has "Create stack with ▸" and a stack's menu "Add to stack ▸", each listing this display's plain tiles. (Originally "Add to stack" joined the nearest stack.) | The user wants to pick the tile, and no one-member stacks. |
| Superseded by `2026-10-27-milestone-3-feedback.md`: "Move left/right" were removed; dragging in edit mode moves tiles. (Originally they were drops before/after the neighboring bar slot.) | User feedback after testing on screen. |
| Gallery: unplaced existing tiles, then one "new tile" per loaded plugin connector except `script`, then script templates for the shipped samples (`uptime`, `ping`); new ids are `<base>`, `<base>-2`, …, chosen from the file being saved. | `script` needs hooks, which the templates supply; picking the id from the file never replaces a tile added by hand but not reloaded yet. |
| Saving reads the file on disk, applies the change, and validates the result with `ConfigParser` before writing. | A hand edit not yet reloaded survives, and an edit can never produce a config the host would reject. |
| Watcher echo: a reload whose text equals the text the current config came from is ignored. | Idempotent, no timing window; also makes "save without changes" in an editor a no-op. |
| Edit mode makes bars activatable (no `WS_EX_NOACTIVATE`) so Esc reaches them; normal mode restores it. | A no-activate window never gets keyboard input. |
| `tools/install.ps1` stops only the copy running from the install folder and warns about others; it never touches config, scripts, plugins or logs. | Idempotent and safe to rerun; agents can run it against `artifacts\` without stopping the user's instance. |

---

### Task 1: Rotation bounds, edit key and placed tiles

**Files:**
- Modify: `src/TaskbarTiles.Host/Config/ConfigParser.cs` (whole file), `src/TaskbarTiles.Host/Config/DisplayResolver.cs` (whole file)
- Test: `tests/TaskbarTiles.Tests/ConfigParserTests.cs`, `tests/TaskbarTiles.Tests/DisplayResolverTests.cs`

**Interfaces:**
- Consumes: `Duration.TryParse(string?, out TimeSpan)` (Contracts), `HostConfig`, `DisplayLayout`, `TileSlot`, `StackSlot` (`Config/HostConfig.cs`)
- Produces:
  - `ConfigParser.DefaultRotate` (5 s), `ConfigParser.MinRotate` (1 s), `ConfigParser.MaxRotate` (1 h): `public static readonly TimeSpan`
  - `static string DisplayResolver.EditKey(HostConfig config, string displayId, bool isPrimary)`
  - `static IReadOnlyList<string> DisplayResolver.PlacedTileIds(DisplayLayout layout)` (every tile incl. stack members, each once, in order)
  - `DisplayResolver.VisibleTileIds` stays until Task 6 removes it.

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/ConfigParserTests.cs`, find:
```csharp
    [InlineData("\"99999999999h\"")]
    public void Non_string_or_invalid_rotate_is_a_config_error(string rotateJson)
```
Replace with:
```csharp
    [InlineData("\"99999999999h\"")]
    [InlineData("\"0s\"")]
    [InlineData("\"500ms\"")]
    [InlineData("\"61m\"")]
    public void Non_string_invalid_or_out_of_range_rotate_is_a_config_error(string rotateJson)
```

**Edit** `tests/TaskbarTiles.Tests/ConfigParserTests.cs`, find:
```csharp
    [Fact]
    public void Missing_displays_section_means_no_layouts()
```
Replace with:
```csharp
    [Theory]
    [InlineData("1s", 1)]
    [InlineData("1h", 3600)]
    public void Rotate_accepts_1s_to_1h(string rotate, int seconds)
    {
        var json = "{ \"tiles\": { \"a\": { \"connector\": \"x\" } }, \"displays\": { \"primary\": { \"slots\": [ { \"stack\": [\"a\"], \"rotate\": \"" + rotate + "\" } ] } } }";

        var result = ConfigParser.Parse(json);

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.IsType<StackSlot>(result.Config!.Displays["primary"].Slots[0]).Rotate);
    }

    [Fact]
    public void Missing_displays_section_means_no_layouts()
```

**Edit** `tests/TaskbarTiles.Tests/DisplayResolverTests.cs`, find:
```csharp
        Assert.Equal(new[] { "a", "b" }, DisplayResolver.VisibleTileIds(layout));
    }
```
Replace with:
```csharp
        Assert.Equal(new[] { "a", "b" }, DisplayResolver.VisibleTileIds(layout));
    }

    [Fact]
    public void Placed_tile_ids_include_every_stack_member_once()
    {
        var layout = new DisplayLayout([new TileSlot("a", false), new StackSlot(["b", "c", "a"], TimeSpan.FromSeconds(5)), new StackSlot([], TimeSpan.FromSeconds(5))]);
        Assert.Equal(new[] { "a", "b", "c" }, DisplayResolver.PlacedTileIds(layout));
    }

    [Fact]
    public void Edit_key_is_the_device_id_when_it_has_an_entry() =>
        Assert.Equal("DEV1", DisplayResolver.EditKey(Config(("DEV1", Exact), ("primary", Primary)), "DEV1", isPrimary: true));

    [Fact]
    public void Edit_key_is_primary_on_the_primary_display_without_its_own_entry()
    {
        Assert.Equal("primary", DisplayResolver.EditKey(Config(("primary", Primary), ("*", Any)), "DEV1", isPrimary: true));
        Assert.Equal("primary", DisplayResolver.EditKey(Config(("*", Any)), "DEV1", isPrimary: true));
    }

    [Fact]
    public void Edit_key_is_the_device_id_on_other_displays_never_the_star() =>
        Assert.Equal("DEV2", DisplayResolver.EditKey(Config(("primary", Primary), ("*", Any)), "DEV2", isPrimary: false));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ConfigParserTests|FullyQualifiedName~DisplayResolverTests"`
Expected: build error CS0117 (`DisplayResolver` does not contain a definition for `PlacedTileIds` / `EditKey`).

- [ ] **Step 3: Implement**

**Replace** `src/TaskbarTiles.Host/Config/ConfigParser.cs` (whole file):
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Config;

public sealed record ConfigError(string Message, int? Line = null, int? Column = null)
{
    public override string ToString() => Line is null ? Message : $"{Message} (line {Line}, column {Column})";
}

public sealed record ConfigParseResult(HostConfig? Config, ConfigError? Error)
{
    public bool Success => Config is not null;
}

public static class ConfigParser
{
    static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A stack's "rotate" when it has none; edit mode uses it for new stacks.</summary>
    public static readonly TimeSpan DefaultRotate = TimeSpan.FromSeconds(5);

    /// <summary>Bounds for "rotate": faster than 1 s is unreadable, slower than 1 h is not rotating.</summary>
    public static readonly TimeSpan MinRotate = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRotate = TimeSpan.FromHours(1);

    public static ConfigParseResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            return new(null, new ConfigError("Invalid JSON: " + TrimJsonMessage(ex.Message),
                (int?)(ex.LineNumber + 1), (int?)(ex.BytePositionInLine + 1)));
        }

        using (doc)
        {
            try
            {
                return new(Build(doc.RootElement), null);
            }
            catch (ConfigException ex)
            {
                return new(null, new ConfigError(ex.Message));
            }
        }
    }

    static HostConfig Build(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ConfigException("The config must be a JSON object.");

        var tiles = new Dictionary<string, TileConfig>(StringComparer.Ordinal);
        if (root.TryGetProperty("tiles", out var tilesEl))
        {
            if (tilesEl.ValueKind != JsonValueKind.Object) throw new ConfigException("\"tiles\" must be an object.");
            foreach (var tile in tilesEl.EnumerateObject())
                tiles[tile.Name] = BuildTile(tile.Name, tile.Value);
        }

        var displays = new Dictionary<string, DisplayLayout>(StringComparer.Ordinal);
        if (root.TryGetProperty("displays", out var displaysEl))
        {
            if (displaysEl.ValueKind != JsonValueKind.Object) throw new ConfigException("\"displays\" must be an object.");
            foreach (var display in displaysEl.EnumerateObject())
                displays[display.Name] = BuildLayout(display.Name, display.Value, tiles);
        }

        return new HostConfig(tiles, displays);
    }

    static TileConfig BuildTile(string id, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ConfigException($"Tile '{id}' must be an object.");
        if (!el.TryGetProperty("connector", out var c) || c.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(c.GetString()))
            throw new ConfigException($"Tile '{id}' needs a \"connector\" name.");

        var settings = el.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.Clone()
            : EmptyObject();
        return new TileConfig(c.GetString()!, settings);
    }

    static DisplayLayout BuildLayout(string display, JsonElement el, IReadOnlyDictionary<string, TileConfig> tiles)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ConfigException($"Display '{display}' must be an object.");
        if (!el.TryGetProperty("slots", out var slotsEl)) return DisplayLayout.Empty;
        if (slotsEl.ValueKind != JsonValueKind.Array) throw new ConfigException($"\"slots\" on display '{display}' must be an array.");

        var slots = new List<Slot>();
        var index = 0;
        foreach (var slotEl in slotsEl.EnumerateArray())
        {
            index++;
            var slot = BuildSlot(display, index, slotEl);
            IReadOnlyList<string> ids = slot is StackSlot st ? st.TileIds : new[] { ((TileSlot)slot).TileId };
            foreach (var id in ids)
                if (!tiles.ContainsKey(id))
                    throw new ConfigException($"Display '{display}' references unknown tile '{id}'.");
            slots.Add(slot);
        }
        return new DisplayLayout(slots);
    }

    static Slot BuildSlot(string display, int index, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return new TileSlot(el.GetString()!, false);

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("tile", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var pin = el.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.True;
            return new TileSlot(t.GetString()!, pin);
        }

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("stack", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            var ids = s.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ConfigException($"Stack in slot {index} on display '{display}' must list tile ids."))
                .ToList();
            var rotate = DefaultRotate;
            if (el.TryGetProperty("rotate", out var r)
                && (!Duration.TryParse(r.ValueKind == JsonValueKind.String ? r.GetString() : null, out rotate)
                    || rotate < MinRotate || rotate > MaxRotate))
                throw new ConfigException($"Invalid \"rotate\" in slot {index} on display '{display}'. Use a duration from 1s to 1h, e.g. 5s.");
            return new StackSlot(ids, rotate);
        }

        throw new ConfigException($"Slot {index} on display '{display}' must be a tile id, {{\"tile\": ...}} or {{\"stack\": [...]}}.");
    }

    static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    static string TrimJsonMessage(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }

    sealed class ConfigException(string message) : Exception(message);
}
```

**Replace** `src/TaskbarTiles.Host/Config/DisplayResolver.cs` (whole file):
```csharp
namespace TaskbarTiles.Host.Config;

public static class DisplayResolver
{
    public const string PrimaryKey = "primary";
    public const string FallbackKey = "*";

    /// <summary>Exact device id, then "primary" for the primary display, then "*".</summary>
    public static DisplayLayout Resolve(HostConfig config, string displayId, bool isPrimary)
    {
        if (config.Displays.TryGetValue(displayId, out var exact)) return exact;
        if (isPrimary && config.Displays.TryGetValue(PrimaryKey, out var primary)) return primary;
        return config.Displays.TryGetValue(FallbackKey, out var any) ? any : DisplayLayout.Empty;
    }

    /// <summary>
    /// The "displays" key edit mode writes for this display: the device id when it has its own entry;
    /// otherwise "primary" on the primary display (that key only ever applies to the primary display);
    /// otherwise the device id, so editing one display never changes the other displays that share "*".
    /// </summary>
    public static string EditKey(HostConfig config, string displayId, bool isPrimary) =>
        isPrimary && !config.Displays.ContainsKey(displayId) ? PrimaryKey : displayId;

    /// <summary>Every tile the layout places, stack members included, each once, in order of appearance.</summary>
    public static IReadOnlyList<string> PlacedTileIds(DisplayLayout layout) =>
        layout.Slots
            .SelectMany(s => s switch
            {
                TileSlot t => [t.TileId],
                StackSlot st => st.TileIds,
                _ => (IReadOnlyList<string>)[],
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Tiles shown in milestone 1: plain slots plus the first item of each stack.</summary>
    public static IReadOnlyList<string> VisibleTileIds(DisplayLayout layout) =>
        layout.Slots
            .Select(s => s switch
            {
                TileSlot t => t.TileId,
                StackSlot { TileIds.Count: > 0 } st => st.TileIds[0],
                _ => null,
            })
            .OfType<string>()
            .ToList();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ConfigParserTests|FullyQualifiedName~DisplayResolverTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 300`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config/ConfigParser.cs src/TaskbarTiles.Host/Config/DisplayResolver.cs tests/TaskbarTiles.Tests/ConfigParserTests.cs tests/TaskbarTiles.Tests/DisplayResolverTests.cs
git commit -m "feat(config): bound stack rotate to 1s-1h; edit key and placed tiles per display" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Layout edits

**Files:**
- Create: `src/TaskbarTiles.Host/Config/LayoutEdits.cs`
- Test: `tests/TaskbarTiles.Tests/LayoutEditsTests.cs` (new)

**Interfaces:**
- Consumes: `DisplayLayout`, `Slot`, `TileSlot`, `StackSlot` (value equality, `StackSlot` compares members); `ConfigParser.DefaultRotate` (Task 1)
- Produces (namespace `TaskbarTiles.Host.Config`):
  - `enum DropPosition { Before, Into, After }`
  - `static class LayoutEdits`:
    - `DropPosition DropPositionAt(double relativeX)` (≤ 0.3 Before, ≥ 0.7 After, else Into)
    - `DisplayLayout Drop(DisplayLayout layout, int from, int target, DropPosition position)`
    - `DisplayLayout StackInto(DisplayLayout layout, int from, int target)`
    - `DisplayLayout AddToNearestStack(DisplayLayout layout, int index)`
    - `DisplayLayout RemoveFromStack(DisplayLayout layout, int index, int member)`
    - `DisplayLayout Remove(DisplayLayout layout, int index, int? member = null)`
    - `DisplayLayout Append(DisplayLayout layout, string tileId)`
    - `bool SameSlots(DisplayLayout a, DisplayLayout b)`
  - Indexes are positions in `DisplayLayout.Slots`; a bad index returns the layout unchanged; a stack left with one member becomes a `TileSlot(id, false)`, an empty one disappears; inputs are never mutated.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/LayoutEditsTests.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class LayoutEditsTests
{
    static readonly TimeSpan Five = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Eight = TimeSpan.FromSeconds(8);

    static TileSlot T(string id, bool pin = false) => new(id, pin);
    static StackSlot S(TimeSpan rotate, params string[] ids) => new(ids, rotate);
    static DisplayLayout L(params Slot[] slots) => new(slots);

    static void Same(DisplayLayout expected, DisplayLayout actual) =>
        Assert.True(LayoutEdits.SameSlots(expected, actual), $"expected [{string.Join(", ", expected.Slots)}] but got [{string.Join(", ", actual.Slots)}]");

    [Theory]
    [InlineData(0.0, DropPosition.Before)]
    [InlineData(0.3, DropPosition.Before)]
    [InlineData(0.31, DropPosition.Into)]
    [InlineData(0.5, DropPosition.Into)]
    [InlineData(0.69, DropPosition.Into)]
    [InlineData(0.7, DropPosition.After)]
    [InlineData(1.0, DropPosition.After)]
    public void Middle_of_a_slot_means_into(double relativeX, DropPosition expected) =>
        Assert.Equal(expected, LayoutEdits.DropPositionAt(relativeX));

    [Fact]
    public void Drop_after_a_later_slot_moves_right()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b"), T("c")), from: 0, target: 2, DropPosition.After);
        Same(L(T("b"), T("c"), T("a")), result);
    }

    [Fact]
    public void Drop_before_an_earlier_slot_moves_left_and_keeps_pins()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b"), T("c", pin: true)), from: 2, target: 0, DropPosition.Before);
        Same(L(T("c", pin: true), T("a"), T("b")), result);
    }

    [Fact]
    public void Drop_before_the_next_slot_changes_nothing()
    {
        var layout = L(T("a"), T("b"), T("c"));
        Same(layout, LayoutEdits.Drop(layout, from: 0, target: 1, DropPosition.Before));
    }

    [Fact]
    public void Drop_into_a_tile_makes_a_stack_where_the_target_was()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b", pin: true), T("c")), from: 2, target: 0, DropPosition.Into);
        Same(L(S(Five, "a", "c"), T("b", pin: true)), result);
    }

    [Fact]
    public void Drop_into_a_stack_appends_and_keeps_its_rotate()
    {
        var result = LayoutEdits.Drop(L(T("a"), S(Eight, "b", "c")), from: 0, target: 1, DropPosition.Into);
        Same(L(S(Eight, "b", "c", "a")), result);
    }

    [Fact]
    public void Drop_a_stack_into_a_tile_merges_and_keeps_the_dragged_rotate()
    {
        var result = LayoutEdits.Drop(L(T("a"), S(Eight, "b", "c")), from: 1, target: 0, DropPosition.Into);
        Same(L(S(Eight, "a", "b", "c")), result);
    }

    [Fact]
    public void Drop_into_a_stack_that_already_has_the_tile_only_removes_the_dragged_slot()
    {
        var result = LayoutEdits.Drop(L(S(Five, "a", "b"), T("a")), from: 1, target: 0, DropPosition.Into);
        Same(L(S(Five, "a", "b")), result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    public void Drop_with_bad_or_equal_indexes_changes_nothing(int from, int target)
    {
        var layout = L(T("a"), T("b"));
        Same(layout, LayoutEdits.Drop(layout, from, target, DropPosition.After));
    }

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

    [Fact]
    public void Remove_from_stack_puts_the_member_right_after_the_stack()
    {
        var result = LayoutEdits.RemoveFromStack(L(S(Eight, "a", "b", "c"), T("d")), 0, 1);
        Same(L(S(Eight, "a", "c"), T("b"), T("d")), result);
    }

    [Fact]
    public void Remove_from_a_two_member_stack_leaves_two_plain_tiles()
    {
        var result = LayoutEdits.RemoveFromStack(L(S(Five, "a", "b")), 0, 0);
        Same(L(T("b"), T("a")), result);
    }

    [Fact]
    public void Remove_from_stack_on_a_plain_tile_changes_nothing()
    {
        var layout = L(T("a"), T("b"));
        Same(layout, LayoutEdits.RemoveFromStack(layout, 0, 0));
    }

    [Fact]
    public void Remove_drops_the_slot()
    {
        Same(L(T("b")), LayoutEdits.Remove(L(T("a"), T("b")), 0));
        Same(L(T("a")), LayoutEdits.Remove(L(T("a"), S(Five, "b", "c")), 1));
    }

    [Fact]
    public void Remove_with_a_member_drops_only_that_member_and_collapses_the_stack()
    {
        Same(L(S(Eight, "a", "c")), LayoutEdits.Remove(L(S(Eight, "a", "b", "c")), 0, member: 1));
        Same(L(T("b")), LayoutEdits.Remove(L(S(Five, "a", "b")), 0, member: 0));
        Same(L(T("x")), LayoutEdits.Remove(L(S(Five, "a"), T("x")), 0, member: 0));
    }

    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
        Same(L(T("a"), T("b")), LayoutEdits.Append(L(T("a")), "b"));

    [Fact]
    public void Edits_never_change_the_input()
    {
        var layout = L(T("a"), T("b"), T("c"));
        LayoutEdits.Drop(layout, 0, 2, DropPosition.Into);
        LayoutEdits.Drop(layout, 0, 1, DropPosition.After);
        LayoutEdits.Remove(layout, 1);
        Same(L(T("a"), T("b"), T("c")), layout);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~LayoutEditsTests"`
Expected: build error CS0103/CS0246 (`LayoutEdits` and `DropPosition` do not exist).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Config/LayoutEdits.cs`:
```csharp
namespace TaskbarTiles.Host.Config;

/// <summary>Where a dragged slot lands relative to the slot under the pointer.</summary>
public enum DropPosition { Before, Into, After }

/// <summary>
/// Pure edits of one display's slots, used by edit mode and the tile menu. Indexes are positions in
/// <see cref="DisplayLayout.Slots"/>. An index that does not fit leaves the layout unchanged, so a stale request is harmless.
/// A stack an edit leaves with one member becomes a plain tile; a stack it leaves empty disappears.
/// </summary>
public static class LayoutEdits
{
    /// <summary>The middle 40 % of a slot means "into"; the outer parts mean before or after it.</summary>
    public static DropPosition DropPositionAt(double relativeX) =>
        relativeX <= 0.3 ? DropPosition.Before : relativeX >= 0.7 ? DropPosition.After : DropPosition.Into;

    /// <summary>Drag and drop: Before/After reorders, Into stacks the dragged slot with the target.</summary>
    public static DisplayLayout Drop(DisplayLayout layout, int from, int target, DropPosition position)
    {
        if (!Valid(layout, from) || !Valid(layout, target) || from == target) return layout;
        if (position == DropPosition.Into) return StackInto(layout, from, target);

        var slots = layout.Slots.ToList();
        var moving = slots[from];
        slots.RemoveAt(from);
        var at = target > from ? target - 1 : target;
        slots.Insert(position == DropPosition.After ? at + 1 : at, moving);
        return new DisplayLayout(slots);
    }

    /// <summary>
    /// Puts the tiles of slot <paramref name="from"/> into slot <paramref name="target"/>: appended to a target stack,
    /// or a new stack [target, dragged…] where the target tile was. Tiles already in the target stack are not added twice.
    /// </summary>
    public static DisplayLayout StackInto(DisplayLayout layout, int from, int target)
    {
        if (!Valid(layout, from) || !Valid(layout, target) || from == target) return layout;
        var dragged = layout.Slots[from];
        var draggedIds = Members(dragged);

        Slot merged = layout.Slots[target] switch
        {
            StackSlot s => new StackSlot(s.TileIds.Concat(draggedIds.Where(id => !s.TileIds.Contains(id))).ToList(), s.Rotate),
            TileSlot t => new StackSlot(new[] { t.TileId }.Concat(draggedIds.Where(id => id != t.TileId)).ToList(),
                dragged is StackSlot ds ? ds.Rotate : ConfigParser.DefaultRotate),
            var other => other,
        };

        var slots = layout.Slots.ToList();
        slots[target] = Collapse(merged)!;
        slots.RemoveAt(from);
        return new DisplayLayout(slots);
    }

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
    public static DisplayLayout RemoveFromStack(DisplayLayout layout, int index, int member)
    {
        if (!Valid(layout, index) || layout.Slots[index] is not StackSlot s || member < 0 || member >= s.TileIds.Count) return layout;
        var slots = layout.Slots.ToList();
        var rest = Collapse(new StackSlot(s.TileIds.Where((_, i) => i != member).ToList(), s.Rotate));
        slots.Insert(index + 1, new TileSlot(s.TileIds[member], false));
        if (rest is null) slots.RemoveAt(index);
        else slots[index] = rest;
        return new DisplayLayout(slots);
    }

    /// <summary>Removes the slot, or with <paramref name="member"/> one member of a stack.</summary>
    public static DisplayLayout Remove(DisplayLayout layout, int index, int? member = null)
    {
        if (!Valid(layout, index)) return layout;
        var slots = layout.Slots.ToList();
        if (member is { } m && slots[index] is StackSlot s)
        {
            if (m < 0 || m >= s.TileIds.Count) return layout;
            var rest = Collapse(new StackSlot(s.TileIds.Where((_, i) => i != m).ToList(), s.Rotate));
            if (rest is null) slots.RemoveAt(index);
            else slots[index] = rest;
        }
        else
        {
            slots.RemoveAt(index);
        }
        return new DisplayLayout(slots);
    }

    /// <summary>Adds a plain tile at the end.</summary>
    public static DisplayLayout Append(DisplayLayout layout, string tileId) =>
        new(layout.Slots.Append(new TileSlot(tileId, false)).ToList());

    /// <summary>True when both layouts hold equal slots in the same order.</summary>
    public static bool SameSlots(DisplayLayout a, DisplayLayout b) => a.Slots.SequenceEqual(b.Slots);

    static bool Valid(DisplayLayout layout, int index) => index >= 0 && index < layout.Slots.Count;

    static IReadOnlyList<string> Members(Slot slot) => slot switch
    {
        TileSlot t => [t.TileId],
        StackSlot s => s.TileIds,
        _ => [],
    };

    static Slot? Collapse(Slot slot) => slot switch
    {
        StackSlot { TileIds.Count: 0 } => null,
        StackSlot { TileIds.Count: 1 } s => new TileSlot(s.TileIds[0], false),
        _ => slot,
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~LayoutEditsTests"`
Expected: PASS (29 tests).

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 329`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config/LayoutEdits.cs tests/TaskbarTiles.Tests/LayoutEditsTests.cs
git commit -m "feat(config): pure layout edits for edit mode and the tile menu" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Saving config.json

**Files:**
- Create: `src/TaskbarTiles.Host/Config/ConfigJson.cs`
- Modify: `src/TaskbarTiles.Host/Config/ConfigStore.cs` (whole file)
- Test: `tests/TaskbarTiles.Tests/ConfigJsonTests.cs`, `tests/TaskbarTiles.Tests/ConfigStoreSaveTests.cs` (new)

**Interfaces:**
- Consumes: `ConfigParser.Parse(string)`, `ConfigError(string Message, int? Line, int? Column)`, `TileConfig(string Connector, JsonElement Settings)`, `LayoutEdits.SameSlots` (Task 2, tests only)
- Produces (namespace `TaskbarTiles.Host.Config`):
  - `static class ConfigJson`: `JsonObject ParseRoot(string text)` (comments skipped, trailing commas allowed; throws on invalid JSON or a non-object root), `string Serialize(JsonObject root)` (indented, relaxed escaping, ends with a newline), `void SetDisplay(JsonObject root, string key, DisplayLayout layout)` (sets `displays[key].slots`, keeps other keys), `void SetTile(JsonObject root, string id, TileConfig tile)`, `JsonNode ToNode(Slot slot)`, `string FormatDuration(TimeSpan value)` (`"2h"`, `"5m"`, `"30s"`, `"1.5s"`)
  - `interface IConfigWriter { ConfigError? Save(Action<JsonObject> edit); }` (null = saved; never throws)
  - `ConfigStore : IConfigWriter, IDisposable` with `ConfigError? Save(Action<JsonObject> edit)` and `string BackupPath` (`config.json.bak`); `Changed` is now also raised by a successful `Save`, on the saving thread.

Behavior of `Save`: reads the file now on disk; refuses (error with line/column, nothing written) if it does not parse; applies the edit to the JSON tree; refuses if the result does not parse; writes `config.json.tmp` and swaps it in with `File.Replace(tmp, config.json, config.json.bak)`; updates `Current`; raises `Changed`. The watcher skips a reload whose text equals the text `Current` came from (unless the last reload failed), so our own write never comes back as a reload.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/ConfigJsonTests.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigJsonTests
{
    [Theory]
    [InlineData(5, "5s")]
    [InlineData(1.5, "1.5s")]
    [InlineData(90, "90s")]
    [InlineData(120, "2m")]
    [InlineData(3600, "1h")]
    public void Durations_are_written_in_the_largest_whole_unit(double seconds, string expected)
    {
        var text = ConfigJson.FormatDuration(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
        Assert.True(TaskbarTiles.Contracts.Duration.TryParse(text, out var back));
        Assert.Equal(TimeSpan.FromSeconds(seconds), back);
    }

    [Fact]
    public void Slots_round_trip_through_the_parser()
    {
        var layout = new DisplayLayout([
            new TileSlot("a", false),
            new TileSlot("b", true),
            new StackSlot(["a", "b"], TimeSpan.FromSeconds(8)),
        ]);
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "connector": "x" }, "b": { "connector": "x" } } }""");

        ConfigJson.SetDisplay(root, "primary", layout);
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.True(LayoutEdits.SameSlots(layout, parsed.Config!.Displays["primary"]));
    }

    [Fact]
    public void Set_display_keeps_other_keys_and_their_order()
    {
        var root = ConfigJson.ParseRoot("""
        {
          // dropped on save
          "custom": 1,
          "tiles": { "a": { "connector": "x", "note": "kept" } },
          "displays": { "*": { "slots": [] }, "primary": { "slots": [], "extra": true }, },
        }
        """);

        ConfigJson.SetDisplay(root, "primary", new DisplayLayout([new TileSlot("a", false)]));
        var text = ConfigJson.Serialize(root);

        Assert.DoesNotContain("dropped", text);
        Assert.Contains("\"note\": \"kept\"", text);
        Assert.Contains("\"extra\": true", text);
        Assert.True(text.IndexOf("\"custom\"", StringComparison.Ordinal) < text.IndexOf("\"tiles\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"*\"", StringComparison.Ordinal) < text.IndexOf("\"primary\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("a", doc.RootElement.GetProperty("displays").GetProperty("primary").GetProperty("slots")[0].GetString());
    }

    [Fact]
    public void Set_display_creates_missing_sections_and_entries()
    {
        var root = ConfigJson.ParseRoot("{}");

        ConfigJson.SetDisplay(root, @"\\?\DISPLAY#DEL41A8#5&2b1c", DisplayLayout.Empty);

        using var doc = JsonDocument.Parse(ConfigJson.Serialize(root));
        Assert.Equal(0, doc.RootElement.GetProperty("displays").GetProperty(@"\\?\DISPLAY#DEL41A8#5&2b1c").GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public void Set_tile_writes_connector_and_settings()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "connector": "x" } } }""");
        var settings = JsonDocument.Parse("""{ "hooks": { "click": "scripts/a.ps1" } }""").RootElement.Clone();

        ConfigJson.SetTile(root, "b", new TileConfig("script", settings));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new[] { "a", "b" }, parsed.Config!.Tiles.Keys);
        Assert.Equal("scripts/a.ps1", parsed.Config.Tiles["b"].Settings.GetProperty("hooks").GetProperty("click").GetString());
    }

    [Fact]
    public void Serialize_indents_and_keeps_non_ascii_readable()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "café": { "connector": "x", "settings": {} } } }""");

        var text = ConfigJson.Serialize(root);

        Assert.Contains("café", text);
        Assert.Contains(Environment.NewLine + "  \"tiles\"", text);
        Assert.EndsWith(Environment.NewLine, text);
        Assert.Equal("x", ConfigParser.Parse(text).Config!.Tiles["café"].Connector);
    }

    [Fact]
    public void Parse_root_rejects_a_non_object() =>
        Assert.Throws<InvalidOperationException>(() => ConfigJson.ParseRoot("[1]"));
}
```

**Create** `tests/TaskbarTiles.Tests/ConfigStoreSaveTests.cs`:
```csharp
using System.IO;
using System.Text.Json.Nodes;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigStoreSaveTests
{
    const string Original = """
    {
      // my notes
      "tiles": { "a": { "connector": "x" }, "b": { "connector": "x" } },
      "displays": { "primary": { "slots": ["a"] } }
    }
    """;

    static Action<JsonObject> PlaceBoth =>
        root => ConfigJson.SetDisplay(root, "primary", new DisplayLayout([new TileSlot("a", false), new TileSlot("b", false)]));

    [Fact]
    public void Save_rewrites_the_file_and_updates_current()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var error = store.Save(PlaceBoth);

        Assert.Null(error);
        Assert.Equal(1, changed);
        Assert.Equal(2, store.Current.Displays["primary"].Slots.Count);
        var written = File.ReadAllText(dir.File("config.json"));
        Assert.DoesNotContain("my notes", written); // comments are lost: documented
        Assert.Equal(2, ConfigParser.Parse(written).Config!.Displays["primary"].Slots.Count);
        Assert.False(File.Exists(dir.File("config.json.tmp")));
    }

    [Fact]
    public void Save_keeps_the_previous_file_as_bak_each_time()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();

        Assert.Null(store.Save(PlaceBoth));
        Assert.Equal(Original, File.ReadAllText(store.BackupPath));

        var afterFirst = File.ReadAllText(dir.File("config.json"));
        Assert.Null(store.Save(root => ConfigJson.SetDisplay(root, "primary", DisplayLayout.Empty)));
        Assert.Equal(afterFirst, File.ReadAllText(store.BackupPath));
    }

    [Fact]
    public void Save_refuses_a_file_that_does_not_parse_and_leaves_it_alone()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        File.WriteAllText(dir.File("config.json"), "{ \"tiles\": ");

        var error = store.Save(PlaceBoth);

        Assert.NotNull(error);
        Assert.Contains("nothing was saved", error!.Message);
        Assert.NotNull(error.Line);
        Assert.Equal("{ \"tiles\": ", File.ReadAllText(dir.File("config.json")));
        Assert.False(File.Exists(store.BackupPath));
        Assert.Single(store.Current.Displays["primary"].Slots); // last good config stays
    }

    [Fact]
    public void Save_refuses_an_edit_that_would_make_the_config_invalid()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();

        var error = store.Save(root => ConfigJson.SetDisplay(root, "primary", new DisplayLayout([new TileSlot("ghost", false)])));

        Assert.NotNull(error);
        Assert.Contains("ghost", error!.Message);
        Assert.Equal(Original, File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Save_reports_an_edit_that_throws_without_writing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        var log = new CollectingLog();
        using var store = new ConfigStore(dir.File("config.json"), log);
        store.Load();

        var error = store.Save(_ => throw new InvalidOperationException("bad edit"));

        Assert.NotNull(error);
        Assert.Contains("bad edit", error!.Message);
        Assert.Equal(Original, File.ReadAllText(dir.File("config.json")));
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR") && m.Contains("Saving config.json failed"));
    }

    [Fact]
    public void Save_builds_on_the_file_on_disk_so_a_hand_edit_not_yet_reloaded_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        File.WriteAllText(dir.File("config.json"), Original.Replace("\"b\": { \"connector\": \"x\" }", "\"b\": { \"connector\": \"x\" }, \"c\": { \"connector\": \"y\" }"));

        Assert.Null(store.Save(PlaceBoth));

        Assert.Equal("y", store.Current.Tiles["c"].Connector);
    }

    [Fact]
    public async Task Our_own_save_does_not_come_back_as_a_reload()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        var log = new CollectingLog();
        using var store = new ConfigStore(dir.File("config.json"), log);
        store.Load();
        store.StartWatching();
        var changed = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changed);

        Assert.Null(store.Save(PlaceBoth));
        await Task.Delay(1000); // well past the 300 ms debounce

        Assert.Equal(1, Volatile.Read(ref changed));
        Assert.DoesNotContain(log.Snapshot(), m => m.Contains("Reloaded config.json"));
    }

    [Fact]
    public async Task A_hand_edit_after_a_save_is_still_picked_up()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        store.StartWatching();
        Assert.Null(store.Save(PlaceBoth));
        await Task.Delay(600);
        var reloaded = new TaskCompletionSource();
        store.Changed += (_, _) => reloaded.TrySetResult();

        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "z": { "connector": "x" } } }""");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.Current.Tiles.ContainsKey("z"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ConfigJsonTests|FullyQualifiedName~ConfigStoreSaveTests"`
Expected: build error CS0103 (`ConfigJson` does not exist) and CS1061 (`ConfigStore` has no `Save` / `BackupPath`).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Config/ConfigJson.cs`:
```csharp
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskbarTiles.Host.Config;

/// <summary>
/// Turns config.json text into an editable tree and back. Saving rewrites the whole file: comments and custom
/// formatting are lost, everything else (unknown keys included) keeps its place; new keys are appended.
/// </summary>
public static class ConfigJson
{
    static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file for people: keep "…" and quotes readable
    };

    /// <summary>Throws JsonException on invalid JSON and InvalidOperationException when the root is not an object.</summary>
    public static JsonObject ParseRoot(string text) =>
        JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
        ?? throw new InvalidOperationException("The config must be a JSON object.");

    public static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>Sets displays[key].slots, keeping any other keys of that display entry.</summary>
    public static void SetDisplay(JsonObject root, string key, DisplayLayout layout)
    {
        var displays = ChildObject(root, "displays");
        var display = ChildObject(displays, key);
        display["slots"] = new JsonArray(layout.Slots.Select(ToNode).ToArray());
    }

    /// <summary>Adds or replaces tiles[id] with { "connector": ..., "settings": ... }.</summary>
    public static void SetTile(JsonObject root, string id, TileConfig tile)
    {
        var tiles = ChildObject(root, "tiles");
        tiles[id] = new JsonObject
        {
            ["connector"] = tile.Connector,
            ["settings"] = JsonNode.Parse(tile.Settings.GetRawText()),
        };
    }

    /// <summary>A plain tile is its id, a pinned tile { "tile", "pin" }, a stack { "stack", "rotate" }.</summary>
    public static JsonNode ToNode(Slot slot) => slot switch
    {
        TileSlot { Pin: false } t => JsonValue.Create(t.TileId),
        TileSlot t => new JsonObject { ["tile"] = t.TileId, ["pin"] = true },
        StackSlot s => new JsonObject
        {
            ["stack"] = new JsonArray(s.TileIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["rotate"] = FormatDuration(s.Rotate),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>"2h", "5m" or seconds such as "30s" and "1.5s" (to the millisecond), all readable by Duration.TryParse.</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value > TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerHour == 0) return $"{(long)value.TotalHours}h";
        if (value > TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerMinute == 0) return $"{(long)value.TotalMinutes}m";
        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    static JsonObject ChildObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
```

**Replace** `src/TaskbarTiles.Host/Config/ConfigStore.cs` (whole file). The default config gains a comment line about edit mode; everything else that is not `Save` is unchanged except that `Load` and `Reload` remember the applied text:
```csharp
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Config;

/// <summary>Saves an edit of config.json. ConfigStore implements it; tests use a fake.</summary>
public interface IConfigWriter
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the file's JSON tree and saves it. Returns null on success, otherwise why
    /// nothing was written. Never throws.
    /// </summary>
    ConfigError? Save(Action<JsonObject> edit);
}

/// <summary>Owns config.json: creates it on first run, parses it, reloads it when it changes on disk, and saves edits.</summary>
public sealed class ConfigStore(string path, ITileLog log) : IConfigWriter, IDisposable
{
    public const string DefaultJson = """
    {
      // Each tile is driven by a connector (a plugin). Place tiles on displays under "displays".
      // Script tiles run the files in the scripts folder next to this file (docs/script-connectors.md in the repo).
      // Edit layout (right-click a tile) rewrites this file without comments; the previous file is kept as config.json.bak.
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } },
        "uptime":  { "connector": "script", "settings": { "hooks": {
                       "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                       "flyout":   { "run": "scripts/uptime.ps1" } } } }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true }, "uptime" ] },
        "*":       { "slots": [] }
      }
    }
    """;

    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    readonly object _reloadGate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    string? _appliedText; // the file text Current was parsed from; reloading the same text is a no-op

    public HostConfig Current { get; private set; } = HostConfig.Empty;
    public ConfigError? LastError { get; private set; }

    /// <summary>The copy of the previous file that every save leaves behind.</summary>
    public string BackupPath => path + ".bak";

    /// <summary>Raised after a reload (on a thread-pool thread) or a save (on the saving thread) changed Current.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on a thread-pool thread when an edited file fails to parse. Current stays unchanged.</summary>
    public event EventHandler<ConfigError>? ReloadFailed;

    public void Load()
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultJson);
                log.Info($"Created default config at {path}");
            }

            text = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Current = HostConfig.Empty;
            LastError = new ConfigError("Could not read config.json: " + ex.Message);
            log.Error(LastError.Message, ex);
            return;
        }

        var result = ConfigParser.Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
            _appliedText = text;
        }
        else
        {
            LastError = result.Error;
            log.Error($"config.json is invalid: {result.Error}");
        }
    }

    public void StartWatching()
    {
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Reads the file as it is on disk now, applies the edit and writes the result atomically (temp file, then replace),
    /// keeping the previous file as config.json.bak. Refuses to touch a file that does not parse, and refuses an edit
    /// whose result would not parse. The watcher then sees the text Current already came from and ignores it.
    /// </summary>
    public ConfigError? Save(Action<JsonObject> edit)
    {
        lock (_reloadGate)
        {
            try
            {
                var text = ReadWithRetry();
                var before = ConfigParser.Parse(text);
                if (!before.Success)
                    return new ConfigError("config.json has an error, so nothing was saved: " + before.Error!.Message,
                        before.Error.Line, before.Error.Column);

                var root = ConfigJson.ParseRoot(text);
                edit(root);
                var newText = ConfigJson.Serialize(root);
                var after = ConfigParser.Parse(newText);
                if (!after.Success) return new ConfigError("The change was not saved: " + after.Error);

                WriteAtomically(newText);
                Current = after.Config!;
                LastError = null;
                _appliedText = newText;
                log.Info($"Saved config.json; the previous version is in {Path.GetFileName(BackupPath)}");
            }
            catch (Exception ex)
            {
                log.Error("Saving config.json failed", ex);
                return new ConfigError("Could not save config.json: " + ex.Message);
            }
        }

        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { log.Error("A config Changed handler failed", ex); }
        return null;
    }

    void WriteAtomically(string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Replace(temp, path, BackupPath, ignoreMetadataErrors: true);
    }

    // Runs on a thread-pool timer. Nothing may escape: an unhandled exception would kill the host.
    void Reload()
    {
        lock (_reloadGate)
        {
            try
            {
                string text;
                try { text = ReadWithRetry(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Could not read config.json: {ex.Message}");
                    return;
                }

                // Our own save, or a save without changes: Current already reflects this text.
                if (LastError is null && text == _appliedText) return;

                var result = ConfigParser.Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
                    LastError = null;
                    _appliedText = text;
                    log.Info("Reloaded config.json");
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    LastError = result.Error;
                    log.Warn($"config.json edit rejected: {result.Error}");
                    ReloadFailed?.Invoke(this, result.Error!);
                }
            }
            catch (Exception ex)
            {
                log.Error("Config reload failed", ex);
            }
        }
    }

    // Editors often hold the file briefly while saving.
    string ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ConfigJsonTests|FullyQualifiedName~ConfigStoreSaveTests|FullyQualifiedName~ConfigStoreTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 348`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config/ConfigJson.cs src/TaskbarTiles.Host/Config/ConfigStore.cs tests/TaskbarTiles.Tests/ConfigJsonTests.cs tests/TaskbarTiles.Tests/ConfigStoreSaveTests.cs
git commit -m "feat(config): save config.json atomically with a .bak and no reload echo" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: LayoutEngine

**Files:**
- Create: `src/TaskbarTiles.Host/Layout/LayoutEngine.cs`
- Test: `tests/TaskbarTiles.Tests/LayoutEngineTests.cs` (new)

**Interfaces:**
- Consumes: nothing
- Produces (namespace `TaskbarTiles.Host.Layout`):
  - `enum SlotMode { Full, Compact, Overflow }`
  - `readonly record struct LayoutItem(double FullWidth, bool Pinned)`
  - `sealed record LayoutResult(IReadOnlyList<SlotMode> Modes, bool ShowOverflow, double Width)` with `int Shown`
  - `static class LayoutEngine { const double CompactWidth = 40; const double OverflowWidth = 40; static LayoutResult Compute(IReadOnlyList<LayoutItem> items, double available, double gap); }`
  - `Width` is the total DIP of what is shown (slots, the `»` tile, gaps); `ShowOverflow` is false and `Width` 0 when not even the `»` tile fits. `FitTiles` is removed in Task 9.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/LayoutEngineTests.cs`:
```csharp
using TaskbarTiles.Host.Layout;
using static TaskbarTiles.Host.Layout.SlotMode;

namespace TaskbarTiles.Tests;

public class LayoutEngineTests
{
    const double Gap = 6;

    static LayoutItem Tile(double width = 100, bool pinned = false) => new(width, pinned);

    static LayoutResult Run(double available, params LayoutItem[] items) => LayoutEngine.Compute(items, available, Gap);

    [Fact]
    public void Everything_fits_at_full_width()
    {
        var result = Run(312, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Full, Full }, result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(312, result.Width);
    }

    [Fact]
    public void Unpinned_tiles_become_compact_from_the_right_end()
    {
        // 100 + 6 + 100 + 6 + 40 = 252
        var result = Run(260, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Full, Compact }, result.Modes);
        Assert.Equal(252, result.Width);
    }

    [Fact]
    public void Compacting_continues_leftwards_until_it_fits()
    {
        // 100 + 6 + 40 + 6 + 40 = 192
        var result = Run(200, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Compact, Compact }, result.Modes);
    }

    [Fact]
    public void Pinned_tiles_are_never_compacted()
    {
        // pinned 268 + 6 + 40 + 6 + 40 = 360
        var result = Run(360, Tile(268, pinned: true), Tile(), Tile());

        Assert.Equal(new[] { Full, Compact, Compact }, result.Modes);
        Assert.Equal(360, result.Width);
    }

    [Fact]
    public void Narrow_tiles_are_not_compacted_because_it_gains_nothing()
    {
        // 30 + 6 + 40 = 76 fits once the 100 is compact; the 30 stays as it is
        var result = Run(80, Tile(30), Tile(100));

        Assert.Equal(new[] { Full, Compact }, result.Modes);
    }

    [Fact]
    public void Then_tiles_overflow_from_the_right_end_into_the_overflow_tile()
    {
        // all compact: 40*4 + 6*3 = 178 > 140; overflow the last: 40*3 + » 40 + 6*3 = 178 still; overflow two: 40*2 + 40 + 6*2 = 132
        var result = Run(140, Tile(), Tile(), Tile(), Tile());

        Assert.Equal(new[] { Compact, Compact, Overflow, Overflow }, result.Modes);
        Assert.True(result.ShowOverflow);
        Assert.Equal(132, result.Width);
        Assert.Equal(2, result.Shown);
    }

    [Fact]
    public void Pinned_tiles_overflow_last()
    {
        // pinned 100 + 6 + » 40 = 146
        var result = Run(150, Tile(), Tile(100, pinned: true), Tile());

        Assert.Equal(new[] { Overflow, Full, Overflow }, result.Modes);
        Assert.Equal(146, result.Width);
    }

    [Fact]
    public void Only_the_overflow_tile_when_nothing_else_fits()
    {
        var result = Run(45, Tile(100, pinned: true), Tile());

        Assert.Equal(new[] { Overflow, Overflow }, result.Modes);
        Assert.True(result.ShowOverflow);
        Assert.Equal(40, result.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    public void Nothing_is_shown_when_not_even_the_overflow_tile_fits(double available)
    {
        var result = Run(available, Tile(), Tile());

        Assert.Equal(new[] { Overflow, Overflow }, result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Shown);
    }

    [Fact]
    public void No_slots_means_nothing_to_show()
    {
        var result = Run(500);

        Assert.Empty(result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(0, result.Width);
    }

    [Fact]
    public void A_stack_is_one_item_with_its_widest_member_width()
    {
        // media 268 pinned, a stack of 132 and 268 counts as one 268 item: 268 + 6 + 268 = 542
        var result = Run(542, Tile(268, pinned: true), Tile(268));

        Assert.Equal(new[] { Full, Full }, result.Modes);
        var tighter = Run(541, Tile(268, pinned: true), Tile(268));
        Assert.Equal(new[] { Full, Compact }, tighter.Modes);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~LayoutEngineTests"`
Expected: build error CS0246 (namespace `TaskbarTiles.Host.Layout` / `LayoutItem` not found).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Layout/LayoutEngine.cs`:
```csharp
namespace TaskbarTiles.Host.Layout;

/// <summary>How a slot is shown: at full width, icon only, or inside the » overflow popup.</summary>
public enum SlotMode { Full, Compact, Overflow }

/// <summary>One slot as the layout sees it. A stack is one item.</summary>
public readonly record struct LayoutItem(double FullWidth, bool Pinned);

/// <summary>A mode per item, whether the » tile is shown, and the total width in DIP of everything shown.</summary>
public sealed record LayoutResult(IReadOnlyList<SlotMode> Modes, bool ShowOverflow, double Width)
{
    public int Shown => Modes.Count(m => m != SlotMode.Overflow);
}

/// <summary>
/// Fits slots into the free width: everything at full width if it fits; otherwise unpinned slots become compact
/// from the right end; then slots move into a » tile from the right end, unpinned ones first and pinned ones last.
/// Pure: the bar runs it on every free-zone or slot change.
/// </summary>
public static class LayoutEngine
{
    public const double CompactWidth = 40;
    public const double OverflowWidth = 40;

    public static LayoutResult Compute(IReadOnlyList<LayoutItem> items, double available, double gap)
    {
        if (!double.IsFinite(available) || available < 0) available = 0;
        var modes = Enumerable.Repeat(SlotMode.Full, items.Count).ToArray();
        if (items.Count == 0) return new LayoutResult(modes, false, 0);

        bool Fits() => WidthOf(items, modes, gap) <= available;

        if (Fits()) return Result(items, modes, gap);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Pinned || items[i].FullWidth <= CompactWidth) continue;
            modes[i] = SlotMode.Compact;
            if (Fits()) return Result(items, modes, gap);
        }

        var order = Enumerable.Range(0, items.Count).Reverse().OrderBy(i => items[i].Pinned ? 1 : 0); // stable: right to left within each group
        foreach (var i in order)
        {
            modes[i] = SlotMode.Overflow;
            if (Fits()) return Result(items, modes, gap);
        }

        return new LayoutResult(modes, false, 0); // not even the » tile fits
    }

    static LayoutResult Result(IReadOnlyList<LayoutItem> items, SlotMode[] modes, double gap) =>
        new(modes, modes.Contains(SlotMode.Overflow), WidthOf(items, modes, gap));

    static double WidthOf(IReadOnlyList<LayoutItem> items, SlotMode[] modes, double gap)
    {
        double width = 0;
        var shown = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (modes[i] == SlotMode.Overflow) continue;
            width += modes[i] == SlotMode.Compact ? CompactWidth : items[i].FullWidth;
            shown++;
        }
        if (modes.Contains(SlotMode.Overflow))
        {
            width += OverflowWidth;
            shown++;
        }
        return shown == 0 ? 0 : width + gap * (shown - 1);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~LayoutEngineTests"`
Expected: PASS (14 tests).

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 362`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Layout/LayoutEngine.cs tests/TaskbarTiles.Tests/LayoutEngineTests.cs
git commit -m "feat(layout): LayoutEngine fits slots as full, compact or overflow" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Stack rotation

**Files:**
- Create: `src/TaskbarTiles.Host/Layout/StackRotation.cs`
- Test: `tests/TaskbarTiles.Tests/StackRotationTests.cs` (new)

**Interfaces:**
- Consumes: nothing
- Produces: `sealed class StackRotation(int count)` (namespace `TaskbarTiles.Host.Layout`; count ≥ 1) with `int Count`, `int Index`, `bool Paused { get; set; }`, `bool Hovered { get; set; }`, `bool Suspended { get; set; }`, `int? TakenOverBy`, and `bool Tick()`, `bool Step(int delta)`, `bool SetUrgent(int member, bool urgent)`, `bool Acknowledge()` — each returns true when `Index` changed. UI thread only.

Rules: `Tick` advances unless suspended (edit mode), paused (menu) or hovered; while an urgent member holds the stack, `Tick` returns to it. `Step` (wheel) always works. A member that turns urgent takes over if nothing else holds the stack and it was not acknowledged; `Acknowledge` (a click while it is shown) releases it and hands over to the next urgent member, if any; it can take over again only after it stopped being urgent.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/StackRotationTests.cs`:
```csharp
using TaskbarTiles.Host.Layout;

namespace TaskbarTiles.Tests;

public class StackRotationTests
{
    [Fact]
    public void Ticks_advance_and_wrap()
    {
        var r = new StackRotation(3);

        Assert.True(r.Tick());
        Assert.Equal(1, r.Index);
        r.Tick();
        Assert.True(r.Tick());
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void A_single_member_never_moves()
    {
        var r = new StackRotation(1);
        Assert.False(r.Tick());
        Assert.False(r.Step(1));
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Zero_members_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackRotation(0));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Paused_hovered_or_suspended_stacks_do_not_rotate(bool paused, bool hovered, bool suspended)
    {
        var r = new StackRotation(3) { Paused = paused, Hovered = hovered, Suspended = suspended };

        Assert.False(r.Tick());
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Wheel_cycles_both_ways_even_when_paused()
    {
        var r = new StackRotation(3) { Paused = true };

        Assert.True(r.Step(-1));
        Assert.Equal(2, r.Index);
        r.Step(+1);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void An_urgent_member_takes_over_and_holds_the_stack()
    {
        var r = new StackRotation(3);

        Assert.True(r.SetUrgent(2, true));
        Assert.Equal(2, r.Index);
        Assert.Equal(2, r.TakenOverBy);
        Assert.False(r.Tick());
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void After_the_wheel_moved_away_a_tick_returns_to_the_urgent_member()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.Step(1);
        Assert.Equal(2, r.Index);
        Assert.True(r.Tick());
        Assert.Equal(1, r.Index);
    }

    [Fact]
    public void Clicking_the_urgent_member_releases_the_stack()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.Acknowledge();

        Assert.Null(r.TakenOverBy);
        Assert.True(r.Tick());
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void Clicking_while_another_member_is_shown_does_not_release()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);
        r.Step(1);

        Assert.False(r.Acknowledge());
        Assert.Equal(1, r.TakenOverBy);
    }

    [Fact]
    public void A_member_that_stays_urgent_does_not_take_over_again_after_the_click()
    {
        var r = new StackRotation(2);
        r.SetUrgent(1, true);
        r.Acknowledge();
        r.Tick();

        Assert.False(r.SetUrgent(1, true)); // same state reported again
        Assert.Null(r.TakenOverBy);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Becoming_urgent_anew_takes_over_again()
    {
        var r = new StackRotation(2);
        r.SetUrgent(1, true);
        r.Acknowledge();
        r.Tick();

        r.SetUrgent(1, false);
        Assert.True(r.SetUrgent(1, true));
        Assert.Equal(1, r.TakenOverBy);
    }

    [Fact]
    public void A_second_urgent_member_waits_its_turn()
    {
        var r = new StackRotation(3);
        r.SetUrgent(0, true);
        r.SetUrgent(2, true);
        Assert.Equal(0, r.TakenOverBy);

        Assert.True(r.Acknowledge());

        Assert.Equal(2, r.TakenOverBy);
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void An_urgent_member_that_calms_down_releases_the_stack_by_itself()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.SetUrgent(1, false);

        Assert.Null(r.TakenOverBy);
        Assert.True(r.Tick());
    }

    [Fact]
    public void Takeover_waits_while_suspended_but_is_remembered()
    {
        var r = new StackRotation(3) { Suspended = true };
        r.SetUrgent(2, true);
        r.Step(-2); // to 0 in edit mode

        Assert.False(r.Tick());
        r.Suspended = false;
        Assert.True(r.Tick());
        Assert.Equal(2, r.Index);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~StackRotationTests"`
Expected: build error CS0246 (`StackRotation` not found).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Layout/StackRotation.cs`:
```csharp
namespace TaskbarTiles.Host.Layout;

/// <summary>
/// Which member of a stack is shown. The bar calls <see cref="Tick"/> every "rotate"; it advances unless the stack is
/// paused (menu), hovered or suspended (edit mode). A member that becomes urgent takes over: it is shown and ticks keep
/// returning to it until the user clicks it (<see cref="Acknowledge"/>); it takes over again only after it stopped being
/// urgent and became urgent anew. Pure state; UI thread only. Methods return true when <see cref="Index"/> changed.
/// </summary>
public sealed class StackRotation
{
    readonly bool[] _urgent;
    readonly HashSet<int> _acknowledged = [];

    public StackRotation(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _urgent = new bool[count];
    }

    public int Count => _urgent.Length;
    public int Index { get; private set; }

    /// <summary>"Pause rotation" from the tile menu. Kept for the life of the bar, not saved.</summary>
    public bool Paused { get; set; }

    /// <summary>The mouse is over the stack.</summary>
    public bool Hovered { get; set; }

    /// <summary>Edit mode: nothing moves on its own.</summary>
    public bool Suspended { get; set; }

    /// <summary>The urgent member that holds the stack, if any.</summary>
    public int? TakenOverBy { get; private set; }

    public bool Tick()
    {
        if (Suspended) return false;
        if (TakenOverBy is { } holder) return Show(holder);
        if (Paused || Hovered || Count < 2) return false;
        return Show((Index + 1) % Count);
    }

    /// <summary>Mouse wheel: +1 next, -1 previous. Works while paused or taken over (ticks then return to the urgent member).</summary>
    public bool Step(int delta) => Show(((Index + delta) % Count + Count) % Count);

    public bool SetUrgent(int member, bool urgent)
    {
        if (member < 0 || member >= Count || _urgent[member] == urgent) return false;
        _urgent[member] = urgent;
        if (urgent)
        {
            if (TakenOverBy is not null || _acknowledged.Contains(member)) return false;
            TakenOverBy = member;
            return Show(member);
        }

        _acknowledged.Remove(member);
        if (TakenOverBy != member) return false;
        TakenOverBy = NextUrgent();
        return TakenOverBy is { } next && Show(next);
    }

    /// <summary>The user clicked the stack: an urgent member it shows has been seen and lets go.</summary>
    public bool Acknowledge()
    {
        if (TakenOverBy is not { } holder || holder != Index) return false;
        _acknowledged.Add(holder);
        TakenOverBy = NextUrgent();
        return TakenOverBy is { } next && Show(next);
    }

    int? NextUrgent()
    {
        for (var i = 0; i < Count; i++)
            if (_urgent[i] && !_acknowledged.Contains(i)) return i;
        return null;
    }

    bool Show(int index)
    {
        if (index == Index) return false;
        Index = index;
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~StackRotationTests"`
Expected: PASS (16 tests).

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 378`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Layout/StackRotation.cs tests/TaskbarTiles.Tests/StackRotationTests.cs
git commit -m "feat(layout): stack rotation with hover pause, wheel and urgent takeover" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Bars get slots; layout edits save per display

**Files:**
- Create: `src/TaskbarTiles.Host/Layout/BarSlot.cs`
- Modify: `src/TaskbarTiles.Host/HostController.cs` (whole file), `src/TaskbarTiles.Host/Config/DisplayResolver.cs` (whole file: `VisibleTileIds` removed), `src/TaskbarTiles.Host/UI/TileBar.cs` (temporary `SetSlots` that shows the first member of each slot, replaced in Task 8), `src/TaskbarTiles.Host/App.xaml.cs` (passes the writer)
- Test: `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs` (new), `tests/TaskbarTiles.Tests/TestDoubles.cs`, `tests/TaskbarTiles.Tests/DisplayResolverTests.cs`

**Interfaces:**
- Consumes: `DisplayResolver.Resolve/EditKey/PlacedTileIds` (Task 1); `LayoutEdits` (Task 2); `IConfigWriter`, `ConfigJson.SetDisplay` (Task 3)
- Produces:
  - `sealed record BarSlot(int Index, IReadOnlyList<TileRuntime> Members, bool Pinned, TimeSpan? Rotate)` (namespace `TaskbarTiles.Host.Layout`) with `bool IsStack`, `double FullWidth` (widest member), `bool SameContent(BarSlot other)` (ignores `Index`), value equality over members
  - `interface ITileBarView { string DisplayId; void SetSlots(IReadOnlyList<BarSlot> slots); void Apply(TaskbarInfo info); void Close(); }` (Task 10 adds `SetEditing`)
  - `interface ILayoutEditor { ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit); }` (Tasks 7 and 10 add members)
  - `HostController(ConnectorCatalog catalog, Func<HostConfig> config, ITaskbarSource taskbars, IUiDispatcher ui, Func<string, ITileLog> logFor, Func<string, ITileBarView> createBar, string? configDirectory = null, IConfigWriter? writer = null) : ILayoutEditor, IAsyncDisposable`
  - `TileBar.SetSlots(IReadOnlyList<BarSlot>)` (temporary body)
  - Test doubles: `FakeBar.Slots`, `FakeBar.Tiles` (all members flattened), `FakeConfigWriter(string json)` with `Json`, `Saves`, `Current`

Behavior: one runtime per tile id placed on any display, stack members included. Bars get one `BarSlot` per config slot (an empty stack is skipped but later slots keep their config index). `EditLayout` resolves the display's layout, applies the edit, does nothing if the slots are unchanged, otherwise saves `displays[EditKey]` through the writer and posts a re-sync to the UI dispatcher (not inline: the request comes from a handler of a view the sync may rebuild). Without a writer, or for a display that is gone, it returns an error and saves nothing.

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TestDoubles.cs`, find:
```csharp
public sealed class FakeBar(string displayId) : TaskbarTiles.Host.ITileBarView
{
    public string DisplayId { get; } = displayId;
    public IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> Tiles { get; private set; } = [];
    public TaskbarTiles.Host.Taskbar.TaskbarInfo? Info { get; private set; }
    public bool Closed { get; private set; }
    public void SetTiles(IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> runtimes) => Tiles = runtimes;
```
Replace with:
```csharp
public sealed class FakeBar(string displayId) : TaskbarTiles.Host.ITileBarView
{
    public string DisplayId { get; } = displayId;
    public IReadOnlyList<TaskbarTiles.Host.Layout.BarSlot> Slots { get; private set; } = [];
    /// <summary>Every member of every slot, in order.</summary>
    public IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> Tiles => Slots.SelectMany(s => s.Members).ToList();
    public TaskbarTiles.Host.Taskbar.TaskbarInfo? Info { get; private set; }
    public bool Closed { get; private set; }
    public void SetSlots(IReadOnlyList<TaskbarTiles.Host.Layout.BarSlot> slots) => Slots = slots;
```

**Edit** `tests/TaskbarTiles.Tests/TestDoubles.cs`, find:
```csharp
public static class ScriptFiles
```
Replace with:
```csharp
/// <summary>An in-memory config.json: Save edits the JSON text the way ConfigStore does, without a file.</summary>
public sealed class FakeConfigWriter(string json) : TaskbarTiles.Host.Config.IConfigWriter
{
    public string Json { get; private set; } = json;
    public int Saves { get; private set; }
    public TaskbarTiles.Host.Config.HostConfig Current => TaskbarTiles.Host.Config.ConfigParser.Parse(Json).Config!;

    public TaskbarTiles.Host.Config.ConfigError? Save(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var root = TaskbarTiles.Host.Config.ConfigJson.ParseRoot(Json);
        edit(root);
        var text = TaskbarTiles.Host.Config.ConfigJson.Serialize(root);
        var parsed = TaskbarTiles.Host.Config.ConfigParser.Parse(text);
        if (!parsed.Success) return parsed.Error;
        Json = text;
        Saves++;
        return null;
    }
}

public static class ScriptFiles
```

**Edit** `tests/TaskbarTiles.Tests/DisplayResolverTests.cs` (the milestone 1 behavior goes away), find:
```csharp
    [Fact]
    public void Visible_tile_ids_take_first_item_of_each_stack()
    {
        var layout = new DisplayLayout([new TileSlot("a", false), new StackSlot(["b", "c"], TimeSpan.FromSeconds(5)), new StackSlot([], TimeSpan.FromSeconds(5))]);
        Assert.Equal(new[] { "a", "b" }, DisplayResolver.VisibleTileIds(layout));
    }

    [Fact]
    public void Placed_tile_ids_include_every_stack_member_once()
```
Replace with:
```csharp
    [Fact]
    public void Placed_tile_ids_include_every_stack_member_once()
```

**Create** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class HostControllerLayoutTests
{
    const string Config = """
    { "tiles": { "a": { "connector": "ghost" }, "b": { "connector": "ghost" }, "c": { "connector": "ghost" } },
      "displays": {
        "primary": { "slots": [ { "tile": "a", "pin": true }, { "stack": ["b", "c"], "rotate": "8s" } ] },
        "*": { "slots": ["c"] } } }
    """;

    sealed class Fixture
    {
        public Fixture(string json, params (string Id, bool Primary)[] displays)
        {
            Writer = new FakeConfigWriter(json);
            Source = new FakeTaskbarSource
            {
                Current = displays.Length == 0
                    ? [FakeTaskbarSource.Display("MAIN", true)]
                    : displays.Select(d => FakeTaskbarSource.Display(d.Id, d.Primary)).ToList(),
            };
            Host = new HostController(ConnectorCatalog.Empty, () => Writer.Current, Source, new ImmediateDispatcher(),
                _ => new CollectingLog(), id => Bars[id] = new FakeBar(id), writer: Writer);
            Host.Start();
        }

        public FakeConfigWriter Writer { get; }
        public FakeTaskbarSource Source { get; }
        public Dictionary<string, FakeBar> Bars { get; } = [];
        public HostController Host { get; }

        public JsonElement Displays => JsonDocument.Parse(Writer.Json).RootElement.GetProperty("displays");
    }

    [Fact]
    public void A_stack_is_one_slot_and_all_its_members_get_runtimes()
    {
        var f = new Fixture(Config);

        var slots = f.Bars["MAIN"].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Index);
        Assert.True(slots[0].Pinned);
        Assert.False(slots[0].IsStack);
        Assert.Equal(1, slots[1].Index);
        Assert.Equal(TimeSpan.FromSeconds(8), slots[1].Rotate);
        Assert.Equal(new[] { "b", "c" }, slots[1].Members.Select(m => m.TileId));
        Assert.Equal(new[] { "a", "b", "c" }, f.Host.Runtimes.Keys.Order());
    }

    [Fact]
    public void An_empty_stack_shows_nothing_but_later_slots_keep_their_index()
    {
        var f = new Fixture("""
        { "tiles": { "a": { "connector": "ghost" } }, "displays": { "primary": { "slots": [ { "stack": [] }, "a" ] } } }
        """);

        var slot = Assert.Single(f.Bars["MAIN"].Slots);
        Assert.Equal(1, slot.Index);
    }

    [Fact]
    public void Editing_the_primary_display_writes_primary()
    {
        var f = new Fixture(Config);

        var error = f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After));

        Assert.Null(error);
        Assert.Equal(JsonValueKind.Object, f.Displays.GetProperty("primary").GetProperty("slots")[0].ValueKind); // the stack now first
        Assert.False(f.Displays.TryGetProperty("MAIN", out _));
        Assert.Equal(new[] { "b", "c", "a" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_a_display_that_uses_the_star_forks_its_own_entry()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false), ("THIRD", false));

        var error = f.Host.EditLayout("SECOND", l => LayoutEdits.Append(l, "a"));

        Assert.Null(error);
        Assert.Equal(new[] { "c", "a" }, f.Displays.GetProperty("SECOND").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "c" }, f.Displays.GetProperty("*").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "c", "a" }, f.Bars["SECOND"].Tiles.Select(t => t.TileId));
        Assert.Equal(new[] { "c" }, f.Bars["THIRD"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_a_display_with_its_own_entry_writes_that_entry()
    {
        var json = Config.Replace("\"*\":", "\"MAIN\": { \"slots\": [\"a\"] }, \"*\":");
        var f = new Fixture(json);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Append(l, "b")));

        Assert.Equal(new[] { "a", "b" }, f.Displays.GetProperty("MAIN").GetProperty("slots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2, f.Displays.GetProperty("primary").GetProperty("slots").GetArrayLength()); // untouched
    }

    [Fact]
    public void An_edit_that_changes_nothing_saves_nothing()
    {
        var f = new Fixture(Config);

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 0, DropPosition.Before)));

        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void A_rejected_save_returns_the_error_and_changes_nothing()
    {
        var f = new Fixture(Config);

        var error = f.Host.EditLayout("MAIN", l => LayoutEdits.Append(l, "ghost"));

        Assert.NotNull(error);
        Assert.Contains("ghost", error!.Message);
        Assert.Equal(0, f.Writer.Saves);
        Assert.Equal(new[] { "a", "b", "c" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Editing_an_unknown_display_is_an_error()
    {
        var f = new Fixture(Config);

        Assert.NotNull(f.Host.EditLayout("GONE", l => LayoutEdits.Append(l, "a")));
        Assert.Equal(0, f.Writer.Saves);
    }

    [Fact]
    public void Without_a_writer_edits_are_refused()
    {
        var cfg = ConfigParser.Parse(Config).Config!;
        var host = new HostController(ConnectorCatalog.Empty, () => cfg, new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] },
            new ImmediateDispatcher(), _ => new CollectingLog(), id => new FakeBar(id));
        host.Start();

        Assert.NotNull(host.EditLayout("MAIN", l => LayoutEdits.Drop(l, 0, 1, DropPosition.After)));
    }

    [Fact]
    public void Removing_a_stack_member_keeps_its_runtime_while_another_display_places_it()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));
        var c = f.Host.Runtimes["c"];

        Assert.Null(f.Host.EditLayout("MAIN", l => LayoutEdits.Remove(l, 1, member: 1)));

        Assert.Same(c, f.Host.Runtimes["c"]); // still on SECOND through "*"
        Assert.Equal(new[] { "a", "b" }, f.Bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.True(f.Writer.Current.Tiles.ContainsKey("c")); // the definition stays
    }
}
```

The existing `HostControllerTests` keep working unchanged: they read `FakeBar.Tiles`, which is now every member of every slot.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~HostController"`
Expected: build errors CS0246 (`BarSlot` not found) and CS0535 (`FakeBar` does not implement `ITileBarView.SetTiles`).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Layout/BarSlot.cs`:
```csharp
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One slot as a bar shows it: a single tile (one member, <see cref="Rotate"/> null) or a stack.
/// <see cref="Index"/> is the slot's position in the display's config slots, which layout edits refer to;
/// member i of a stack is the stack's i-th tile id.
/// </summary>
public sealed record BarSlot(int Index, IReadOnlyList<TileRuntime> Members, bool Pinned, TimeSpan? Rotate)
{
    public bool IsStack => Rotate is not null;

    /// <summary>A stack is as wide as its widest member, so it never changes width while it rotates.</summary>
    public double FullWidth => Members.Count == 0 ? 0 : Members.Max(m => m.Width);

    /// <summary>Same tiles, pin and rotation, wherever the slot sits.</summary>
    public bool SameContent(BarSlot other) =>
        Pinned == other.Pinned && Rotate == other.Rotate && Members.SequenceEqual(other.Members);

    // Records compare lists by reference; compare members so equal slots are equal.
    public bool Equals(BarSlot? other) => other is not null && Index == other.Index && SameContent(other);

    public override int GetHashCode() => HashCode.Combine(Index, Members.Count, Pinned, Rotate);
}
```

**Replace** `src/TaskbarTiles.Host/HostController.cs` (whole file):
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetSlots(IReadOnlyList<BarSlot> slots);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>Layout changes a bar asks for. HostController implements it. UI thread only.</summary>
public interface ILayoutEditor
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the layout the display shows and saves config.json under the display's
    /// edit key (<see cref="DisplayResolver.EditKey"/>). Returns null when saved or when nothing changed, otherwise why
    /// nothing was saved.
    /// </summary>
    ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit);
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar,
    string? configDirectory = null,
    IConfigWriter? writer = null) : ILayoutEditor, IAsyncDisposable
{
    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    public ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit)
    {
        if (_disposed) return new ConfigError("Taskbar Tiles is shutting down.");
        if (writer is null) return new ConfigError("This host cannot save config.json.");
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        if (display is null) return new ConfigError($"Display '{displayId}' is no longer connected.");

        var cfg = config();
        var layout = DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        var changed = edit(layout);
        if (LayoutEdits.SameSlots(layout, changed)) return null;

        var key = DisplayResolver.EditKey(cfg, displayId, display.IsPrimary);
        var error = writer.Save(root => ConfigJson.SetDisplay(root, key, changed));
        if (error is not null) return error;

        logFor("host").Info($"Saved the layout of display '{displayId}' under \"{key}\"");
        ui.Post(Sync); // not inline: the request usually comes from a handler of a view this rebuilds
        return null;
    }

    void Sync()
    {
        if (_disposed) return;
        var cfg = config();
        var displays = taskbars.Current.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        var layouts = displays.ToDictionary(d => d.DisplayId, d => DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary));

        SyncRuntimes(cfg, layouts.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            try { _bars[gone].Close(); }
            catch (Exception ex) { logFor("host").Error($"Closing bar for display '{gone}' failed", ex); }
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            try
            {
                if (!_bars.TryGetValue(info.DisplayId, out var bar))
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                bar.SetSlots(BarSlots(layouts[info.DisplayId]));
                bar.Apply(info);
            }
            catch (Exception ex)
            {
                logFor("host").Error($"Updating bar for display '{info.DisplayId}' failed", ex);
            }
        }
    }

    /// <summary>Config slots as the bar shows them; an empty stack shows nothing but keeps its index.</summary>
    IReadOnlyList<BarSlot> BarSlots(DisplayLayout layout)
    {
        var slots = new List<BarSlot>();
        for (var i = 0; i < layout.Slots.Count; i++)
        {
            switch (layout.Slots[i])
            {
                case TileSlot t when _runtimes.TryGetValue(t.TileId, out var entry):
                    slots.Add(new BarSlot(i, [entry.Runtime], t.Pin, null));
                    break;
                case StackSlot s when s.TileIds.Count > 0 && s.TileIds.All(_runtimes.ContainsKey):
                    slots.Add(new BarSlot(i, s.TileIds.Select(id => _runtimes[id].Runtime).ToList(), false, s.Rotate));
                    break;
            }
        }
        return slots;
    }

    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        foreach (var (id, entry) in _runtimes.ToList())
        {
            var keep = wanted.Contains(id) && cfg.Tiles.TryGetValue(id, out var now) && SameConfig(now, entry.Config);
            if (keep) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var id in wanted)
        {
            if (_runtimes.ContainsKey(id) || !cfg.Tiles.TryGetValue(id, out var tileCfg)) continue;

            var connectorId = tileCfg.Connector;
            // The runtime calls this on the thread pool (plugin constructors are plugin code) and again for every restart.
            // A throwing constructor shows the error look and is retried with backoff.
            Func<ITileConnector> create = () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId);
            var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
            var runtime = new TileRuntime(id, connectorId, width, create, tileCfg.Settings, ui, logFor($"tile-{id}"), _runtimeOptions);
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    /// <summary>
    /// Bars (WPF windows) are closed synchronously before the first await, so call this on the UI thread.
    /// Everything after the first await is off-thread safe (ConfigureAwait(false)).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var bar in _bars.Values)
        {
            try { bar.Close(); }
            catch (Exception ex) { logFor("host").Error("Closing a bar failed", ex); }
        }
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
```

**Replace** `src/TaskbarTiles.Host/Config/DisplayResolver.cs` (whole file):
```csharp
namespace TaskbarTiles.Host.Config;

public static class DisplayResolver
{
    public const string PrimaryKey = "primary";
    public const string FallbackKey = "*";

    /// <summary>Exact device id, then "primary" for the primary display, then "*".</summary>
    public static DisplayLayout Resolve(HostConfig config, string displayId, bool isPrimary)
    {
        if (config.Displays.TryGetValue(displayId, out var exact)) return exact;
        if (isPrimary && config.Displays.TryGetValue(PrimaryKey, out var primary)) return primary;
        return config.Displays.TryGetValue(FallbackKey, out var any) ? any : DisplayLayout.Empty;
    }

    /// <summary>
    /// The "displays" key edit mode writes for this display: the device id when it has its own entry;
    /// otherwise "primary" on the primary display (that key only ever applies to the primary display);
    /// otherwise the device id, so editing one display never changes the other displays that share "*".
    /// </summary>
    public static string EditKey(HostConfig config, string displayId, bool isPrimary) =>
        isPrimary && !config.Displays.ContainsKey(displayId) ? PrimaryKey : displayId;

    /// <summary>Every tile the layout places, stack members included, each once, in order of appearance.</summary>
    public static IReadOnlyList<string> PlacedTileIds(DisplayLayout layout) =>
        layout.Slots
            .SelectMany(s => s switch
            {
                TileSlot t => [t.TileId],
                StackSlot st => st.TileIds,
                _ => (IReadOnlyList<string>)[],
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

}
```

Keep the bar compiling until Task 8 renders stacks.

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
```
Replace with:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
```

**Edit** `src/TaskbarTiles.Host/UI/TileBar.cs`, find:
```csharp
    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    public void SetTiles(IReadOnlyList<TileRuntime> runtimes)
```
Replace with:
```csharp
    /// <summary>Shows the first member of each slot (stacks render in full later in milestone 3).</summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots) => SetTiles(slots.Select(s => s.Members[0]).ToList());

    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    void SetTiles(IReadOnlyList<TileRuntime> runtimes)
```

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir);
```
Replace with:
```csharp
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir, _config);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~HostController|FullyQualifiedName~DisplayResolverTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 387`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Layout/BarSlot.cs src/TaskbarTiles.Host/HostController.cs src/TaskbarTiles.Host/Config/DisplayResolver.cs src/TaskbarTiles.Host/UI/TileBar.cs src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/DisplayResolverTests.cs
git commit -m "feat(host): bars get slots, every stack member runs, layout edits save per display" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Tile gallery

**Files:**
- Create: `src/TaskbarTiles.Host/Layout/TileGallery.cs`
- Modify: `src/TaskbarTiles.Host/HostController.cs` (whole file)
- Test: `tests/TaskbarTiles.Tests/TileGalleryTests.cs` (new)

**Interfaces:**
- Consumes: `DisplayResolver.PlacedTileIds` (Task 1); `LayoutEdits.Append` (Task 2); `ConfigJson.SetTile/SetDisplay` (Task 3); `HostController`, `ILayoutEditor`, `FakeConfigWriter` (Task 6); `ScriptSettings.Parse` (script plugin, tests only)
- Produces (namespace `TaskbarTiles.Host.Layout`):
  - `sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile)` (`NewTile` null = place the existing tile `TileId`)
  - `sealed record ScriptTemplate(string Id, string Title, string Script, string SettingsJson)`; `static class ScriptTemplates { static IReadOnlyList<ScriptTemplate> Samples }` (`uptime`, `ping`)
  - `static class TileGallery { const string ScriptConnectorId = "script"; static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayLayout layout, IEnumerable<string> connectorIds, IReadOnlyList<ScriptTemplate> templates); static string UniqueId(string baseId, IEnumerable<string> taken); }`
  - `ILayoutEditor` + `IReadOnlyList<GalleryItem> GalleryFor(string displayId)`, `ConfigError? AddFromGallery(string displayId, GalleryItem item)`
  - `HostController` constructor + `IReadOnlyList<ScriptTemplate>? templates = null` (last parameter; null = `ScriptTemplates.Samples`)

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/TileGalleryTests.cs`:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class TileGalleryTests
{
    const string Config = """
    { "tiles": { "spotify": { "connector": "media" }, "ci": { "connector": "script", "settings": { "hooks": { "click": "x" } } }, "uptime": { "connector": "script", "settings": { "hooks": { "click": "x" } } } },
      "displays": { "primary": { "slots": [ { "stack": ["spotify"] } ] } } }
    """;

    static readonly ScriptTemplate Template = new("uptime", "Clock and uptime", "scripts/uptime.ps1", """{ "hooks": { "click": "scripts/uptime.ps1" } }""");

    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    [Fact]
    public void Lists_unplaced_tiles_then_plugin_connectors_then_script_templates()
    {
        var cfg = Parse(Config);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], ["sample", "script", "media"], [Template]);

        Assert.Equal(new[] { "ci", "uptime", "media", "sample", "Clock and uptime" }, items.Select(i => i.Title));
        Assert.Null(items[0].NewTile);
        Assert.Equal("Your tile · script", items[0].Subtitle);
        Assert.Equal("media", items[2].NewTile!.Connector);
        Assert.Equal("{}", items[2].NewTile!.Settings.GetRawText());
        Assert.Equal("uptime", items[4].TileId);
        Assert.Equal("script", items[4].NewTile!.Connector);
        Assert.Equal("Script · scripts/uptime.ps1", items[4].Subtitle);
    }

    [Theory]
    [InlineData(new string[0], "ping")]
    [InlineData(new[] { "ping" }, "ping-2")]
    [InlineData(new[] { "ping", "ping-2", "ping-4" }, "ping-3")]
    public void Unique_id_appends_the_first_free_number(string[] taken, string expected) =>
        Assert.Equal(expected, TileGallery.UniqueId("ping", taken));

    [Fact]
    public void Sample_templates_point_at_shipped_scripts_and_have_valid_hooks()
    {
        var repo = RepoRoot();
        Assert.NotEmpty(ScriptTemplates.Samples);
        foreach (var t in ScriptTemplates.Samples)
        {
            Assert.True(File.Exists(Path.Combine(repo, t.Script)), $"{t.Script} is missing");
            using var doc = JsonDocument.Parse(t.SettingsJson);
            Assert.Empty(ScriptSettings.Parse(doc.RootElement).Errors);
            Assert.Contains(t.Script, t.SettingsJson);
        }
    }

    [Fact]
    public void Adding_a_new_tile_writes_its_definition_under_a_free_id_and_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var bars = new Dictionary<string, FakeBar>();
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars[id] = new FakeBar(id), writer: writer, templates: [Template]);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item));

        var cfg = writer.Current;
        Assert.Equal("script", cfg.Tiles["uptime-2"].Connector);
        Assert.Equal("scripts/uptime.ps1", cfg.Tiles["uptime-2"].Settings.GetProperty("hooks").GetProperty("click").GetString());
        Assert.Equal(new TileSlot("uptime-2", false), cfg.Displays["primary"].Slots[^1]);
        Assert.Equal(new[] { "spotify", "uptime-2" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Adding_an_existing_tile_only_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, templates: []);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal(3, writer.Current.Tiles.Count);
        Assert.Equal(new TileSlot("ci", false), writer.Current.Displays["primary"].Slots[^1]);
        Assert.DoesNotContain(host.GalleryFor("MAIN"), i => i.Title == "ci");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TaskbarTiles.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TaskbarTiles.sln not found above the test output folder.");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileGalleryTests"`
Expected: build error CS0246 (`ScriptTemplate` not found).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/Layout/TileGallery.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One choice in edit mode's "+" gallery. <see cref="NewTile"/> null: place the existing tile <see cref="TileId"/>.
/// Otherwise: add a new tile with that definition under a free id based on <see cref="TileId"/>.
/// </summary>
public sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile);

/// <summary>A ready-made script tile for one of the sample scripts shipped in scripts\.</summary>
public sealed record ScriptTemplate(string Id, string Title, string Script, string SettingsJson);

public static class ScriptTemplates
{
    /// <summary>The samples in the repo's scripts folder, copied to %LOCALAPPDATA%\TaskbarTiles\scripts on first run.</summary>
    public static IReadOnlyList<ScriptTemplate> Samples { get; } =
    [
        new("uptime", "Clock and uptime", "scripts/uptime.ps1", """
            { "hooks": {
                "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                "flyout":   { "run": "scripts/uptime.ps1" } } }
            """),
        new("ping", "Ping a host", "scripts/ping.ps1", """
            { "host": "1.1.1.1", "hooks": {
                "interval":    { "every": "15s", "run": "scripts/ping.ps1" },
                "flyout":      { "run": "scripts/ping.ps1" },
                "action:ping": { "run": "scripts/ping.ps1" } } }
            """),
    ];
}

public static class TileGallery
{
    /// <summary>The script connector needs hooks, so it is offered through the script templates instead.</summary>
    public const string ScriptConnectorId = "script";

    /// <summary>
    /// Existing tiles this display does not place yet (in config order), then a new tile of each loaded connector
    /// (by id), then the script templates.
    /// </summary>
    public static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayLayout layout,
        IEnumerable<string> connectorIds, IReadOnlyList<ScriptTemplate> templates)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        var items = new List<GalleryItem>();

        foreach (var (id, tile) in config.Tiles)
            if (!placed.Contains(id))
                items.Add(new GalleryItem(id, $"Your tile · {tile.Connector}", id, null));

        foreach (var connector in connectorIds.Where(c => c != ScriptConnectorId).Order(StringComparer.Ordinal))
            items.Add(new GalleryItem(connector, "New tile from the plugin", connector, new TileConfig(connector, Json("{}"))));

        foreach (var t in templates)
            items.Add(new GalleryItem(t.Title, $"Script · {t.Script}", t.Id, new TileConfig(ScriptConnectorId, Json(t.SettingsJson))));

        return items;
    }

    /// <summary><paramref name="baseId"/> if it is free, else baseId-2, baseId-3, …</summary>
    public static string UniqueId(string baseId, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseId)) return baseId;
        for (var n = 2; ; n++)
            if (!used.Contains($"{baseId}-{n}")) return $"{baseId}-{n}";
    }

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
```

**Replace** `src/TaskbarTiles.Host/HostController.cs` (whole file):
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetSlots(IReadOnlyList<BarSlot> slots);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>Layout changes a bar asks for. HostController implements it. UI thread only.</summary>
public interface ILayoutEditor
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the layout the display shows and saves config.json under the display's
    /// edit key (<see cref="DisplayResolver.EditKey"/>). Returns null when saved or when nothing changed, otherwise why
    /// nothing was saved.
    /// </summary>
    ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit);

    /// <summary>What edit mode's "+" offers on this display.</summary>
    IReadOnlyList<GalleryItem> GalleryFor(string displayId);

    /// <summary>Places the gallery item at the end of the display's layout, adding its tile definition when it is new.</summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item);
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar,
    string? configDirectory = null,
    IConfigWriter? writer = null,
    IReadOnlyList<ScriptTemplate>? templates = null) : ILayoutEditor, IAsyncDisposable
{
    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

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

    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
    {
        var cfg = config();
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        var layout = display is null ? DisplayLayout.Empty : DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        return TileGallery.Build(cfg, layout, catalog.Connectors.Keys, templates ?? ScriptTemplates.Samples);
    }

    public ConfigError? AddFromGallery(string displayId, GalleryItem item)
    {
        if (item.NewTile is not { } definition) return EditLayout(displayId, l => LayoutEdits.Append(l, item.TileId));

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

    /// <summary>The display to edit, or (null, why not).</summary>
    (TaskbarInfo? Display, ConfigError? Refused) EditTarget(string displayId)
    {
        if (_disposed) return (null, new ConfigError("Taskbar Tiles is shutting down."));
        if (writer is null) return (null, new ConfigError("This host cannot save config.json."));
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        return display is null ? (null, new ConfigError($"Display '{displayId}' is no longer connected.")) : (display, null);
    }

    ConfigError? Saved(string displayId, string key, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved the layout of display '{displayId}' under \"{key}\"");
        ui.Post(Sync); // not inline: the request usually comes from a handler of a view this rebuilds
        return null;
    }

    void Sync()
    {
        if (_disposed) return;
        var cfg = config();
        var displays = taskbars.Current.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        var layouts = displays.ToDictionary(d => d.DisplayId, d => DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary));

        SyncRuntimes(cfg, layouts.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            try { _bars[gone].Close(); }
            catch (Exception ex) { logFor("host").Error($"Closing bar for display '{gone}' failed", ex); }
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            try
            {
                if (!_bars.TryGetValue(info.DisplayId, out var bar))
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                bar.SetSlots(BarSlots(layouts[info.DisplayId]));
                bar.Apply(info);
            }
            catch (Exception ex)
            {
                logFor("host").Error($"Updating bar for display '{info.DisplayId}' failed", ex);
            }
        }
    }

    /// <summary>Config slots as the bar shows them; an empty stack shows nothing but keeps its index.</summary>
    IReadOnlyList<BarSlot> BarSlots(DisplayLayout layout)
    {
        var slots = new List<BarSlot>();
        for (var i = 0; i < layout.Slots.Count; i++)
        {
            switch (layout.Slots[i])
            {
                case TileSlot t when _runtimes.TryGetValue(t.TileId, out var entry):
                    slots.Add(new BarSlot(i, [entry.Runtime], t.Pin, null));
                    break;
                case StackSlot s when s.TileIds.Count > 0 && s.TileIds.All(_runtimes.ContainsKey):
                    slots.Add(new BarSlot(i, s.TileIds.Select(id => _runtimes[id].Runtime).ToList(), false, s.Rotate));
                    break;
            }
        }
        return slots;
    }

    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        foreach (var (id, entry) in _runtimes.ToList())
        {
            var keep = wanted.Contains(id) && cfg.Tiles.TryGetValue(id, out var now) && SameConfig(now, entry.Config);
            if (keep) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var id in wanted)
        {
            if (_runtimes.ContainsKey(id) || !cfg.Tiles.TryGetValue(id, out var tileCfg)) continue;

            var connectorId = tileCfg.Connector;
            // The runtime calls this on the thread pool (plugin constructors are plugin code) and again for every restart.
            // A throwing constructor shows the error look and is retried with backoff.
            Func<ITileConnector> create = () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId);
            var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
            var runtime = new TileRuntime(id, connectorId, width, create, tileCfg.Settings, ui, logFor($"tile-{id}"), _runtimeOptions);
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    /// <summary>
    /// Bars (WPF windows) are closed synchronously before the first await, so call this on the UI thread.
    /// Everything after the first await is off-thread safe (ConfigureAwait(false)).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var bar in _bars.Values)
        {
            try { bar.Close(); }
            catch (Exception ex) { logFor("host").Error("Closing a bar failed", ex); }
        }
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileGalleryTests|FullyQualifiedName~HostController"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 394`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Layout/TileGallery.cs src/TaskbarTiles.Host/HostController.cs tests/TaskbarTiles.Tests/TileGalleryTests.cs
git commit -m "feat(layout): tile gallery with unplaced tiles, plugin connectors and script templates" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Stacks on the bar, compact tile look, inline buttons through TileCommands

**Files:**
- Create: `src/TaskbarTiles.Host/UI/TileText.cs`, `src/TaskbarTiles.Host/UI/SlotView.cs`
- Modify: `src/TaskbarTiles.Host/UI/TileView.xaml`, `src/TaskbarTiles.Host/UI/TileView.xaml.cs` (whole file), `src/TaskbarTiles.Host/UI/FlyoutWindow.cs` (whole file), `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file)
- Test: `tests/TaskbarTiles.Tests/TileTextTests.cs`, `tests/TaskbarTiles.Tests/SlotViewTests.cs` (new)

**Interfaces:**
- Consumes: `BarSlot` (Task 6), `StackRotation` (Task 5), `LayoutEngine.CompactWidth` (Task 4), `TileCommands.InvokeAsync(TileRuntime, string)` (existing), `TileRuntime.State/StateChanged/Width`
- Produces (namespace `TaskbarTiles.Host.UI`):
  - `static class TileText { static string Monogram(string? title); static string? CompactTooltip(TileState state); }`
  - `TileView.Compact { get; set; }` (icon only; the owner sets the width), `TileView.FlyoutAnchor { get; set; }` (null = the tile itself)
  - `FlyoutWindow.ShowPanel(FrameworkElement anchor, UIElement content, bool light, ITileLog log, double? width = 320, Action? onClosed = null)` (null width = size to content); `public static TextBlock FlyoutWindow.Text(string text, double size, FontWeight weight, string brushKey)`
  - `sealed class SlotView : Grid` — `SlotView(BarSlot slot, Func<bool> isLightTheme, TileCommands commands, ITileLog log)`, `BarSlot Slot`, `StackRotation? Rotation` (null for a single tile), `int CurrentMember`, `TileView Current`, `double FullWidth`, `bool Compact { get; set; }`, `FrameworkElement? FlyoutAnchor { set; }`, `void Retarget(BarSlot slot)` (same content, new index), `void Detach()`
  - test helper `Sta.Run(Action)` (in `SlotViewTests.cs`): runs WPF code on an STA thread and rethrows
  - `TileBar.SetSlots` renders one `SlotView` per slot (still fitted with `FitTiles` until Task 9); views with unchanged content are reused, so a stack keeps its rotation state across a reorder

Behavior:
- A stack shows one member at a time with a 4 DIP dot per member at the bottom (the shown one opaque). It rotates every `rotate` with a `DispatcherTimer`, pauses while hovered, the wheel moves to the next (down) or previous (up) member, an urgent member takes over (the slot listens to each member's `StateChanged`), and a left click releases the takeover after the member handled its own click (popup or `"click"`).
- Compact tiles are 40 DIP: padding 5 + 28 DIP icon + 5 + 2 DIP border. Without an icon or image they show `TileText.Monogram(title)` on the neutral color. The tooltip becomes `TileText.CompactTooltip(state)`.
- Inline tile buttons go through `TileCommands.InvokeAsync`, so `host:` ids behave the same as in popups (deferred from milestone 2).
- The right-click menu of a slot is filled when it opens, for the member shown at that moment.

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/TileTextTests.cs`:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileTextTests
{
    [Theory]
    [InlineData("uptime", "U")]
    [InlineData("  #1 build", "1")]
    [InlineData("élan", "É")]
    [InlineData("…", "•")]
    [InlineData("", "•")]
    [InlineData(null, "•")]
    public void Monogram_is_the_first_letter_or_digit(string? title, string expected) =>
        Assert.Equal(expected, TileText.Monogram(title));

    [Fact]
    public void Compact_tooltip_joins_title_subtitle_and_tooltip()
    {
        var state = new TileState { Title = "12:30", Subtitle = "up 2h", Tooltip = "Booted 08:00" };

        Assert.Equal($"12:30{Environment.NewLine}up 2h{Environment.NewLine}Booted 08:00", TileText.CompactTooltip(state));
    }

    [Fact]
    public void Compact_tooltip_skips_empty_and_repeated_lines()
    {
        var state = new TileState { Title = "Error", Subtitle = "exit 3", Tooltip = "exit 3" };

        Assert.Equal($"Error{Environment.NewLine}exit 3", TileText.CompactTooltip(state));
        Assert.Null(TileText.CompactTooltip(new TileState { Title = " " }));
    }
}
```

**Create** `tests/TaskbarTiles.Tests/SlotViewTests.cs`:
```csharp
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class SlotViewTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    static TileRuntime Runtime(string id, double width = 132) =>
        new(id, "fake", width, () => new FakeConnector(), NoSettings, new ImmediateDispatcher(), new CollectingLog());

    static TileCommands Commands() => new(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());

    [Fact]
    public void A_stack_shows_one_member_with_dots_and_takes_its_widest_width() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a", 132), Runtime("b", 268)], false, TimeSpan.FromSeconds(5));

        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        Assert.Equal(268, view.Width);
        Assert.Equal(0, view.CurrentMember);
        Assert.Same(slot.Members[0], view.Current.Runtime);
        Assert.Equal(Visibility.Visible, view.Current.Visibility);
        view.Detach();
    });

    [Fact]
    public void Wheel_down_shows_the_next_member() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a"), Runtime("b")], false, TimeSpan.FromSeconds(5));
        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.MouseWheelEvent });

        Assert.Equal(1, view.CurrentMember);
        Assert.Same(slot.Members[1], view.Current.Runtime);
        view.Detach();
    });

    [Fact]
    public void Compact_slots_are_40_dip_wide() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a", 268)], true, null), () => false, Commands(), new CollectingLog());

        view.Compact = true;

        Assert.Equal(LayoutEngine.CompactWidth, view.Width);
        Assert.True(view.Current.Compact);
        Assert.Null(view.Rotation);
        view.Detach();
    });
}

/// <summary>Runs WPF code on a fresh STA thread and rethrows what it threw.</summary>
public static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileTextTests|FullyQualifiedName~SlotViewTests"`
Expected: build error CS0103/CS0246 (`TileText`, `SlotView` do not exist).

- [ ] **Step 3: Implement**

**Create** `src/TaskbarTiles.Host/UI/TileText.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.UI;

/// <summary>Text a compact (icon-only) tile shows instead of its title.</summary>
public static class TileText
{
    /// <summary>The first letter or digit of the title in upper case; "•" when there is none. Shown when a compact tile has no icon.</summary>
    public static string Monogram(string? title)
    {
        foreach (var c in title ?? "")
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "•";
    }

    /// <summary>Title, subtitle and tooltip, one per line, skipping empty and repeated lines; null when all are empty.</summary>
    public static string? CompactTooltip(TileState state)
    {
        var lines = new[] { state.Title, state.Subtitle, state.Tooltip }
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}
```

Name the two panels the compact look hides.

**Edit** `src/TaskbarTiles.Host/UI/TileView.xaml`, find:
```xml
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,4,0">
```
Replace with:
```xml
            <StackPanel x:Name="TextPanel" Grid.Column="1" VerticalAlignment="Center" Margin="0,0,4,0">
```

**Edit** `src/TaskbarTiles.Host/UI/TileView.xaml`, find:
```xml
            <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
```
Replace with:
```xml
            <StackPanel x:Name="ExtrasPanel" Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
```

**Replace** `src/TaskbarTiles.Host/UI/TileView.xaml.cs` (whole file):
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Renders one tile's TileState, full or compact (icon only), and turns clicks into flyouts or actions.</summary>
public partial class TileView : UserControl
{
    static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily MonogramFont = new("Segoe UI");

    readonly Func<bool> _isLightTheme;
    readonly TileCommands _commands;
    readonly ITileLog _log;
    bool _urgent;
    bool _compact;
    bool _clickInFlight;
    SolidColorBrush? _pulse;

    public TileView(TileRuntime runtime, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        InitializeComponent();
        Runtime = runtime;
        _isLightTheme = isLightTheme;
        _commands = commands;
        _log = log;
        Width = runtime.Width;
        MouseLeftButtonUp += OnClick;
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        KeyDown += (_, e) => { if (e.Key == Key.Enter) OnClick(this, null); };
        Render(runtime.State);
        // Subscribe last: a constructor that throws must not leave a subscription behind.
        Runtime.StateChanged += OnStateChanged;
    }

    public TileRuntime Runtime { get; }

    /// <summary>Icon only, with title, subtitle and tooltip in the tooltip. The owner sets the width.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            Render(Runtime.State);
        }
    }

    /// <summary>What the popup is placed above; null means this tile. Tiles in the overflow popup point it at the » tile.</summary>
    public FrameworkElement? FlyoutAnchor { get; set; }

    public void Detach()
    {
        Runtime.StateChanged -= OnStateChanged;
        StopPulse();
    }

    void OnStateChanged(object? sender, EventArgs e) => Render(Runtime.State);

    void Render(TileState s)
    {
        TitleText.Text = s.Title ?? "";
        SubtitleText.Text = s.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(s.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = _compact ? TileText.CompactTooltip(s) : string.IsNullOrEmpty(s.Tooltip) ? null : s.Tooltip;

        var picture = Theme.Decode(s.Image);
        Picture.Source = picture;
        if (picture is null && s.Icon is { } icon)
        {
            IconGlyph.FontFamily = GlyphFont;
            IconGlyph.Text = Theme.Glyph(icon.Glyph);
            IconBackground.Background = Theme.ColorFor(icon.Color);
        }
        else if (picture is null && _compact)
        {
            // A compact tile without an icon still needs something to recognise it by.
            IconGlyph.FontFamily = MonogramFont;
            IconGlyph.Text = TileText.Monogram(s.Title);
            IconBackground.Background = Theme.ColorFor(TileColor.Neutral);
        }
        else
        {
            IconGlyph.Text = "";
            IconBackground.Background = Brushes.Transparent;
        }
        IconHost.Visibility = picture is null && s.Icon is null && !_compact ? Visibility.Collapsed : Visibility.Visible;
        IconHost.Margin = _compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        Root.Padding = _compact ? new Thickness(5, 0, 5, 0) : new Thickness(6, 0, 6, 0); // 5 + 28 + 5 + 2 border = 40 DIP
        TextPanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        ExtrasPanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;

        ButtonPanel.Children.Clear();
        foreach (var b in s.Buttons)
        {
            var button = new Button
            {
                Content = Theme.Glyph(b.Glyph),
                ToolTip = b.Tooltip,
                IsEnabled = b.Enabled,
                Style = (Style)FindResource("IconButton"),
            };
            var id = b.Id;
            // Same path as popup actions: host ids become commands, the rest go to the connector,
            // which drops a click on an action id that is still running.
            button.Click += async (_, _) =>
            {
                try { await _commands.InvokeAsync(Runtime, id); }
                catch (Exception ex) { _log.Error($"Tile '{Runtime.TileId}' button '{id}' failed", ex); }
            };
            ButtonPanel.Children.Add(button);
        }

        RenderSparkline(s.Sparkline);

        if (s.Progress is { } p)
        {
            var done = Math.Clamp(p, 0, 1);
            ProgressDone.Width = new GridLength(done, GridUnitType.Star);
            ProgressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
            ProgressTrack.Visibility = Visibility.Visible;
        }
        else
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
        }

        var wasUrgent = _urgent;
        _urgent = s.Urgent;
        if (_urgent && !wasUrgent) StartPulse();
        else if (!_urgent && wasUrgent) StopPulse();
        UpdateBackground();
    }

    void RenderSparkline(IReadOnlyList<double>? values)
    {
        if (values is not { Count: > 1 })
        {
            SparkHost.Visibility = Visibility.Collapsed;
            return;
        }

        double min = values.Min(), max = values.Max(), range = max - min;
        double w = SparkHost.Width, h = SparkHost.Height;
        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var y = range <= 0 ? h / 2 : h - 2 - (values[i] - min) / range * (h - 4);
            points.Add(new Point(i * w / (values.Count - 1), y));
        }
        Spark.Points = points;
        SparkHost.Visibility = Visibility.Visible;
    }

    void StartPulse()
    {
        var bad = ((SolidColorBrush)Theme.ColorFor(TileColor.Bad)).Color;
        _pulse ??= new SolidColorBrush(Color.FromArgb(0x10, bad.R, bad.G, bad.B));
        Root.Background = _pulse;
        Root.BorderBrush = Theme.ColorFor(TileColor.Bad);
        if (SystemParameters.ClientAreaAnimation)
            _pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x30, bad.R, bad.G, bad.B),
                TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    void StopPulse() => _pulse?.BeginAnimation(SolidColorBrush.ColorProperty, null);

    void UpdateBackground()
    {
        if (_urgent) return; // pulse owns the background while urgent
        Root.SetResourceReference(Border.BackgroundProperty, IsMouseOver ? "TileHover" : "TileFill");
        Root.SetResourceReference(Border.BorderBrushProperty, "TileOutline");
    }

    async void OnClick(object sender, MouseButtonEventArgs? e)
    {
        if (e?.Handled == true || _clickInFlight) return;
        _clickInFlight = true;
        try
        {
            var result = await Runtime.GetFlyoutAsync();
            switch (result.Outcome)
            {
                case FlyoutOutcome.NoPopup:
                    await Runtime.InvokeActionAsync(TileActions.Click);
                    break;
                case FlyoutOutcome.Popup:
                    FlyoutWindow.ShowAbove(FlyoutAnchor ?? this, result.Flyout!, Runtime, _isLightTheme(), _commands, _log);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Tile '{Runtime.TileId}' click failed", ex);
        }
        finally
        {
            _clickInFlight = false;
        }
    }
}
```

**Replace** `src/TaskbarTiles.Host/UI/FlyoutWindow.cs` (whole file). `ShowAbove` keeps its signature and now goes through `ShowPanel`; `Build` returns the content and `Frame` adds the popup border:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// The popup above a tile, the overflow popup and the tile gallery. Only one is open at a time; it closes when it
/// loses focus.
/// </summary>
public static class FlyoutWindow
{
    const double WidthDip = 320;
    static Window? _open;

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log) =>
        ShowPanel(anchor, Build(state, runtime, commands, log), light, log, WidthDip);

    /// <summary>
    /// Shows any content in the popup frame above <paramref name="anchor"/>: 320 DIP wide by default, or as wide as the
    /// content with a null width. <paramref name="onClosed"/> runs when it closes, for whatever reason.
    /// </summary>
    public static void ShowPanel(FrameworkElement anchor, UIElement content, bool light, ITileLog log,
        double? width = WidthDip, Action? onClosed = null)
    {
        CloseOpen();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = width is null ? SizeToContent.WidthAndHeight : SizeToContent.Height,
            UseLayoutRounding = true,
            Opacity = 0, // hidden until positioned on the target monitor at the right DPI
        };
        if (width is { } w) window.Width = w;
        Theme.ApplyTo(window.Resources, light);
        window.Content = Frame(content);
        window.Deactivated += (_, _) => { if (_open == window) CloseOpen(); };
        if (onClosed is not null)
            window.Closed += (_, _) =>
            {
                try { onClosed(); }
                catch (Exception ex) { log.Error("Closing a popup failed", ex); }
            };
        window.Loaded += (_, _) =>
        {
            try { Position(window, anchor); }
            catch (Exception ex) { log.Error("Flyout positioning failed", ex); }
            finally { window.Opacity = 1; }
        };
        _open = window;
        window.Show();
        window.Activate();
    }

    static UIElement Build(FlyoutState state, TileRuntime runtime, TileCommands commands, ITileLog log)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (Theme.Decode(state.Image, 144) is { } img)
            header.Children.Add(new Border
            {
                Width = 72, Height = 72, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 12, 0),
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(state.Title, 14, FontWeights.SemiBold, "TileForeground"));
        if (!string.IsNullOrEmpty(state.Subtitle)) titles.Children.Add(Text(state.Subtitle, 12, FontWeights.Normal, "TileDim"));
        header.Children.Add(titles);
        panel.Children.Add(header);

        foreach (var row in state.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (row.Icon is { } icon)
                grid.Children.Add(new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = Theme.ColorFor(icon.Color), VerticalAlignment = VerticalAlignment.Center,
                });
            var text = Text(row.Text, 13, FontWeights.Normal, "TileForeground");
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (row.Value is not null)
            {
                var value = Text(row.Value, 11.5, FontWeights.Normal, "TileDim");
                value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                value.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(value, 2);
                grid.Children.Add(value);
            }
            panel.Children.Add(grid);
        }

        if (state.Actions.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var a in state.Actions)
            {
                var button = new Button
                {
                    Content = a.Label,
                    Style = (Style)Application.Current.FindResource(a.Primary ? "FlyoutPrimaryButton" : "FlyoutButton"),
                };
                var id = a.Id;
                // Host ids (Restart tile, Open log) become commands; the rest go to the connector.
                button.Click += async (_, _) =>
                {
                    try { CloseOpen(); await commands.InvokeAsync(runtime, id); }
                    catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
                };
                actions.Children.Add(button);
            }
            panel.Children.Add(actions);
        }

        return panel;
    }

    static Border Frame(UIElement content)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = content,
        };
        border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return border;
    }

    /// <summary>A text line in the popup's colors: brushKey is a Theme key such as "TileForeground" or "TileDim".</summary>
    public static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>
    /// Centers the window above the anchor in physical pixels, 8 DIP above the taskbar, clamped to the work area
    /// of the monitor holding the anchor. The window starts at Opacity 0; moving it onto the target monitor may
    /// change its DPI (WPF rescales synchronously), so we re-measure and re-apply until the DPI is stable (max 3 passes).
    /// </summary>
    static void Position(Window window, FrameworkElement anchor)
    {
        var anchorSource = PresentationSource.FromVisual(anchor) as HwndSource;
        if (anchorSource?.CompositionTarget is null) return;
        var anchorScale = anchorSource.CompositionTarget.TransformToDevice.M11;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var anchorWidthPx = anchor.ActualWidth * anchorScale;

        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(anchorSource.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var haveWork = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info);

        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            int x = (int)(topLeft.X + anchorWidthPx / 2 - w / 2.0);
            int y = (int)(topLeft.Y - 4 * anchorScale - 8 * anchorScale - h); // anchor sits 4 DIP below the taskbar top
            if (haveWork)
            {
                x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - w));
                y = Math.Max(y, info.rcWork.Top);
            }
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);

            var after = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(after - dpi) < 0.001) break;
            dpi = after;
        }
    }
}
```

**Create** `src/TaskbarTiles.Host/UI/SlotView.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One slot on a bar: a single tile, or a stack that shows one member at a time with position dots. A stack rotates
/// every "rotate", pauses while hovered, cycles on the mouse wheel, and lets an urgent member take over until it is clicked.
/// </summary>
public sealed class SlotView : Grid
{
    readonly List<TileView> _members = [];
    readonly StackPanel? _dots;
    readonly DispatcherTimer? _timer;
    bool _compact;

    public SlotView(BarSlot slot, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        Slot = slot;
        foreach (var runtime in slot.Members)
        {
            var view = new TileView(runtime, isLightTheme, commands, log);
            _members.Add(view);
            Children.Add(view);
        }

        if (slot.IsStack)
        {
            Rotation = new StackRotation(slot.Members.Count);
            _dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false,
            };
            for (var i = 0; i < slot.Members.Count; i++)
            {
                var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1.5, 0, 1.5, 0) };
                dot.SetResourceReference(Shape.FillProperty, "TileForeground");
                _dots.Children.Add(dot);
            }
            Children.Add(_dots);

            for (var i = 0; i < slot.Members.Count; i++) Rotation.SetUrgent(i, slot.Members[i].State.Urgent);
            foreach (var runtime in slot.Members) runtime.StateChanged += OnMemberStateChanged;

            MouseEnter += (_, _) => Rotation.Hovered = true;
            MouseLeave += (_, _) => Rotation.Hovered = false;
            MouseWheel += OnWheel;
            // After the member's own click handling: the click that opens the urgent member's popup also releases the stack.
            AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Changed(Rotation.Acknowledge())), handledEventsToo: true);

            if (slot.Members.Count > 1)
            {
                _timer = new DispatcherTimer { Interval = slot.Rotate!.Value };
                _timer.Tick += (_, _) => Changed(Rotation.Tick());
                _timer.Start();
            }
        }

        ApplyWidths();
        ShowCurrent();
    }

    public BarSlot Slot { get; private set; }

    /// <summary>Null for a single tile.</summary>
    public StackRotation? Rotation { get; }

    /// <summary>Index of the member shown (always 0 for a single tile).</summary>
    public int CurrentMember => Rotation?.Index ?? 0;

    public TileView Current => _members[CurrentMember];

    public double FullWidth => Slot.FullWidth;

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            ApplyWidths();
        }
    }

    /// <summary>Where member popups appear; null places them above the tile itself.</summary>
    public FrameworkElement? FlyoutAnchor
    {
        set { foreach (var m in _members) m.FlyoutAnchor = value; }
    }

    /// <summary>Takes a slot with the same content at a new index (after a reorder), keeping rotation state.</summary>
    public void Retarget(BarSlot slot)
    {
        if (!slot.SameContent(Slot)) throw new ArgumentException("Retarget needs a slot with the same content.", nameof(slot));
        Slot = slot;
    }

    public void Detach()
    {
        _timer?.Stop();
        foreach (var runtime in Slot.Members) runtime.StateChanged -= OnMemberStateChanged;
        foreach (var m in _members) m.Detach();
    }

    void OnMemberStateChanged(object? sender, EventArgs e)
    {
        if (Rotation is null || sender is not TileRuntime runtime) return;
        for (var i = 0; i < Slot.Members.Count; i++)
            if (ReferenceEquals(Slot.Members[i], runtime)) Changed(Rotation.SetUrgent(i, runtime.State.Urgent));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Rotation is null) return;
        Changed(Rotation.Step(e.Delta < 0 ? +1 : -1)); // wheel down shows the next member
        e.Handled = true;
    }

    void Changed(bool indexChanged)
    {
        if (indexChanged) ShowCurrent();
    }

    void ApplyWidths()
    {
        var width = _compact ? LayoutEngine.CompactWidth : FullWidth;
        Width = width;
        foreach (var m in _members)
        {
            m.Width = width;
            m.Compact = _compact;
        }
    }

    void ShowCurrent()
    {
        var current = CurrentMember;
        for (var i = 0; i < _members.Count; i++)
            _members[i].Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
        if (_dots is null) return;
        for (var i = 0; i < _dots.Children.Count; i++)
            ((UIElement)_dots.Children[i]).Opacity = i == current ? 1 : 0.3;
    }
}
```

**Replace** `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file):
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>One per display: a borderless topmost strip sized to its visible slots, placed in the free zone.</summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    readonly List<SlotView> _views = [];
    readonly ITileLog _log;
    readonly TileCommands _commands;
    IReadOnlyList<BarSlot> _slots = [];
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId, ITileLog log, TileCommands commands)
    {
        _log = log;
        _commands = commands;
        DisplayId = displayId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        // Bar-wide menu for the gaps between tiles; each slot gets its own menu in SetSlots.
        ContextMenu = new ContextMenu();
        Fill(ContextMenu, null);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the slots' menus.
        ContextMenuOpening += (_, _) => { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public string DisplayId { get; }
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Shows the slots. Views whose slot content is unchanged are kept (with their rotation state), even when they moved.
    /// </summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots)
    {
        if (_slots.SequenceEqual(slots)) return;
        _slots = slots.ToList();

        var pool = _views.ToList();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var slot in slots)
        {
            var view = pool.FirstOrDefault(v => v.Slot.SameContent(slot));
            if (view is not null)
            {
                pool.Remove(view);
                view.Retarget(slot);
            }
            else
            {
                try { view = CreateView(slot); }
                catch (Exception ex)
                {
                    _log.Error($"Slot {slot.Index} ({string.Join(", ", slot.Members.Select(m => m.TileId))}) could not be shown", ex);
                    continue;
                }
            }
            _views.Add(view);
            _panel.Children.Add(view);
        }
        foreach (var unused in pool) unused.Detach();
        Reflow();
    }

    public void Apply(TaskbarInfo info)
    {
        _info = info;
        if (_light != info.IsLightTheme)
        {
            _light = info.IsLightTheme;
            Theme.ApplyTo(Resources, info.IsLightTheme);
        }
        Reflow();
    }

    SlotView CreateView(BarSlot slot)
    {
        var view = new SlotView(slot, () => _light ?? false, _commands, _log) { ContextMenu = new ContextMenu() };
        // Built when it opens, for the member shown at that moment.
        view.ContextMenuOpening += (_, _) => Fill(view.ContextMenu, view.Current.Runtime);
        return view;
    }

    void Fill(ContextMenu menu, TileRuntime? tile)
    {
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(onTile: tile is not null))
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Header };
            if (entry.Tile is { } command && tile is not null)
                item.Click += async (_, _) => await _commands.ExecuteAsync(tile, command); // never throws
            else if (entry.Bar == BarCommand.OpenConfig)
                item.Click += (_, _) => _commands.OpenConfig();
            else if (entry.Bar == BarCommand.Exit)
                item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(item);
        }
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var widths = _views.Select(v => v.FullWidth).ToList();
        var count = _info.Hidden || zone.IsEmpty ? 0 : FitTiles.Count(widths, zone.Width / scale, GapDip);
        for (var i = 0; i < _views.Count; i++)
        {
            _views[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            _views[i].Margin = new Thickness(0, 0, i < count - 1 ? GapDip : 0, 0);
        }

        if (count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        var totalDip = widths.Take(count).Sum() + GapDip * (count - 1);
        var w = (int)Math.Ceiling(totalDip * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileTextTests|FullyQualifiedName~SlotViewTests"`
Expected: PASS (11 tests).

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 405`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/UI/TileText.cs src/TaskbarTiles.Host/UI/SlotView.cs src/TaskbarTiles.Host/UI/TileView.xaml src/TaskbarTiles.Host/UI/TileView.xaml.cs src/TaskbarTiles.Host/UI/FlyoutWindow.cs src/TaskbarTiles.Host/UI/TileBar.cs tests/TaskbarTiles.Tests/TileTextTests.cs tests/TaskbarTiles.Tests/SlotViewTests.cs
git commit -m "feat(ui): stacks on the bar, compact tile look, inline buttons via TileCommands" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Reflow with LayoutEngine, overflow popup, layout commands in the menu

**Files:**
- Modify: `src/TaskbarTiles.Host/UI/TileCommands.cs` (whole file), `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file), `src/TaskbarTiles.Host/UI/ConfigNotice.cs`, `src/TaskbarTiles.Host/UI/Styles.xaml`, `src/TaskbarTiles.Host/App.xaml.cs`
- Delete: `src/TaskbarTiles.Host/UI/FitTiles.cs`, `tests/TaskbarTiles.Tests/FitTilesTests.cs`
- Test: `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, `tests/TaskbarTiles.Tests/ConfigNoticeTests.cs`

**Interfaces:**
- Consumes: `LayoutEngine`, `LayoutItem`, `LayoutResult`, `SlotMode` (Task 4); `LayoutEdits.Drop/AddToNearestStack/RemoveFromStack/Remove`, `DropPosition` (Task 2); `ILayoutEditor.EditLayout` (Task 6); `SlotView`, `FlyoutWindow.ShowPanel` (Task 8); `NotificationWindow.Show(string title, string body, bool light, Action onClick, ITileLog log)` (existing)
- Produces:
  - `enum LayoutCommand { MoveLeft, MoveRight, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }`
  - `sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null, bool Enabled = true)`
  - `sealed record SlotMenuState(bool IsFirst, bool IsLast, bool IsStack, bool RotationPaused, bool CanStack)`
  - `TileMenu.Entries(SlotMenuState? slot)` (null = the bar's gaps; Task 10 adds `bool editing`)
  - `ConfigNotice.SaveFailedTitle` (`"Layout not saved"`), `ConfigNotice.SaveFailedBody(ConfigError error)`
  - `TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)`
  - `Styles.xaml` key `BarButton` (tile-shaped button for `»`, and for `+`/Done in Task 10)

Behavior:
- `Reflow` runs `LayoutEngine.Compute` over the slot views (full width, pin) with the free width in DIP; full and compact slots go on the bar in order, then the `»` tile if anything overflowed. The window is as wide as `LayoutResult.Width` and hidden when it is 0 (or the taskbar is hidden).
- `»` toggles a popup (`FlyoutWindow.ShowPanel`, sized to content) listing the overflowed slots at full size and taskbar height. They keep working: buttons, right-click menus, and their own popups, which open above the `»` tile and replace the overflow popup. When it closes (any reason) the views are handed back; any reflow that changes the overflowed set or the slots closes it first.
- Tile menu: Configure connector…, Refresh now, Show connector log, Restart tile, ─, Move left, Move right, then Add to stack (single tile) or Remove from stack + Pause/Resume rotation (stack), ─, Remove tile, ─, Show config file, ─, Exit Taskbar Tiles. Move left/right are drops before/after the neighboring bar slot and are disabled at the ends; Add to stack is disabled for the only slot. Remove from stack and Remove tile on a stack act on the member shown when the menu opened. Pause/Resume rotation only change the in-memory `StackRotation.Paused`.
- A change that cannot be saved shows a notice ("Layout not saved", the reason, "config.json is unchanged."); clicking it shows `config.json` in Explorer.

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, find:
```csharp
    [Fact]
    public void Tile_menu_lists_tile_commands_then_bar_commands() =>
        Assert.Equal(
            new[] { "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "", "Show config file", "", "Exit Taskbar Tiles" },
            TileMenu.Entries(onTile: true).Select(e => e.Header));

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(onTile: false);

        Assert.Equal(new[] { "Show config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.OpenConfig, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.Exit, entries[2].Bar);
    }
```
Replace with:
```csharp
    static readonly SlotMenuState MiddleTile = new(IsFirst: false, IsLast: false, IsStack: false, RotationPaused: false, CanStack: true);

    [Fact]
    public void Tile_menu_lists_tile_commands_layout_commands_then_bar_commands() =>
        Assert.Equal(
            new[]
            {
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
                "Move left", "Move right", "Add to stack", "", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile).Select(e => e.Header));

    [Fact]
    public void Stack_menu_offers_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true });
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true });

        Assert.Equal(new[] { "Remove from stack", "Pause rotation" }, running.Skip(7).Take(2).Select(e => e.Header));
        Assert.Equal(LayoutCommand.PauseRotation, running[8].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[8].Layout);
        Assert.DoesNotContain(running, e => e.Layout == LayoutCommand.AddToStack);
    }

    [Fact]
    public void Moves_and_add_to_stack_are_disabled_where_they_cannot_apply()
    {
        var only = TileMenu.Entries(new SlotMenuState(IsFirst: true, IsLast: true, IsStack: false, RotationPaused: false, CanStack: false));

        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveLeft).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveRight).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.AddToStack).Enabled);
        Assert.True(only.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(null);

        Assert.Equal(new[] { "Show config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.OpenConfig, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.Exit, entries[2].Bar);
    }
```

**Edit** `tests/TaskbarTiles.Tests/ConfigNoticeTests.cs`, find:
```csharp
        Assert.DoesNotContain("..", body);
        Assert.StartsWith("Tile 'a' needs a \"connector\" name. ", body);
    }
```
Replace with:
```csharp
        Assert.DoesNotContain("..", body);
        Assert.StartsWith("Tile 'a' needs a \"connector\" name. ", body);
    }

    [Fact]
    public void Save_failure_says_the_file_is_unchanged()
    {
        var body = ConfigNotice.SaveFailedBody(new ConfigError("config.json has an error, so nothing was saved: Invalid JSON.", 4, 1));

        Assert.StartsWith("config.json has an error, so nothing was saved: Invalid JSON. (line 4, column 1). config.json is unchanged.", body);
        Assert.EndsWith("Click to show config.json in Explorer.", body);
    }
```

**Delete** `tests/TaskbarTiles.Tests/FitTilesTests.cs`

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~ConfigNoticeTests"`
Expected: build errors CS0246 (`SlotMenuState` not found) and CS0117 (`ConfigNotice` has no `SaveFailedBody`).

- [ ] **Step 3: Implement**

**Replace** `src/TaskbarTiles.Host/UI/TileCommands.cs` (whole file; `TileCommands` itself is unchanged):
```csharp
using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Commands on one tile, from its context menu or its error popup.</summary>
public enum TileCommand { Configure, Refresh, ShowLog, Restart }

/// <summary>Commands on the whole app.</summary>
public enum BarCommand { OpenConfig, Exit }

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

public static class TileMenu
{
    /// <summary>The right-click menu of a slot, or of the bar's gaps when <paramref name="slot"/> is null.</summary>
    public static IReadOnlyList<MenuEntry> Entries(SlotMenuState? slot)
    {
        var entries = new List<MenuEntry>();
        if (slot is not null)
        {
            entries.Add(new("Configure connector…", TileCommand.Configure));
            entries.Add(new("Refresh now", TileCommand.Refresh));
            entries.Add(new("Show connector log", TileCommand.ShowLog));
            entries.Add(new("Restart tile", TileCommand.Restart));
            entries.Add(MenuEntry.Separator);
            entries.Add(new("Move left", Layout: LayoutCommand.MoveLeft, Enabled: !slot.IsFirst));
            entries.Add(new("Move right", Layout: LayoutCommand.MoveRight, Enabled: !slot.IsLast));
            if (slot.IsStack)
            {
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
                entries.Add(slot.RotationPaused
                    ? new("Resume rotation", Layout: LayoutCommand.ResumeRotation)
                    : new("Pause rotation", Layout: LayoutCommand.PauseRotation));
            }
            else
            {
                entries.Add(new("Add to stack", Layout: LayoutCommand.AddToStack, Enabled: slot.CanStack));
            }
            entries.Add(MenuEntry.Separator);
            entries.Add(new("Remove tile", Layout: LayoutCommand.RemoveTile));
            entries.Add(MenuEntry.Separator);
        }
        entries.Add(new("Show config file", Bar: BarCommand.OpenConfig));
        entries.Add(MenuEntry.Separator);
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }
}

/// <summary>Executes tile commands. Called on the UI thread; never throws.</summary>
public sealed class TileCommands(string configFile, string logsDir, Action<string> openFile, ITileLog log)
{
    /// <summary>Same file FileLog.For(logsDir, $"tile-{id}") writes.</summary>
    public string LogPathFor(string tileId) => Path.Combine(logsDir, FileLog.Sanitize($"tile-{tileId}") + ".log");

    public static bool TryFromActionId(string actionId, out TileCommand command)
    {
        switch (actionId)
        {
            case TileRuntime.RestartActionId: command = TileCommand.Restart; return true;
            case TileRuntime.ShowLogActionId: command = TileCommand.ShowLog; return true;
            default: command = default; return false;
        }
    }

    /// <summary>Popup and button actions: the host's own action ids become commands, everything else goes to the connector.</summary>
    public Task InvokeAsync(TileRuntime runtime, string actionId) =>
        TryFromActionId(actionId, out var command) ? ExecuteAsync(runtime, command) : runtime.InvokeActionAsync(actionId);

    public async Task ExecuteAsync(TileRuntime runtime, TileCommand command)
    {
        try
        {
            switch (command)
            {
                case TileCommand.Configure:
                    Open(configFile);
                    break;
                case TileCommand.ShowLog:
                    Open(EnsureExists(LogPathFor(runtime.TileId)));
                    break;
                case TileCommand.Restart:
                    await runtime.RestartAsync();
                    break;
                case TileCommand.Refresh:
                    // A crashed connector cannot refresh; a restart is what the user wants.
                    if (runtime.LastError is { Fatal: true }) await runtime.RestartAsync();
                    else await runtime.InvokeActionAsync(TileActions.Refresh);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"Command {command} on tile '{runtime.TileId}' failed", ex);
        }
    }

    public void OpenConfig() => Open(configFile);

    void Open(string path)
    {
        try { openFile(path); }
        catch (Exception ex) { log.Error($"Could not open {path}", ex); }
    }

    static string EnsureExists(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }
}
```

**Edit** `src/TaskbarTiles.Host/UI/ConfigNotice.cs`, find:
```csharp
        return $"{what}. {consequence} Click to show config.json in Explorer.";
    }
```
Replace with:
```csharp
        return $"{what}. {consequence} Click to show config.json in Explorer.";
    }

    public const string SaveFailedTitle = "Layout not saved";

    /// <summary>Why an edit-mode or menu change was not written; config.json is left as it was.</summary>
    public static string SaveFailedBody(ConfigError error) =>
        $"{error.ToString().TrimEnd().TrimEnd('.')}. config.json is unchanged. Click to show config.json in Explorer.";
```

**Edit** `src/TaskbarTiles.Host/UI/Styles.xaml`, find:
```xml
    <!-- Flyout action buttons -->
```
Replace with:
```xml
    <!-- Tile-shaped bar buttons: the » overflow tile, and + and Done in edit mode -->
    <Style x:Key="BarButton" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource TileForeground}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{DynamicResource TileFill}" BorderBrush="{DynamicResource TileOutline}"
                            BorderThickness="1" CornerRadius="6">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource TileHover}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Flyout action buttons -->
```

**Replace** `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file):
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One per display: a borderless topmost strip in the free zone. LayoutEngine decides per slot: full, compact, or
/// inside the » overflow popup. Reflows on every free-zone or slot change.
/// </summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;
    const double InsetDip = 4; // tiles sit 4 DIP inside the taskbar's top and bottom edges

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, InsetDip, 0, InsetDip) };
    readonly List<SlotView> _views = [];
    readonly Button _overflowButton;
    readonly ITileLog _log;
    readonly TileCommands _commands;
    readonly ILayoutEditor _editor;
    IReadOnlyList<BarSlot> _slots = [];
    List<SlotView> _overflowed = [];
    StackPanel? _overflowPanel; // set while the overflow popup is open
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
        DisplayId = displayId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        _overflowButton = new Button
        {
            Content = "»",
            Width = LayoutEngine.OverflowWidth,
            ToolTip = "More tiles",
            Style = TryFindResource("BarButton") as Style,
        };
        _overflowButton.Click += (_, _) => ToggleOverflow();

        // Bar-wide menu for the gaps between tiles; each slot gets its own menu in CreateView.
        ContextMenu = new ContextMenu();
        FillBarMenu(ContextMenu);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the slots' menus.
        ContextMenuOpening += (_, _) => { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public string DisplayId { get; }
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Shows the slots. Views whose slot content is unchanged are kept (with their rotation state), even when they moved.
    /// </summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots)
    {
        if (_slots.SequenceEqual(slots)) return;
        _slots = slots.ToList();
        CloseOverflow(); // the popup may hold views that move or go away

        var pool = _views.ToList();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var slot in slots)
        {
            var view = pool.FirstOrDefault(v => v.Slot.SameContent(slot));
            if (view is not null)
            {
                pool.Remove(view);
                view.Retarget(slot);
            }
            else
            {
                try { view = CreateView(slot); }
                catch (Exception ex)
                {
                    _log.Error($"Slot {slot.Index} ({string.Join(", ", slot.Members.Select(m => m.TileId))}) could not be shown", ex);
                    continue;
                }
            }
            _views.Add(view);
        }
        foreach (var unused in pool) unused.Detach();
        Reflow();
    }

    public void Apply(TaskbarInfo info)
    {
        _info = info;
        if (_light != info.IsLightTheme)
        {
            _light = info.IsLightTheme;
            Theme.ApplyTo(Resources, info.IsLightTheme);
        }
        Reflow();
    }

    SlotView CreateView(BarSlot slot)
    {
        var view = new SlotView(slot, () => _light ?? false, _commands, _log) { ContextMenu = new ContextMenu() };
        // Built when it opens, for the member shown at that moment.
        view.ContextMenuOpening += (_, _) => FillSlotMenu(view.ContextMenu, view);
        return view;
    }

    void FillBarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(null)) menu.Items.Add(Item(entry, null, 0));
    }

    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        var position = _views.IndexOf(view);
        var state = new SlotMenuState(
            IsFirst: position <= 0,
            IsLast: position == _views.Count - 1,
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            CanStack: !view.Slot.IsStack && _views.Count > 1);
        var member = view.CurrentMember; // the member shown when the menu opened, even if the stack rotates meanwhile
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(state)) menu.Items.Add(Item(entry, view, member));
    }

    Control Item(MenuEntry entry, SlotView? view, int member)
    {
        if (entry.IsSeparator) return new Separator();
        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
        if (entry.Tile is { } command && view is not null)
        {
            var runtime = view.Slot.Members[member];
            item.Click += async (_, _) => await _commands.ExecuteAsync(runtime, command); // never throws
        }
        else if (entry.Layout is { } layout && view is not null)
            item.Click += (_, _) => Run(layout, view, member);
        else if (entry.Bar == BarCommand.OpenConfig)
            item.Click += (_, _) => _commands.OpenConfig();
        else if (entry.Bar == BarCommand.Exit)
            item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        return item;
    }

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
            case LayoutCommand.RemoveFromStack:
                Edit(l => LayoutEdits.RemoveFromStack(l, slot.Index, member));
                break;
            case LayoutCommand.RemoveTile:
                Edit(l => LayoutEdits.Remove(l, slot.Index, slot.IsStack ? member : null));
                break;
        }
    }

    /// <summary>Saves a layout change; the new slots arrive through SetSlots. Shows a notice when nothing was saved.</summary>
    void Edit(Func<DisplayLayout, DisplayLayout> edit)
    {
        ConfigError? error;
        try { error = _editor.EditLayout(DisplayId, edit); }
        catch (Exception ex)
        {
            _log.Error("Layout edit failed", ex);
            error = new ConfigError("The layout could not be changed: " + ex.Message);
        }
        if (error is null) return;

        _log.Warn($"Layout not saved: {error}");
        try { NotificationWindow.Show(ConfigNotice.SaveFailedTitle, ConfigNotice.SaveFailedBody(error), _light ?? false, _commands.OpenConfig, _log); }
        catch (Exception ex) { _log.Error("Could not show the save notice", ex); }
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var layout = _info.Hidden || zone.IsEmpty
            ? null
            : LayoutEngine.Compute(_views.Select(v => new LayoutItem(v.FullWidth, v.Slot.Pinned)).ToList(), zone.Width / scale, GapDip);
        Arrange(layout);

        if (layout is null || layout.Width <= 0)
        {
            CloseOverflow();
            if (IsVisible) Hide();
            return;
        }

        var w = (int)Math.Ceiling(layout.Width * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Puts full and compact slots on the bar, then the » tile when anything overflows.</summary>
    void Arrange(LayoutResult? layout)
    {
        var shown = new List<FrameworkElement>();
        var overflowed = new List<SlotView>();
        for (var i = 0; i < _views.Count; i++)
        {
            var mode = layout?.Modes[i] ?? SlotMode.Overflow;
            if (mode == SlotMode.Overflow)
            {
                overflowed.Add(_views[i]);
                continue;
            }
            _views[i].Compact = mode == SlotMode.Compact;
            shown.Add(_views[i]);
        }
        if (layout?.ShowOverflow == true) shown.Add(_overflowButton);

        if (!overflowed.SequenceEqual(_overflowed)) CloseOverflow();
        _overflowed = overflowed;

        if (!shown.SequenceEqual(_panel.Children.Cast<FrameworkElement>()))
        {
            _panel.Children.Clear();
            foreach (var element in shown) _panel.Children.Add(element);
        }
        for (var i = 0; i < shown.Count; i++)
            shown[i].Margin = new Thickness(0, 0, i < shown.Count - 1 ? GapDip : 0, 0);
    }

    /// <summary>The » tile: a popup listing the overflowed slots at full size, still clickable, with their menus.</summary>
    void ToggleOverflow()
    {
        if (_overflowPanel is not null)
        {
            CloseOverflow();
            return;
        }
        if (_overflowed.Count == 0 || _info is null) return;

        var tileHeight = Math.Max(24, _info.FreeZone.Zone.Height / _info.DpiScale - 2 * InsetDip);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (var i = 0; i < _overflowed.Count; i++)
        {
            var view = _overflowed[i];
            view.Compact = false;
            view.Height = tileHeight;
            view.FlyoutAnchor = _overflowButton; // a tile's own popup replaces this one, above the » tile
            view.Margin = new Thickness(0, 0, 0, i < _overflowed.Count - 1 ? GapDip : 0);
            panel.Children.Add(view);
        }
        _overflowPanel = panel;
        var moved = _overflowed.ToList();
        FlyoutWindow.ShowPanel(_overflowButton, panel, _light ?? false, _log, width: null, onClosed: () =>
        {
            panel.Children.Clear(); // hand the views back so the bar can show them again
            foreach (var view in moved)
            {
                view.Height = double.NaN;
                view.FlyoutAnchor = null;
            }
            if (_overflowPanel == panel) _overflowPanel = null;
        });
    }

    void CloseOverflow()
    {
        if (_overflowPanel is not null) FlyoutWindow.CloseOpen();
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseOverflow();
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
```

**Delete** `src/TaskbarTiles.Host/UI/FitTiles.cs`

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!);
```
Replace with:
```csharp
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!, _host!);
```
(Bars are created inside `_host.Start()`, after `_host` is assigned.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~ConfigNoticeTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 404` (the 4 `FitTilesTests` are gone).

- [ ] **Step 5: Commit**

```bash
git add -A src/TaskbarTiles.Host/UI src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/TileCommandsTests.cs tests/TaskbarTiles.Tests/ConfigNoticeTests.cs tests/TaskbarTiles.Tests/FitTilesTests.cs
git commit -m "feat(ui): reflow with LayoutEngine, overflow popup and layout commands in the menu" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Edit mode

**Files:**
- Modify: `src/TaskbarTiles.Host/HostController.cs` (whole file), `src/TaskbarTiles.Host/UI/TileCommands.cs` (whole file), `src/TaskbarTiles.Host/UI/SlotView.cs` (whole file), `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file), `src/TaskbarTiles.Host/UI/Styles.xaml`
- Test: `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, `tests/TaskbarTiles.Tests/SlotViewTests.cs` (whole file), `tests/TaskbarTiles.Tests/TestDoubles.cs`

**Interfaces:**
- Consumes: `LayoutEdits.DropPositionAt/Drop/Remove` (Task 2); `ILayoutEditor.EditLayout/GalleryFor/AddFromGallery` (Tasks 6, 7); `StackRotation.Suspended` (Task 5); `SlotView`, `FlyoutWindow.ShowPanel/Text`, `Sta.Run` (Task 8); `TileMenu`, `SlotMenuState`, `ConfigNotice.SaveFailed*`, `BarButton` style (Task 9)
- Produces:
  - `ITileBarView.SetEditing(bool editing)`; `FakeBar.Editing`
  - `ILayoutEditor.Editing` (`bool`), `ILayoutEditor.SetEditMode(bool editing)` — implemented by `HostController`; a bar created while edit mode is on starts in edit mode
  - `enum BarCommand { EditLayout, DoneEditing, OpenConfig, Exit }`; `TileMenu.Entries(SlotMenuState? slot, bool editing)`
  - `SlotView.Editing { get; set; }`, `SlotView.DropHighlight { get; set; }` (only while editing), `event EventHandler? SlotView.RemoveRequested`, `internal Button SlotView.RemoveButton`
  - `Styles.xaml` key `RemoveButton`

Behavior:
- **Edit layout** (tile menu or the bar's gaps) turns edit mode on for every bar; the clicked bar activates so Esc reaches it. While editing, bars drop `WS_EX_NOACTIVATE`; popups close; stacks stop rotating (the wheel still picks the member); each slot shows a dashed outline, a shield that keeps clicks from the tile, and a ×; `+` and **Done** follow the tiles (a display without tiles shows just these two). Menus show **Done editing** instead of **Edit layout**. **Done**, **Done editing** or Esc turns edit mode off everywhere.
- Drag (4 DIP threshold, mouse captured by the bar): the dragged slot fades to 35 %; over the middle 40 % of another slot that slot is highlighted (accent). Release on its left/right part reorders (before/after), on its middle stacks (`LayoutEdits.Drop`). Release elsewhere does nothing. Only slots on the bar can be dragged (not those inside the overflow popup; their menus still work).
- × removes the slot, or on a stack the member shown.
- `+` opens a popup: "Add a tile", then one button per `GalleryItem` in two columns (title and subtitle); a click adds it through `ILayoutEditor.AddFromGallery`.
- Every change saves immediately; a failure shows the "Layout not saved" notice.

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
                "Move left", "Move right", "Add to stack", "", "Remove tile", "",
                "Show config file", "", "Exit Taskbar Tiles",
            },
            TileMenu.Entries(MiddleTile).Select(e => e.Header));

    [Fact]
    public void Stack_menu_offers_remove_from_stack_and_pause_or_resume()
    {
        var running = TileMenu.Entries(MiddleTile with { IsStack = true });
        var paused = TileMenu.Entries(MiddleTile with { IsStack = true, RotationPaused = true });

        Assert.Equal(new[] { "Remove from stack", "Pause rotation" }, running.Skip(7).Take(2).Select(e => e.Header));
        Assert.Equal(LayoutCommand.PauseRotation, running[8].Layout);
        Assert.Equal(LayoutCommand.ResumeRotation, paused[8].Layout);
        Assert.DoesNotContain(running, e => e.Layout == LayoutCommand.AddToStack);
    }

    [Fact]
    public void Moves_and_add_to_stack_are_disabled_where_they_cannot_apply()
    {
        var only = TileMenu.Entries(new SlotMenuState(IsFirst: true, IsLast: true, IsStack: false, RotationPaused: false, CanStack: false));

        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveLeft).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.MoveRight).Enabled);
        Assert.False(only.Single(e => e.Layout == LayoutCommand.AddToStack).Enabled);
        Assert.True(only.Single(e => e.Layout == LayoutCommand.RemoveTile).Enabled);
    }

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(null);

        Assert.Equal(new[] { "Show config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.OpenConfig, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.Exit, entries[2].Bar);
    }
```
Replace with:
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

    [Fact]
    public void Bar_menu_has_only_bar_commands()
    {
        var entries = TileMenu.Entries(null, editing: false);

        Assert.Equal(new[] { "Edit layout", "", "Show config file", "", "Exit Taskbar Tiles" }, entries.Select(e => e.Header));
        Assert.Equal(BarCommand.EditLayout, entries[0].Bar);
        Assert.True(entries[1].IsSeparator);
        Assert.Equal(BarCommand.OpenConfig, entries[2].Bar);
        Assert.Equal(BarCommand.Exit, entries[4].Bar);
    }

    [Fact]
    public void While_editing_both_menus_offer_done_editing()
    {
        Assert.Equal(BarCommand.DoneEditing, TileMenu.Entries(null, editing: true)[0].Bar);
        var tile = TileMenu.Entries(MiddleTile, editing: true);
        Assert.Contains(tile, e => e.Header == "Done editing" && e.Bar == BarCommand.DoneEditing);
        Assert.DoesNotContain(tile, e => e.Header == "Edit layout");
    }
```

**Edit** `tests/TaskbarTiles.Tests/TestDoubles.cs`, find:
```csharp
    public bool Closed { get; private set; }
    public void SetSlots(
```
Replace with:
```csharp
    public bool Closed { get; private set; }
    public bool Editing { get; private set; }
    public void SetEditing(bool editing) => Editing = editing;
    public void SetSlots(
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, find:
```csharp
        Assert.True(f.Writer.Current.Tiles.ContainsKey("c")); // the definition stays
    }
}
```
Replace with:
```csharp
        Assert.True(f.Writer.Current.Tiles.ContainsKey("c")); // the definition stays
    }

    [Fact]
    public void Edit_mode_switches_every_bar_and_reaches_bars_created_while_on()
    {
        var f = new Fixture(Config, ("MAIN", true), ("SECOND", false));

        f.Host.SetEditMode(true);

        Assert.True(f.Host.Editing);
        Assert.True(f.Bars["MAIN"].Editing);
        Assert.True(f.Bars["SECOND"].Editing);

        f.Source.Current = [.. f.Source.Current, FakeTaskbarSource.Display("THIRD", false)];
        f.Source.Raise();
        Assert.True(f.Bars["THIRD"].Editing);

        f.Host.SetEditMode(false);
        Assert.All(f.Bars.Values, b => Assert.False(b.Editing));
    }
}
```

**Replace** `tests/TaskbarTiles.Tests/SlotViewTests.cs` (whole file; two edit-mode tests added):
```csharp
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class SlotViewTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    static TileRuntime Runtime(string id, double width = 132) =>
        new(id, "fake", width, () => new FakeConnector(), NoSettings, new ImmediateDispatcher(), new CollectingLog());

    static TileCommands Commands() => new(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());

    [Fact]
    public void A_stack_shows_one_member_with_dots_and_takes_its_widest_width() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a", 132), Runtime("b", 268)], false, TimeSpan.FromSeconds(5));

        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        Assert.Equal(268, view.Width);
        Assert.Equal(0, view.CurrentMember);
        Assert.Same(slot.Members[0], view.Current.Runtime);
        Assert.Equal(Visibility.Visible, view.Current.Visibility);
        view.Detach();
    });

    [Fact]
    public void Wheel_down_shows_the_next_member() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a"), Runtime("b")], false, TimeSpan.FromSeconds(5));
        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.MouseWheelEvent });

        Assert.Equal(1, view.CurrentMember);
        Assert.Same(slot.Members[1], view.Current.Runtime);
        view.Detach();
    });

    [Fact]
    public void Compact_slots_are_40_dip_wide() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a", 268)], true, null), () => false, Commands(), new CollectingLog());

        view.Compact = true;

        Assert.Equal(LayoutEngine.CompactWidth, view.Width);
        Assert.True(view.Current.Compact);
        Assert.Null(view.Rotation);
        view.Detach();
    });

    [Fact]
    public void Edit_mode_shows_the_remove_button_and_suspends_rotation() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a"), Runtime("b")], false, TimeSpan.FromSeconds(5)), () => false, Commands(), new CollectingLog());
        var removed = 0;
        view.RemoveRequested += (_, _) => removed++;

        Assert.Equal(Visibility.Collapsed, view.RemoveButton.Visibility);
        view.Editing = true;

        Assert.Equal(Visibility.Visible, view.RemoveButton.Visibility);
        Assert.True(view.Rotation!.Suspended);
        view.RemoveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, removed);

        view.Editing = false;
        Assert.False(view.Rotation.Suspended);
        Assert.Equal(Visibility.Collapsed, view.RemoveButton.Visibility);
        view.Detach();
    });

    [Fact]
    public void Drop_highlight_only_applies_in_edit_mode() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a")], false, null), () => false, Commands(), new CollectingLog());

        view.DropHighlight = true;
        Assert.False(view.DropHighlight);

        view.Editing = true;
        view.DropHighlight = true;
        Assert.True(view.DropHighlight);

        view.Editing = false;
        Assert.False(view.DropHighlight);
        view.Detach();
    });
}

/// <summary>Runs WPF code on a fresh STA thread and rethrows what it threw.</summary>
public static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~HostController|FullyQualifiedName~SlotViewTests"`
Expected: build errors CS1501 (`Entries` takes 1 argument), CS0117 (`BarCommand.EditLayout`), CS1061 (`SetEditMode`, `RemoveButton`, `Editing` missing).

- [ ] **Step 3: Implement**

**Replace** `src/TaskbarTiles.Host/HostController.cs` (whole file):
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetSlots(IReadOnlyList<BarSlot> slots);
    void Apply(TaskbarInfo info);

    /// <summary>Edit mode: dashed tiles, drag, ×, + and Done.</summary>
    void SetEditing(bool editing);
    void Close();
}

/// <summary>Layout changes a bar asks for. HostController implements it. UI thread only.</summary>
public interface ILayoutEditor
{
    /// <summary>Edit mode is on for every bar at once; each bar still edits only its own display's layout.</summary>
    bool Editing { get; }

    void SetEditMode(bool editing);

    /// <summary>
    /// Applies <paramref name="edit"/> to the layout the display shows and saves config.json under the display's
    /// edit key (<see cref="DisplayResolver.EditKey"/>). Returns null when saved or when nothing changed, otherwise why
    /// nothing was saved.
    /// </summary>
    ConfigError? EditLayout(string displayId, Func<DisplayLayout, DisplayLayout> edit);

    /// <summary>What edit mode's "+" offers on this display.</summary>
    IReadOnlyList<GalleryItem> GalleryFor(string displayId);

    /// <summary>Places the gallery item at the end of the display's layout, adding its tile definition when it is new.</summary>
    ConfigError? AddFromGallery(string displayId, GalleryItem item);
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar,
    string? configDirectory = null,
    IConfigWriter? writer = null,
    IReadOnlyList<ScriptTemplate>? templates = null) : ILayoutEditor, IAsyncDisposable
{
    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);
    readonly TileRuntimeOptions _runtimeOptions = new() { ConfigDirectory = configDirectory ?? AppPaths.DataDir };
    bool _disposed;

    public IReadOnlyDictionary<string, TileRuntime> Runtimes => _runtimes.ToDictionary(kv => kv.Key, kv => kv.Value.Runtime);

    public bool Editing { get; private set; }

    public void Start()
    {
        taskbars.Changed += (_, _) => Sync();
        taskbars.Start();
        Sync();
    }

    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();

    public void NotifyResumed()
    {
        if (_disposed) return;
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    /// <summary>
    /// Turns edit mode on or off on every bar, so a display without tiles (whose bar is otherwise hidden) can get some.
    /// </summary>
    public void SetEditMode(bool editing)
    {
        if (_disposed || Editing == editing) return;
        Editing = editing;
        foreach (var (id, bar) in _bars)
        {
            try { bar.SetEditing(editing); }
            catch (Exception ex) { logFor("host").Error($"Switching edit mode on display '{id}' failed", ex); }
        }
    }

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

    public IReadOnlyList<GalleryItem> GalleryFor(string displayId)
    {
        var cfg = config();
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        var layout = display is null ? DisplayLayout.Empty : DisplayResolver.Resolve(cfg, displayId, display.IsPrimary);
        return TileGallery.Build(cfg, layout, catalog.Connectors.Keys, templates ?? ScriptTemplates.Samples);
    }

    public ConfigError? AddFromGallery(string displayId, GalleryItem item)
    {
        if (item.NewTile is not { } definition) return EditLayout(displayId, l => LayoutEdits.Append(l, item.TileId));

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

    /// <summary>The display to edit, or (null, why not).</summary>
    (TaskbarInfo? Display, ConfigError? Refused) EditTarget(string displayId)
    {
        if (_disposed) return (null, new ConfigError("Taskbar Tiles is shutting down."));
        if (writer is null) return (null, new ConfigError("This host cannot save config.json."));
        var display = taskbars.Current.FirstOrDefault(d => d.DisplayId == displayId);
        return display is null ? (null, new ConfigError($"Display '{displayId}' is no longer connected.")) : (display, null);
    }

    ConfigError? Saved(string displayId, string key, ConfigError? error)
    {
        if (error is not null) return error;
        logFor("host").Info($"Saved the layout of display '{displayId}' under \"{key}\"");
        ui.Post(Sync); // not inline: the request usually comes from a handler of a view this rebuilds
        return null;
    }

    void Sync()
    {
        if (_disposed) return;
        var cfg = config();
        var displays = taskbars.Current.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        var layouts = displays.ToDictionary(d => d.DisplayId, d => DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary));

        SyncRuntimes(cfg, layouts.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            try { _bars[gone].Close(); }
            catch (Exception ex) { logFor("host").Error($"Closing bar for display '{gone}' failed", ex); }
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            try
            {
                if (!_bars.TryGetValue(info.DisplayId, out var bar))
                {
                    _bars[info.DisplayId] = bar = createBar(info.DisplayId);
                    if (Editing) bar.SetEditing(true);
                }
                bar.SetSlots(BarSlots(layouts[info.DisplayId]));
                bar.Apply(info);
            }
            catch (Exception ex)
            {
                logFor("host").Error($"Updating bar for display '{info.DisplayId}' failed", ex);
            }
        }
    }

    /// <summary>Config slots as the bar shows them; an empty stack shows nothing but keeps its index.</summary>
    IReadOnlyList<BarSlot> BarSlots(DisplayLayout layout)
    {
        var slots = new List<BarSlot>();
        for (var i = 0; i < layout.Slots.Count; i++)
        {
            switch (layout.Slots[i])
            {
                case TileSlot t when _runtimes.TryGetValue(t.TileId, out var entry):
                    slots.Add(new BarSlot(i, [entry.Runtime], t.Pin, null));
                    break;
                case StackSlot s when s.TileIds.Count > 0 && s.TileIds.All(_runtimes.ContainsKey):
                    slots.Add(new BarSlot(i, s.TileIds.Select(id => _runtimes[id].Runtime).ToList(), false, s.Rotate));
                    break;
            }
        }
        return slots;
    }

    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        foreach (var (id, entry) in _runtimes.ToList())
        {
            var keep = wanted.Contains(id) && cfg.Tiles.TryGetValue(id, out var now) && SameConfig(now, entry.Config);
            if (keep) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var id in wanted)
        {
            if (_runtimes.ContainsKey(id) || !cfg.Tiles.TryGetValue(id, out var tileCfg)) continue;

            var connectorId = tileCfg.Connector;
            // The runtime calls this on the thread pool (plugin constructors are plugin code) and again for every restart.
            // A throwing constructor shows the error look and is retried with backoff.
            Func<ITileConnector> create = () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId);
            var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
            var runtime = new TileRuntime(id, connectorId, width, create, tileCfg.Settings, ui, logFor($"tile-{id}"), _runtimeOptions);
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    /// <summary>
    /// Bars (WPF windows) are closed synchronously before the first await, so call this on the UI thread.
    /// Everything after the first await is off-thread safe (ConfigureAwait(false)).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var bar in _bars.Values)
        {
            try { bar.Close(); }
            catch (Exception ex) { logFor("host").Error("Closing a bar failed", ex); }
        }
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
```

**Replace** `src/TaskbarTiles.Host/UI/TileCommands.cs` (whole file):
```csharp
using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Commands on one tile, from its context menu or its error popup.</summary>
public enum TileCommand { Configure, Refresh, ShowLog, Restart }

/// <summary>Commands on the whole app.</summary>
public enum BarCommand { EditLayout, DoneEditing, OpenConfig, Exit }

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

public static class TileMenu
{
    /// <summary>The right-click menu of a slot, or of the bar's gaps when <paramref name="slot"/> is null.</summary>
    public static IReadOnlyList<MenuEntry> Entries(SlotMenuState? slot, bool editing)
    {
        var editEntry = editing ? new MenuEntry("Done editing", Bar: BarCommand.DoneEditing) : new MenuEntry("Edit layout", Bar: BarCommand.EditLayout);
        var entries = new List<MenuEntry>();
        if (slot is not null)
        {
            entries.Add(new("Configure connector…", TileCommand.Configure));
            entries.Add(new("Refresh now", TileCommand.Refresh));
            entries.Add(new("Show connector log", TileCommand.ShowLog));
            entries.Add(new("Restart tile", TileCommand.Restart));
            entries.Add(MenuEntry.Separator);
            entries.Add(new("Move left", Layout: LayoutCommand.MoveLeft, Enabled: !slot.IsFirst));
            entries.Add(new("Move right", Layout: LayoutCommand.MoveRight, Enabled: !slot.IsLast));
            if (slot.IsStack)
            {
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
                entries.Add(slot.RotationPaused
                    ? new("Resume rotation", Layout: LayoutCommand.ResumeRotation)
                    : new("Pause rotation", Layout: LayoutCommand.PauseRotation));
            }
            else
            {
                entries.Add(new("Add to stack", Layout: LayoutCommand.AddToStack, Enabled: slot.CanStack));
            }
            entries.Add(MenuEntry.Separator);
            entries.Add(editEntry);
            entries.Add(new("Remove tile", Layout: LayoutCommand.RemoveTile));
            entries.Add(MenuEntry.Separator);
        }
        else
        {
            entries.Add(editEntry);
            entries.Add(MenuEntry.Separator);
        }
        entries.Add(new("Show config file", Bar: BarCommand.OpenConfig));
        entries.Add(MenuEntry.Separator);
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }
}

/// <summary>Executes tile commands. Called on the UI thread; never throws.</summary>
public sealed class TileCommands(string configFile, string logsDir, Action<string> openFile, ITileLog log)
{
    /// <summary>Same file FileLog.For(logsDir, $"tile-{id}") writes.</summary>
    public string LogPathFor(string tileId) => Path.Combine(logsDir, FileLog.Sanitize($"tile-{tileId}") + ".log");

    public static bool TryFromActionId(string actionId, out TileCommand command)
    {
        switch (actionId)
        {
            case TileRuntime.RestartActionId: command = TileCommand.Restart; return true;
            case TileRuntime.ShowLogActionId: command = TileCommand.ShowLog; return true;
            default: command = default; return false;
        }
    }

    /// <summary>Popup and button actions: the host's own action ids become commands, everything else goes to the connector.</summary>
    public Task InvokeAsync(TileRuntime runtime, string actionId) =>
        TryFromActionId(actionId, out var command) ? ExecuteAsync(runtime, command) : runtime.InvokeActionAsync(actionId);

    public async Task ExecuteAsync(TileRuntime runtime, TileCommand command)
    {
        try
        {
            switch (command)
            {
                case TileCommand.Configure:
                    Open(configFile);
                    break;
                case TileCommand.ShowLog:
                    Open(EnsureExists(LogPathFor(runtime.TileId)));
                    break;
                case TileCommand.Restart:
                    await runtime.RestartAsync();
                    break;
                case TileCommand.Refresh:
                    // A crashed connector cannot refresh; a restart is what the user wants.
                    if (runtime.LastError is { Fatal: true }) await runtime.RestartAsync();
                    else await runtime.InvokeActionAsync(TileActions.Refresh);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"Command {command} on tile '{runtime.TileId}' failed", ex);
        }
    }

    public void OpenConfig() => Open(configFile);

    void Open(string path)
    {
        try { openFile(path); }
        catch (Exception ex) { log.Error($"Could not open {path}", ex); }
    }

    static string EnsureExists(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }
}
```

**Edit** `src/TaskbarTiles.Host/UI/Styles.xaml`, find:
```xml
    <!-- Flyout action buttons -->
```
Replace with:
```xml
    <!-- The × on a tile in edit mode -->
    <Style x:Key="RemoveButton" TargetType="Button">
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Width" Value="14"/>
        <Setter Property="Height" Value="14"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Grid>
                        <Ellipse x:Name="Bg" Fill="#55525E"/>
                        <TextBlock Text="×" Foreground="{TemplateBinding Foreground}" HorizontalAlignment="Center"
                                   VerticalAlignment="Center" Margin="0,-2,0,0"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bg" Property="Fill" Value="#E5534B"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Flyout action buttons -->
```

**Replace** `src/TaskbarTiles.Host/UI/SlotView.cs` (whole file):
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One slot on a bar: a single tile, or a stack that shows one member at a time with position dots. A stack rotates
/// every "rotate", pauses while hovered, cycles on the mouse wheel, and lets an urgent member take over until it is clicked.
/// In edit mode it gets a dashed outline, a shield that keeps clicks away from the tile (the bar turns them into drags)
/// and a × button.
/// </summary>
public sealed class SlotView : Grid
{
    static readonly Brush Accent = Frozen(Color.FromRgb(0x4C, 0x7C, 0xE0));
    static readonly Brush AccentFill = Frozen(Color.FromArgb(0x2E, 0x7A, 0xA2, 0xF7));

    readonly List<TileView> _members = [];
    readonly StackPanel? _dots;
    readonly DispatcherTimer? _timer;
    readonly Rectangle _outline;
    readonly Border _shield;
    bool _compact;
    bool _editing;
    bool _dropHighlight;

    public SlotView(BarSlot slot, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        Slot = slot;
        foreach (var runtime in slot.Members)
        {
            var view = new TileView(runtime, isLightTheme, commands, log);
            _members.Add(view);
            Children.Add(view);
        }

        if (slot.IsStack)
        {
            Rotation = new StackRotation(slot.Members.Count);
            _dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false,
            };
            for (var i = 0; i < slot.Members.Count; i++)
            {
                var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1.5, 0, 1.5, 0) };
                dot.SetResourceReference(Shape.FillProperty, "TileForeground");
                _dots.Children.Add(dot);
            }
            Children.Add(_dots);

            for (var i = 0; i < slot.Members.Count; i++) Rotation.SetUrgent(i, slot.Members[i].State.Urgent);
            foreach (var runtime in slot.Members) runtime.StateChanged += OnMemberStateChanged;

            MouseEnter += (_, _) => Rotation.Hovered = true;
            MouseLeave += (_, _) => Rotation.Hovered = false;
            MouseWheel += OnWheel;
            // After the member's own click handling: the click that opens the urgent member's popup also releases the stack.
            AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Changed(Rotation.Acknowledge())), handledEventsToo: true);

            if (slot.Members.Count > 1)
            {
                _timer = new DispatcherTimer { Interval = slot.Rotate!.Value };
                _timer.Tick += (_, _) => Changed(Rotation.Tick());
                _timer.Start();
            }
        }

        _outline = new Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _shield = new Border { Background = Frozen(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll, Visibility = Visibility.Collapsed };
        RemoveButton = new Button
        {
            Content = "×",
            ToolTip = slot.IsStack ? "Remove the tile shown from this stack" : "Remove tile",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Visibility = Visibility.Collapsed,
            Style = TryFindResource("RemoveButton") as Style,
        };
        RemoveButton.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
        Children.Add(_outline);
        Children.Add(_shield);
        Children.Add(RemoveButton);

        ApplyWidths();
        ShowCurrent();
        ApplyEditLook();
    }

    /// <summary>The × was clicked: remove this slot, or for a stack the member shown.</summary>
    public event EventHandler? RemoveRequested;

    internal Button RemoveButton { get; }

    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            if (Rotation is not null) Rotation.Suspended = value;
            if (!value) _dropHighlight = false;
            ApplyEditLook();
        }
    }

    /// <summary>While dragging in edit mode: this slot is where the dragged one would be stacked.</summary>
    public bool DropHighlight
    {
        get => _dropHighlight;
        set
        {
            if (_dropHighlight == value) return;
            _dropHighlight = value && _editing;
            ApplyEditLook();
        }
    }

    public BarSlot Slot { get; private set; }

    /// <summary>Null for a single tile.</summary>
    public StackRotation? Rotation { get; }

    /// <summary>Index of the member shown (always 0 for a single tile).</summary>
    public int CurrentMember => Rotation?.Index ?? 0;

    public TileView Current => _members[CurrentMember];

    public double FullWidth => Slot.FullWidth;

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            ApplyWidths();
        }
    }

    /// <summary>Where member popups appear; null places them above the tile itself.</summary>
    public FrameworkElement? FlyoutAnchor
    {
        set { foreach (var m in _members) m.FlyoutAnchor = value; }
    }

    /// <summary>Takes a slot with the same content at a new index (after a reorder), keeping rotation state.</summary>
    public void Retarget(BarSlot slot)
    {
        if (!slot.SameContent(Slot)) throw new ArgumentException("Retarget needs a slot with the same content.", nameof(slot));
        Slot = slot;
    }

    public void Detach()
    {
        _timer?.Stop();
        foreach (var runtime in Slot.Members) runtime.StateChanged -= OnMemberStateChanged;
        foreach (var m in _members) m.Detach();
    }

    void OnMemberStateChanged(object? sender, EventArgs e)
    {
        if (Rotation is null || sender is not TileRuntime runtime) return;
        for (var i = 0; i < Slot.Members.Count; i++)
            if (ReferenceEquals(Slot.Members[i], runtime)) Changed(Rotation.SetUrgent(i, runtime.State.Urgent));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Rotation is null) return;
        Changed(Rotation.Step(e.Delta < 0 ? +1 : -1)); // wheel down shows the next member
        e.Handled = true;
    }

    void Changed(bool indexChanged)
    {
        if (indexChanged) ShowCurrent();
    }

    void ApplyWidths()
    {
        var width = _compact ? LayoutEngine.CompactWidth : FullWidth;
        Width = width;
        foreach (var m in _members)
        {
            m.Width = width;
            m.Compact = _compact;
        }
    }

    void ApplyEditLook()
    {
        var edit = _editing ? Visibility.Visible : Visibility.Collapsed;
        _outline.Visibility = edit;
        _shield.Visibility = edit;
        RemoveButton.Visibility = edit;
        if (_dropHighlight)
        {
            _outline.Stroke = Accent;
            _outline.Fill = AccentFill;
            _outline.StrokeDashArray = null;
        }
        else
        {
            _outline.SetResourceReference(Shape.StrokeProperty, "TileForeground");
            _outline.Fill = null;
            _outline.StrokeDashArray = [3, 2];
        }
        _outline.Opacity = _dropHighlight ? 1 : 0.6;
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    void ShowCurrent()
    {
        var current = CurrentMember;
        for (var i = 0; i < _members.Count; i++)
            _members[i].Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
        if (_dots is null) return;
        for (var i = 0; i < _dots.Children.Count; i++)
            ((UIElement)_dots.Children[i]).Opacity = i == current ? 1 : 0.3;
    }
}
```

**Replace** `src/TaskbarTiles.Host/UI/TileBar.cs` (whole file):
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One per display: a borderless topmost strip in the free zone. LayoutEngine decides per slot: full, compact, or
/// inside the » overflow popup. Reflows on every free-zone or slot change. In edit mode it shows dashed tiles that can
/// be dragged, a × on each, and + and Done at the end; every change is saved to config.json right away.
/// </summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;
    const double InsetDip = 4; // tiles sit 4 DIP inside the taskbar's top and bottom edges
    const double AddWidth = 40;
    const double DoneWidth = 56;
    const double DragThreshold = 4;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, InsetDip, 0, InsetDip) };
    readonly List<SlotView> _views = [];
    readonly Button _overflowButton;
    readonly Button _addButton;
    readonly Button _doneButton;
    readonly ITileLog _log;
    readonly TileCommands _commands;
    readonly ILayoutEditor _editor;
    IReadOnlyList<BarSlot> _slots = [];
    List<SlotView> _overflowed = [];
    StackPanel? _overflowPanel; // set while the overflow popup is open
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;
    bool _editing;
    SlotView? _dragSource;
    SlotView? _dropTarget;
    Point _dragStart;
    bool _dragging;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
        DisplayId = displayId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        _overflowButton = BarButton("»", LayoutEngine.OverflowWidth, "More tiles");
        _overflowButton.Click += (_, _) => ToggleOverflow();
        _addButton = BarButton("+", AddWidth, "Add a tile");
        _addButton.FontSize = 18;
        _addButton.Click += (_, _) => ShowGallery();
        _doneButton = BarButton("Done", DoneWidth, "Leave edit mode (Esc)");
        _doneButton.Click += (_, _) => _editor.SetEditMode(false);

        // Bar-wide menu for the gaps between tiles; each slot gets its own menu in CreateView.
        ContextMenu = new ContextMenu();
        FillBarMenu(ContextMenu);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the slots' menus.
        ContextMenuOpening += (_, _) => { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); };
        PreviewKeyDown += (_, e) =>
        {
            if (!_editing || e.Key != Key.Escape) return;
            e.Handled = true;
            _editor.SetEditMode(false);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetActivatable(_editing);
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public string DisplayId { get; }
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Shows the slots. Views whose slot content is unchanged are kept (with their rotation state), even when they moved.
    /// </summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots)
    {
        if (_slots.SequenceEqual(slots)) return;
        _slots = slots.ToList();
        EndDrag();
        CloseOverflow(); // the popup may hold views that move or go away

        var pool = _views.ToList();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var slot in slots)
        {
            var view = pool.FirstOrDefault(v => v.Slot.SameContent(slot));
            if (view is not null)
            {
                pool.Remove(view);
                view.Retarget(slot);
            }
            else
            {
                try { view = CreateView(slot); }
                catch (Exception ex)
                {
                    _log.Error($"Slot {slot.Index} ({string.Join(", ", slot.Members.Select(m => m.TileId))}) could not be shown", ex);
                    continue;
                }
            }
            _views.Add(view);
        }
        foreach (var unused in pool) unused.Detach();
        Reflow();
    }

    public void Apply(TaskbarInfo info)
    {
        _info = info;
        if (_light != info.IsLightTheme)
        {
            _light = info.IsLightTheme;
            Theme.ApplyTo(Resources, info.IsLightTheme);
        }
        Reflow();
    }

    /// <summary>
    /// Edit mode. The bar can be activated meanwhile (so Esc reaches it); popups close and stacks stop rotating.
    /// </summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        EndDrag();
        FlyoutWindow.CloseOpen();
        foreach (var view in _views) view.Editing = editing;
        SetActivatable(editing);
        FillBarMenu(ContextMenu);
        Reflow();
    }

    Button BarButton(string content, double width, string tooltip) => new()
    {
        Content = content,
        Width = width,
        ToolTip = tooltip,
        Style = TryFindResource("BarButton") as Style,
    };

    void SetActivatable(bool activatable)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
        ex = activatable ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    SlotView CreateView(BarSlot slot)
    {
        var view = new SlotView(slot, () => _light ?? false, _commands, _log) { ContextMenu = new ContextMenu(), Editing = _editing };
        // Built when it opens, for the member shown at that moment.
        view.ContextMenuOpening += (_, _) => FillSlotMenu(view.ContextMenu, view);
        view.MouseLeftButtonDown += (_, e) => BeginDrag(view, e);
        view.RemoveRequested += (_, _) =>
        {
            var current = view.Slot;
            var member = view.CurrentMember;
            Edit(l => LayoutEdits.Remove(l, current.Index, current.IsStack ? member : null));
        };
        return view;
    }

    void FillBarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(null, _editing)) menu.Items.Add(Item(entry, null, 0));
    }

    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        var position = _views.IndexOf(view);
        var state = new SlotMenuState(
            IsFirst: position <= 0,
            IsLast: position == _views.Count - 1,
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            CanStack: !view.Slot.IsStack && _views.Count > 1);
        var member = view.CurrentMember; // the member shown when the menu opened, even if the stack rotates meanwhile
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(state, _editing)) menu.Items.Add(Item(entry, view, member));
    }

    Control Item(MenuEntry entry, SlotView? view, int member)
    {
        if (entry.IsSeparator) return new Separator();
        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
        if (entry.Tile is { } command && view is not null)
        {
            var runtime = view.Slot.Members[member];
            item.Click += async (_, _) => await _commands.ExecuteAsync(runtime, command); // never throws
        }
        else if (entry.Layout is { } layout && view is not null)
            item.Click += (_, _) => Run(layout, view, member);
        else if (entry.Bar == BarCommand.EditLayout)
            item.Click += (_, _) => EnterEditMode();
        else if (entry.Bar == BarCommand.DoneEditing)
            item.Click += (_, _) => _editor.SetEditMode(false);
        else if (entry.Bar == BarCommand.OpenConfig)
            item.Click += (_, _) => _commands.OpenConfig();
        else if (entry.Bar == BarCommand.Exit)
            item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        return item;
    }

    void EnterEditMode()
    {
        _editor.SetEditMode(true); // every bar, this one included
        if (!IsVisible) return;
        Activate();
        Focus();
    }

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
            case LayoutCommand.RemoveFromStack:
                Edit(l => LayoutEdits.RemoveFromStack(l, slot.Index, member));
                break;
            case LayoutCommand.RemoveTile:
                Edit(l => LayoutEdits.Remove(l, slot.Index, slot.IsStack ? member : null));
                break;
        }
    }

    void Edit(Func<DisplayLayout, DisplayLayout> edit) => Save(() => _editor.EditLayout(DisplayId, edit));

    /// <summary>Saves a layout change; the new slots arrive through SetSlots. Shows a notice when nothing was saved.</summary>
    void Save(Func<ConfigError?> save)
    {
        ConfigError? error;
        try { error = save(); }
        catch (Exception ex)
        {
            _log.Error("Layout edit failed", ex);
            error = new ConfigError("The layout could not be changed: " + ex.Message);
        }
        if (error is null) return;

        _log.Warn($"Layout not saved: {error}");
        try { NotificationWindow.Show(ConfigNotice.SaveFailedTitle, ConfigNotice.SaveFailedBody(error), _light ?? false, _commands.OpenConfig, _log); }
        catch (Exception ex) { _log.Error("Could not show the save notice", ex); }
    }

    /// <summary>The + button: tiles this display does not show yet, a new tile per plugin, and the sample scripts.</summary>
    void ShowGallery()
    {
        IReadOnlyList<GalleryItem> items;
        try { items = _editor.GalleryFor(DisplayId); }
        catch (Exception ex)
        {
            _log.Error("Could not list the tile gallery", ex);
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(FlyoutWindow.Text("Add a tile", 14, FontWeights.SemiBold, "TileForeground"));
        var hint = FlyoutWindow.Text(items.Count == 0
            ? "Nothing to add: every tile is on this display already."
            : "It is added at the end of this display's tiles.", 12, FontWeights.Normal, "TileDim");
        hint.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(hint);

        var grid = new UniformGrid { Columns = 2 };
        foreach (var item in items)
        {
            var content = new StackPanel();
            var title = FlyoutWindow.Text(item.Title, 12.5, FontWeights.SemiBold, "TileForeground");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var subtitle = FlyoutWindow.Text(item.Subtitle, 11.5, FontWeights.Normal, "TileDim");
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(title);
            content.Children.Add(subtitle);
            var button = new Button
            {
                Content = content,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 6, 8, 6),
                Style = TryFindResource("FlyoutButton") as Style,
            };
            button.Click += (_, _) =>
            {
                FlyoutWindow.CloseOpen();
                Save(() => _editor.AddFromGallery(DisplayId, item));
            };
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        FlyoutWindow.ShowPanel(_addButton, panel, _light ?? false, _log);
    }

    void BeginDrag(SlotView view, MouseButtonEventArgs e)
    {
        if (!_editing || !ReferenceEquals(view.Parent, _panel)) return;
        _dragSource = view;
        _dragStart = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragSource is null) return;
        var p = e.GetPosition(this);
        if (!_dragging && (Math.Abs(p.X - _dragStart.X) > DragThreshold || Math.Abs(p.Y - _dragStart.Y) > DragThreshold))
        {
            _dragging = true;
            _dragSource.Opacity = 0.35;
        }
        if (!_dragging) return;
        var (target, position) = DropTargetAt(e);
        SetDropTarget(position == DropPosition.Into ? target : null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragSource is null) return;
        var source = _dragSource;
        var dragged = _dragging;
        var (target, position) = DropTargetAt(e);
        EndDrag();
        if (!dragged || target is null) return;
        var from = source.Slot.Index;
        var to = target.Slot.Index;
        Edit(l => LayoutEdits.Drop(l, from, to, position));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    /// <summary>The bar slot under the pointer (not the dragged one) and where on it: the middle 40 % means "into".</summary>
    (SlotView? Target, DropPosition Position) DropTargetAt(MouseEventArgs e)
    {
        foreach (var view in _views)
        {
            if (ReferenceEquals(view, _dragSource) || !ReferenceEquals(view.Parent, _panel) || view.ActualWidth <= 0) continue;
            var x = e.GetPosition(view).X;
            if (x >= 0 && x < view.ActualWidth) return (view, LayoutEdits.DropPositionAt(x / view.ActualWidth));
        }
        return (null, DropPosition.Before);
    }

    void SetDropTarget(SlotView? target)
    {
        if (ReferenceEquals(target, _dropTarget)) return;
        if (_dropTarget is not null) _dropTarget.DropHighlight = false;
        _dropTarget = target;
        if (target is not null) target.DropHighlight = true;
    }

    void EndDrag()
    {
        var source = _dragSource;
        _dragSource = null; // first: releasing the capture raises LostMouseCapture, which calls back here
        _dragging = false;
        if (source is not null) source.Opacity = 1;
        SetDropTarget(null);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;
        var hidden = _info.Hidden || zone.IsEmpty;
        var chrome = _editing ? AddWidth + GapDip + DoneWidth : 0;

        var layout = hidden
            ? null
            : LayoutEngine.Compute(_views.Select(v => new LayoutItem(v.FullWidth, v.Slot.Pinned)).ToList(),
                zone.Width / scale - (_editing ? chrome + GapDip : 0), GapDip);
        Arrange(layout, showChrome: _editing && !hidden);

        var widthDip = (layout?.Width ?? 0) + (_editing && !hidden ? chrome + (layout?.Width > 0 ? GapDip : 0) : 0);
        if (widthDip <= 0)
        {
            CloseOverflow();
            if (IsVisible) Hide();
            return;
        }

        var w = (int)Math.Ceiling(widthDip * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Puts full and compact slots on the bar, then the » tile when anything overflows, then + and Done in edit mode.</summary>
    void Arrange(LayoutResult? layout, bool showChrome)
    {
        var shown = new List<FrameworkElement>();
        var overflowed = new List<SlotView>();
        for (var i = 0; i < _views.Count; i++)
        {
            var mode = layout?.Modes[i] ?? SlotMode.Overflow;
            if (mode == SlotMode.Overflow)
            {
                overflowed.Add(_views[i]);
                continue;
            }
            _views[i].Compact = mode == SlotMode.Compact;
            shown.Add(_views[i]);
        }
        if (layout?.ShowOverflow == true) shown.Add(_overflowButton);
        if (showChrome)
        {
            shown.Add(_addButton);
            shown.Add(_doneButton);
        }

        if (!overflowed.SequenceEqual(_overflowed)) CloseOverflow();
        _overflowed = overflowed;

        if (!shown.SequenceEqual(_panel.Children.Cast<FrameworkElement>()))
        {
            _panel.Children.Clear();
            foreach (var element in shown) _panel.Children.Add(element);
        }
        for (var i = 0; i < shown.Count; i++)
            shown[i].Margin = new Thickness(0, 0, i < shown.Count - 1 ? GapDip : 0, 0);
    }

    /// <summary>The » tile: a popup listing the overflowed slots at full size, still clickable, with their menus.</summary>
    void ToggleOverflow()
    {
        if (_overflowPanel is not null)
        {
            CloseOverflow();
            return;
        }
        if (_overflowed.Count == 0 || _info is null) return;

        var tileHeight = Math.Max(24, _info.FreeZone.Zone.Height / _info.DpiScale - 2 * InsetDip);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (var i = 0; i < _overflowed.Count; i++)
        {
            var view = _overflowed[i];
            view.Compact = false;
            view.Height = tileHeight;
            view.FlyoutAnchor = _overflowButton; // a tile's own popup replaces this one, above the » tile
            view.Margin = new Thickness(0, 0, 0, i < _overflowed.Count - 1 ? GapDip : 0);
            panel.Children.Add(view);
        }
        _overflowPanel = panel;
        var moved = _overflowed.ToList();
        FlyoutWindow.ShowPanel(_overflowButton, panel, _light ?? false, _log, width: null, onClosed: () =>
        {
            panel.Children.Clear(); // hand the views back so the bar can show them again
            foreach (var view in moved)
            {
                view.Height = double.NaN;
                view.FlyoutAnchor = null;
            }
            if (_overflowPanel == panel) _overflowPanel = null;
        });
    }

    void CloseOverflow()
    {
        if (_overflowPanel is not null) FlyoutWindow.CloseOpen();
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseOverflow();
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests|FullyQualifiedName~HostController|FullyQualifiedName~SlotViewTests"`
Expected: PASS.

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 408`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/HostController.cs src/TaskbarTiles.Host/UI/TileCommands.cs src/TaskbarTiles.Host/UI/Styles.xaml src/TaskbarTiles.Host/UI/SlotView.cs src/TaskbarTiles.Host/UI/TileBar.cs tests/TaskbarTiles.Tests/TileCommandsTests.cs tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs tests/TaskbarTiles.Tests/SlotViewTests.cs
git commit -m "feat(ui): edit mode with drag, stack drop, remove, gallery and Done/Esc" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Unknown keys inside a script hook are errors

**Files:**
- Modify: `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`, `docs/script-connectors.md`
- Test: `tests/TaskbarTiles.Tests/ScriptSettingsTests.cs`

**Interfaces:**
- Consumes: `ScriptSettings.Parse(JsonElement)` (existing)
- Produces: a hook object may contain only `run` and `timeout` (`interval` also `every`); any other key adds the error `Hook "<name>" has an unknown setting "<key>"; use run, timeout.` (or `use run, every, timeout.`) and the hook is dropped, so the connector refuses to run (deferred from milestone 2: a typo such as `"timout"` was ignored silently).

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/ScriptSettingsTests.cs`, find:
```csharp
    [InlineData("""{ "hooks": { "click": { "run": "a.ps1", "timeout": "x" } } }""", "timeout")]
    public void Invalid_settings_explain_themselves(string json, string expected)
```
Replace with:
```csharp
    [InlineData("""{ "hooks": { "click": { "run": "a.ps1", "timeout": "x" } } }""", "timeout")]
    [InlineData("""{ "hooks": { "click": { "run": "a.ps1", "timout": "5s" } } }""", "unknown setting \"timout\"; use run, timeout.")]
    [InlineData("""{ "hooks": { "interval": { "every": "5s", "run": "a.ps1", "evrey": "1s" } } }""", "unknown setting \"evrey\"; use run, every, timeout.")]
    [InlineData("""{ "hooks": { "watch": { "run": "a.ps1", "every": "5s" } } }""", "Hook \"watch\" has an unknown setting \"every\"")]
    public void Invalid_settings_explain_themselves(string json, string expected)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ScriptSettingsTests"`
Expected: 3 failures in `Invalid_settings_explain_themselves` (no error contains the expected text).

- [ ] **Step 3: Implement**

**Edit** `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`, find:
```csharp
    static readonly HashSet<string> PlainHooks = new(StringComparer.Ordinal) { "interval", "watch", "click", "flyout", "startup", "resume" };
```
Replace with:
```csharp
    static readonly HashSet<string> PlainHooks = new(StringComparer.Ordinal) { "interval", "watch", "click", "flyout", "startup", "resume" };

    /// <summary>The settings a hook object may have, in the order error messages list them.</summary>
    static readonly string[] IntervalKeys = ["run", "every", "timeout"];
    static readonly string[] HookKeys = ["run", "timeout"];
```

**Edit** `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`, find:
```csharp
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Hook \"{name}\" must be an object with \"run\".");
            return null;
        }
```
Replace with:
```csharp
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Hook \"{name}\" must be an object with \"run\".");
            return null;
        }

        // A typo such as "timout" would otherwise be ignored silently and the default used.
        var allowed = name == "interval" ? IntervalKeys : HookKeys;
        var unknown = el.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !allowed.Contains(n, StringComparer.Ordinal));
        if (unknown is not null)
        {
            errors.Add($"Hook \"{name}\" has an unknown setting \"{unknown}\"; use {string.Join(", ", allowed)}.");
            return null;
        }
```

**Edit** `docs/script-connectors.md`, find:
```markdown
`settings.hooks` maps hook names to `{ "run": "...", "timeout": "20s" }`. Except for `interval`, a plain string is short for `{ "run": "..." }`.
```
Replace with:
```markdown
`settings.hooks` maps hook names to `{ "run": "...", "timeout": "20s" }` (`interval` also takes `every`; any other key is an error). Except for `interval`, a plain string is short for `{ "run": "..." }`.
```

**Edit** `docs/script-connectors.md`, find:
```markdown
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`) put the tile in the error look and nothing runs until `config.json` is fixed.
```
Replace with:
```markdown
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`, an unknown setting inside a hook such as `"timout"`, or `every` on a hook other than `interval`) put the tile in the error look and nothing runs until `config.json` is fixed.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ScriptSettingsTests|FullyQualifiedName~TileGalleryTests"`
Expected: PASS (the gallery test also checks that the script templates still parse without errors).

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 411`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Plugins.Script/ScriptSettings.cs docs/script-connectors.md tests/TaskbarTiles.Tests/ScriptSettingsTests.cs
git commit -m "fix(script): unknown keys inside a hook are settings errors" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Install script and configuration docs

**Files:**
- Create: `tools/install.ps1`, `docs/configuration.md`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj` publish output (`TaskbarTiles.exe`, `plugins\TaskbarTiles.Plugins.Media\…`, `plugins\TaskbarTiles.Plugins.Script\…`, `scripts\*.ps1`)
- Produces: `tools\install.ps1 [-AppDir <path>] [-NoShortcut] [-NoStart]` — stops `TaskbarTiles.exe` only if it runs from `AppDir` (warns about copies elsewhere), `dotnet publish -c Release -o AppDir`, checks the exe and both plugin DLLs exist, creates or updates `Startup\TaskbarTiles.lnk` (target the exe, working directory `AppDir`), starts the exe; prints each step. Default `AppDir` is `%LOCALAPPDATA%\TaskbarTiles\app`. ASCII only, so Windows PowerShell 5.1 reads it without a BOM.

- [ ] **Step 1: Ignore the agent publish folder**

**Edit** `.gitignore`, find:
```text
*.user
```
Replace with:
```text
*.user
artifacts/
```

- [ ] **Step 2: Write the install script**

**Create** `tools/install.ps1`:
```powershell
<#
.SYNOPSIS
    Builds Taskbar Tiles and installs it for the current user.

.DESCRIPTION
    Run it from your own terminal (not from an agent session: the Claude desktop app redirects an agent's writes
    under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the installed app.

    1. Stops TaskbarTiles.exe if it runs from the install folder.
    2. Publishes src\TaskbarTiles.Host (Release) into the install folder.
    3. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    4. Starts the installed app.

    Your config.json, scripts, plugins and logs in %LOCALAPPDATA%\TaskbarTiles are never touched.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.

.PARAMETER NoShortcut
    Do not create or update the Startup shortcut.

.PARAMETER NoStart
    Do not start the app afterwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install.ps1
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\TaskbarTiles.Host\TaskbarTiles.Host.csproj'
$AppDir = [System.IO.Path]::GetFullPath($AppDir)
$exe = Join-Path $AppDir 'TaskbarTiles.exe'

if (-not (Test-Path $project)) { throw "Cannot find $project. Run this script from the repository's tools folder." }

# 1. Stop the installed copy so its files can be replaced. Copies running from elsewhere are left alone.
$running = @(Get-Process -Name TaskbarTiles -ErrorAction SilentlyContinue)
$installed = @($running | Where-Object { $_.Path -and $_.Path.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase) })
foreach ($p in $installed) {
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(10000) | Out-Null
    Write-Host "Stopped TaskbarTiles (process $($p.Id))."
}
foreach ($p in @($running | Where-Object { $installed -notcontains $_ })) {
    Write-Warning "TaskbarTiles is also running from $($p.Path) (process $($p.Id)); it keeps running and the installed copy will exit at start (one instance at a time)."
}
if ($installed.Count -eq 0) { Write-Host 'TaskbarTiles was not running from the install folder.' }

# 2. Publish.
Write-Host "Publishing Release into $AppDir ..."
& dotnet publish $project -c Release -o $AppDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
foreach ($required in @($exe,
        (Join-Path $AppDir 'plugins\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $AppDir 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'))) {
    if (-not (Test-Path $required)) { throw "Publish output is missing $required." }
}
Write-Host "Published $exe"

# 3. Startup shortcut (shows in Task Manager > Startup apps).
if ($NoShortcut) {
    Write-Host 'Skipped the Startup shortcut (-NoShortcut).'
} else {
    $link = Join-Path ([Environment]::GetFolderPath('Startup')) 'TaskbarTiles.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $AppDir
    $shortcut.Description = 'Taskbar Tiles'
    $shortcut.Save()
    Write-Host "Startup shortcut: $link -> $exe"
}

# 4. Start.
if ($NoStart) {
    Write-Host 'Not started (-NoStart).'
} else {
    Start-Process -FilePath $exe -WorkingDirectory $AppDir
    Write-Host 'Started TaskbarTiles.'
}
```

- [ ] **Step 3: Verify it into the repo (agent side; never into %LOCALAPPDATA%)**

Run (PowerShell, repo root):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -AppDir "$PWD\artifacts\app" -NoShortcut -NoStart
Test-Path artifacts\app\scripts\ping.ps1
git status --short
```
Expected: `TaskbarTiles was not running from the install folder.` (plus a warning if the user's installed copy runs: that is expected and it is left alone), `Published …\artifacts\app\TaskbarTiles.exe`, `Skipped the Startup shortcut (-NoShortcut).`, `Not started (-NoStart).`, `True`, and `git status` lists only `.gitignore` and `tools/` (no `artifacts/`). Run the same command a second time: same output (idempotent).

- [ ] **Step 4: Write the configuration docs**

**Create** `docs/configuration.md`:
````markdown
# Configuring Taskbar Tiles

Everything lives in `%LOCALAPPDATA%\TaskbarTiles\config.json`. Right-click a tile → **Show config file** shows it in Explorer. The file is watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).

## Install or update

From your own terminal, in the repository:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

It stops the installed copy, publishes a Release build into `%LOCALAPPDATA%\TaskbarTiles\app`, creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, scripts, plugins and logs are never touched. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.

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
  1. the monitor's device id (edit mode writes these, e.g. `\\?\DISPLAY#DEL41A8#5&2b1c…`)
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

1. Tiles become compact, from the right end: only the icon (or the first letter of the title), 40 px wide, with the title, subtitle and tooltip in the tooltip. Pinned tiles stay full size.
2. If that is not enough, tiles move into a `»` tile at the end, from the right end; pinned tiles go last. Click `»` to see them at full size. They still work there: buttons, popups (shown above the `»` tile) and the right-click menu.

This is recomputed whenever the free space changes (apps opened or closed, alignment, display or DPI changes).

## Edit layout

Right-click a tile or the bar → **Edit layout**. All taskbars switch to edit mode at once (so a display without tiles can get some), but each one edits only its own display's tiles.

- Tiles get a dashed outline and a `×`. Clicks no longer open popups.
- Drag a tile onto the left or right part of another tile to move it there.
- Drop it onto the middle of a stack to add it to the stack, or onto the middle of another tile to make a stack of the two.
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. The tile's definition stays under `tiles`, so `+` can bring it back.
- `+` adds a tile at the end: a tile you already have that this display does not show, a new tile from a loaded plugin, or a new script tile from one of the sample scripts. New tiles get a free id such as `ping-2`.
- **Done** or Esc leaves edit mode.

Outside edit mode the right-click menu also has **Move left**, **Move right**, **Add to stack** (joins the nearest stack, or makes a stack with the neighbor), **Remove from stack** and **Remove tile**.

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
````

- [ ] **Step 5: Run the full suite**

Run: `dotnet build TaskbarTiles.sln` then `dotnet test TaskbarTiles.sln`
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 411`.

- [ ] **Step 6: Commit**

```bash
git add .gitignore tools/install.ps1 docs/configuration.md
git commit -m "build: user-run install script; docs for layout, edit mode and config rewriting" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Install and verify with the user

**Files:**
- Outside the repo (user only): `%LOCALAPPDATA%\TaskbarTiles\app\`, `%LOCALAPPDATA%\TaskbarTiles\config.json`, `config.json.bak`, `scripts\`

**Interfaces:**
- Consumes: everything above
- Produces: the user's confirmation that milestone 3 works on the real taskbar

The agent prepares; the user installs and checks (Steps 2-10) and reports back. Do not edit the user's `config.json` and do not run `tools\install.ps1` against `%LOCALAPPDATA%` from an agent session: give the user the commands and snippets.

- [ ] **Step 1 (agent): Final local check**

```powershell
dotnet build TaskbarTiles.sln
dotnet test TaskbarTiles.sln
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -AppDir "$PWD\artifacts\app" -NoShortcut -NoStart
```
Expected: 0 warnings; `Passed! - Failed: 0, Passed: 411`; the publish into `artifacts\app` succeeds.

- [ ] **Step 2 (user): Back up and install**

In the user's own terminal (not the agent's), from the repo root:
```powershell
Copy-Item "$env:LOCALAPPDATA\TaskbarTiles\config.json" "$env:LOCALAPPDATA\TaskbarTiles\config.before-m3.json"
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```
Expected: "Stopped TaskbarTiles (process …)", "Published …\TaskbarTiles\app\TaskbarTiles.exe", "Startup shortcut: …\Startup\TaskbarTiles.lnk -> …", "Started TaskbarTiles." The existing tiles come back as before. Running the script a second time gives the same result.

- [ ] **Step 3 (user): A stack**

Ask the user to add this tile under `"tiles"` (it turns urgent every other minute) and save `scripts\urgent.ps1` next to the other scripts:
```jsonc
"meeting": { "connector": "script", "settings": { "hooks": { "interval": { "every": "10s", "run": "scripts/urgent.ps1" } } } }
```
`%LOCALAPPDATA%\TaskbarTiles\scripts\urgent.ps1`:
```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$urgent = ((Get-Date).Minute % 2) -eq 0
@{ title = 'Meeting'; subtitle = $(if ($urgent) { 'now!' } else { 'later' }); urgent = $urgent; icon = @{ glyph = 'clock'; color = 'warn' } } | ConvertTo-Json -Compress
```
and to replace `"uptime"` in the `primary` slots with `{ "stack": ["uptime", "ping", "meeting"], "rotate": "5s" }` (adding `ping` from `docs/script-connectors.md` if it is not defined yet). Save.
Expected:
- One tile cell shows one member at a time, with three dots at the bottom; it moves on every 5 s, as wide as the widest member, without changing width.
- With the mouse over it, it stops; the wheel moves forward (down) and back (up).
- On an even minute "Meeting · now!" takes over with a pulsing outline and stays; clicking it releases the stack (it rotates again while Meeting keeps pulsing). On the next even minute it takes over again.
- Right-click the stack: **Pause rotation** stops it, the menu then says **Resume rotation**.

- [ ] **Step 4 (user): Compact and overflow**

Ask the user to make space tight: switch taskbar icons to left-aligned (Settings → Personalization → Taskbar → Taskbar behaviors → Alignment: Left) and open apps until the taskbar fills, or add several tiles (`"uptime"` can be placed more than once: append `"uptime", "uptime", "uptime"` to the `primary` slots).
Expected: as space shrinks, tiles at the right end become 40 px icons (a letter if the tile has no icon), their tooltip shows title, subtitle and tooltip; the pinned media tile stays full. With even less space a `»` tile appears at the end; clicking it lists the hidden tiles at full size; their buttons work, right-click works, and clicking one opens its popup above `»`. Closing apps brings the tiles back to full size. Restore the alignment afterwards if wanted.

- [ ] **Step 5 (user): Tile menu items**

Right-click a plain tile. The menu reads: Configure connector…, Refresh now, Show connector log, Restart tile, Move left, Move right, Add to stack, Edit layout, Remove tile, Show config file, Exit Taskbar Tiles (Move left is disabled on the first tile).
- **Move right** swaps it with its neighbor; it stays after a restart of the app.
- **Add to stack** puts it into the stack; on the stack, **Remove from stack** takes the member shown back out, right after the stack.
- Open `config.json`: the comments are gone, the file is indented, and `config.json.bak` next to it holds the version before the last change. This is expected (documented in `docs/configuration.md`).

- [ ] **Step 6 (user): Edit mode**

Right-click a tile → **Edit layout**.
Expected:
- Every taskbar (each monitor) shows dashed tiles with a ×, and `+` and **Done** at the end; a monitor without tiles shows just `+` and **Done**. Clicking a tile does not open its popup; the stack stops rotating.
- Drag a tile onto the left or right part of another: it moves there. Drag one onto the middle of the stack (it lights up blue): it joins the stack. Drag a tile onto the middle of another plain tile: they become a stack.
- × on a plain tile removes it from this monitor. On the stack, wheel to a member and press ×: only that member goes.
- `+` opens "Add a tile": the removed tile is listed first ("Your tile · …"), then `media` (and other plugins), then "Clock and uptime" and "Ping a host". Pick "Ping a host": a new tile `ping-2` (or `ping` if free) appears at the end and starts pinging.
- Press Esc (or **Done**): edit mode ends on every monitor; the stack rotates again.

- [ ] **Step 7 (user, if there are two monitors): Display keys**

Enter edit mode, add a tile on the secondary monitor with `+`, press **Done**, and open `config.json`.
Expected: a new entry under `"displays"` with a key like `\\?\DISPLAY#…` holding that monitor's slots; `"*"` is unchanged; edits on the primary monitor went to `"primary"`.

- [ ] **Step 8 (user): A broken config.json is never overwritten**

In `config.json`, delete a comma between two tiles and save (the "config.json was not applied" notice appears, as before). Then right-click a tile → **Move right**.
Expected: a notice "Layout not saved" with the parse error and line/column and "config.json is unchanged."; the file still has the missing comma. Restore the comma and save: the tiles follow the file again.

- [ ] **Step 9 (user): Script settings typo**

Add `"timout": "5s"` inside the `meeting` tile's `interval` hook and save.
Expected: the meeting member shows the error look; its popup says `Hook "interval" has an unknown setting "timout"; use run, every, timeout.` Remove the typo: it recovers.

- [ ] **Step 10 (user): Clean up and record**

The user may restore `config.before-m3.json` or keep the new layout. Ask them to confirm Steps 2-9 (7 only with two monitors). Note anything that failed in `.superpowers/sdd/progress.md` for a fix round; milestone 3 is done when the user confirms.

---

## Self-review notes

- Spec coverage for milestone 3:
  - Layout and overflow: per-kind full width and 40 DIP compact width (Tasks 4, 8); the three-step reflow with pinned tiles compacted never and overflowed last (Task 4); reflow on every free-zone or slot change (Task 9: `Apply` and `SetSlots` both reflow); the `»` popup with overflowed tiles at full size and still interactive (Task 9); stacks count as one tile (Tasks 4, 6).
  - Stacks: rotate every `rotate` (default 5 s, bounded, Task 1), pause on hover, wheel, dots, urgent takeover until clicked (Tasks 5, 8); all members' runtimes run (Task 6).
  - Interaction: Move left/right, Add to stack / Pause rotation (+ Remove from stack, Resume rotation), Edit layout, Remove tile (Tasks 9, 10); edit layout with dashed tiles, drag to reorder, drop onto the middle of a stack, ×, `+` gallery of plugin tile kinds and script templates, Done or Esc, on each display's bar (Task 10).
  - Configuration: "Edit mode writes this file" with per-display keys and device ids (Tasks 1, 3, 6); hand edits still picked up by the watcher (Task 3 test).
  - Testing section: `LayoutEngine` (fits, compact, overflow, pinned, stacks, zero width: Task 4); "stack rotation and urgent takeover" (Task 5); config parsing and display resolution (Task 1).
- Folded-in deferred items: inline tile buttons through `TileCommands.InvokeAsync` (Task 8); unknown keys inside a script hook are errors (Task 11); rotate `"0s"` rejected (Task 1, from M1 Task 3 minor); install via a user-run script and agent publishes into `artifacts\` (Task 12). The Notepad / `.json` association note needs nothing (files are shown in Explorer since `6d05c4a`).
- Not in this milestone: plugin hot reload, watchdog, state cache (milestone 4); empty stacks from the gallery (the mockup's "Empty stack" button; stacks are made by dropping); a dedicated reorder preview while dragging (only the stack target is highlighted).
- Type consistency checked: `BarSlot`, `ILayoutEditor` (grows in Tasks 7 and 10), `ITileBarView` (`SetSlots` Task 6, `SetEditing` Task 10), `TileMenu.Entries` (one parameter in Task 9, two in Task 10), `BarCommand` (gains `EditLayout`, `DoneEditing` in Task 10), `FlyoutWindow.ShowPanel` (Task 8) are used with the same names and parameter types in later tasks.
- Checked while writing the plan: every code block in this plan, applied in order to a clean clone of `main` (`6d05c4a`), builds with 0 warnings after each task and passes `dotnet test TaskbarTiles.sln` with the counts given in each task (300, 329, 348, 362, 378, 387, 394, 405, 404, 408, 411, 411; 23 of them are the integration tests from milestones 1 and 2). `tools\install.ps1 -AppDir <repo>\artifacts\app -NoShortcut -NoStart` publishes successfully and `artifacts/` stays untracked. The WPF behavior (drag, popups, activation, Esc) cannot be exercised by unit tests and is covered by Task 13.
