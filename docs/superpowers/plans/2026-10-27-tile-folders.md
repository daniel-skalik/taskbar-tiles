# Taskbar Tiles — Tile Folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each tile kind becomes one folder under `%LOCALAPPDATA%\TaskbarTiles\tiles\<kind>\` (its `tile.json`, scripts and DLLs); `config.json` keeps only tile instances (`{ "kind": …, "settings": … }`) and the layout.

**Architecture:** A pure `KindLoader` scans `tiles\*\tile.json` into a `KindTable` of `TileKind` records (parsed `tile.json`, the connectors its DLLs declare in their own `PluginLoadContext`s, an `Error` for broken kinds, and file stamps). `ConfigParser` parses instances; `KindValidation` checks their kinds against the table and `ConfigStore` runs it on every load, reload and save. `HostController` turns each instance into a runtime from its kind (effective settings = kind settings with instance keys replacing them, `ITileContext.TileDirectory` = kind folder, a `BrokenKindConnector` for missing or broken kinds) and restarts a tile only when its fingerprint (kind folder + stamp + error + effective settings) changes. `KindStore` watches `tiles\` and rescans (DLLs are never loaded again after startup). The build copies `tiles\` and the media plugin into `tiles\media\` of the app; `install.ps1` refreshes the bundled kinds after swapping in the app.

**Tech Stack:** .NET 9, WPF, C# latest, xUnit 2.9, System.Text.Json (`JsonDocument`, `JsonNode`), System.Reflection.Metadata (`PEReader`, in the shared framework), MSBuild, Windows PowerShell 5.1.

Spec: `docs/superpowers/specs/2026-10-27-tile-folders-design.md`. Earlier plans: `docs/superpowers/plans/2026-10-23-milestone-3-layout.md`, `docs/superpowers/plans/2026-10-27-milestone-3-feedback.md`. The code on `main` (`823ffb1`) is the truth where it differs from those plans; every "Edit" below is written against that code (or against the result of an earlier task in this plan, when it says so).

**Branch:** before Task 1, create and switch to `feat/tile-folders` from `main`: `git switch -c feat/tile-folders main`. All commits go there. Do not merge; the user merges on request after Task 9.

## Global Constraints

- Target frameworks: host, media plugin and tests `net9.0-windows10.0.19041.0`; contracts and the script plugin `net9.0`. No new NuGet packages.
- `TaskbarTiles.Contracts` gets exactly one change: `ITileContext.TileDirectory`, a default interface member returning `ConfigDirectory` (so plugins built against the old contracts keep working).
- Start fresh: the old inline tile form `{ "connector": … }` is a config error. No migration code, no fallback.
- Error texts, copied from the spec's Errors table:
  - `Tile '<id>' uses "connector"; tiles now name a kind: "kind": "<folder>"` (the literal text `<folder>`; `<id>` is the tile id)
  - `hooks belong in tiles\<kind>\tile.json` (with the kind's name in place of `<kind>`)
  - a missing kind folder is a config notice naming the tile and the missing `tiles\<kind>` folder; a folder without `tile.json` gets the hint that `tile.json` is missing.
- A broken kind never rejects `config.json`; only mistakes in `config.json` do. The last good config stays active on a config error.
- Plugins never run on the UI thread (connector constructors run via the runtime's `Task.Run`). Kind DLLs load once, when the table is first built; never again while the app runs.
- `config.json` writes go through `IConfigWriter.Save` (whole-file rewrite, `.tmp` + `File.Replace`, `.bak`).
- WPF projects (Host, Tests) do not get `System.IO` from implicit usings: add `using System.IO;` explicitly where needed.
- Tests are xUnit in `tests/TaskbarTiles.Tests`; files and folders they create live in a `TempDir` (or under `D:\Projects\taskbar-tiles\artifacts\`, which is git-ignored). Agent writes under `C:\Users\Danko\AppData\Local` are virtualized by the Claude desktop app, so no test or check may depend on the real `%LOCALAPPDATA%\TaskbarTiles`.
- Always pass `--disable-build-servers` to `dotnet build`, `dotnet test` and `dotnet publish`, e.g. `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~KindLoaderTests"`. `dotnet build TaskbarTiles.sln --disable-build-servers` stays at 0 warnings.
- Agents never run `tools\install.ps1` (or `install.cmd`) against the real `%LOCALAPPDATA%` and never start or stop the installed `TaskbarTiles.exe`. Testing the install uses exactly `-AppDir D:\Projects\taskbar-tiles\artifacts\app -TilesDir D:\Projects\taskbar-tiles\artifacts\tiles-check -NoShortcut -NoStart`.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (use your own model name if it differs).

## File Structure

```
install.cmd                                      double-click install (new)
tiles/                                           bundled kinds in the repo (new)
  uptime/  tile.json, uptime.ps1                 (uptime.ps1 moved from scripts/)
  ping/    tile.json, ping.ps1                   (ping.ps1 moved from scripts/)
  media/   tile.json                             (the build adds the media plugin's output)
scripts/                                         deleted
build/CopyPlugins.targets                        PluginProject Kind="x" copies to tiles\x\ instead of plugins\<Name>\
tools/install.ps1                                -TilesDir; refreshes tiles\uptime, ping, media after the swap; verifies the new layout
docs/configuration.md, docs/script-connectors.md kinds, tile.json, install.cmd, Show tile folder
src/TaskbarTiles.Contracts/ITileContext.cs       TileDirectory (default: ConfigDirectory)
src/TaskbarTiles.Plugins.Script/
  CommandResolver.cs                             resolves against a base directory (the kind folder)
  ScriptConnector.cs                             uses ctx.TileDirectory
  ScriptSettings.cs                              example path in an error message
src/TaskbarTiles.Host/
  AppPaths.cs                                    TilesDir, PluginsDir; PluginRoots, ScriptsDir, SampleScriptsDir removed
  App.xaml.cs                                    KindStore, validator, kinds for the host, DLL notice
  SampleScripts.cs                               deleted
  HostController.cs                              kinds instead of templates; runtimes from kinds; fingerprints
  TaskbarTiles.Host.csproj                       copies tiles\ to the output; media plugin Kind="media"
  Config/HostConfig.cs                           TileConfig(Kind, Settings)
  Config/ConfigParser.cs                         "kind", connector/hooks errors; TrimJsonMessage internal
  Config/ConfigJson.cs                           SetTile writes { "kind" } (+ settings when not empty)
  Config/ConfigStore.cs                          validator, Recheck, new DefaultJson
  Config/KindValidation.cs                       instance kinds vs kind table (new)
  Plugins/PluginLoader.cs                        Connectors(assembly) shared; ConnectorCatalog.Instantiate
  Plugins/TileKinds.cs                           TileKind, KindDlls, KindTable, TileJson (new)
  Plugins/KindLoader.cs                          scan, rescan, DLL loading per kind folder (new)
  Plugins/KindStore.cs                           owns the table, watches tiles\ (new)
  Runtime/TileRuntime.cs                         TileRuntimeOptions.TileDirectory, TileRuntime.TileDirectory
  Runtime/TileContext.cs                         TileDirectory
  Runtime/BrokenKindConnector.cs                 tile error for missing or broken kinds (new)
  Layout/TileGallery.cs                          one entry per kind; ScriptTemplate(s) removed
  UI/TileCommands.cs                             TileCommand.ShowFolder, "Show tile folder"
  UI/ConfigNotice.cs                             DllsChangedTitle/Body
tests/TaskbarTiles.Tests/
  KindLoaderTests.cs, KindValidationTests.cs, TileKindTests.cs, KindStoreTests.cs, BundledKindsTests.cs (new)
  TestDoubles.cs                                 TestTileContext(tileDirectory), TestKinds (modified)
  ConfigParserTests.cs, ConfigJsonTests.cs, ConfigStoreTests.cs, ConfigStoreSaveTests.cs, ConfigNoticeTests.cs,
  HostControllerTests.cs, HostControllerLayoutTests.cs, TileGalleryTests.cs, TileCommandsTests.cs,
  ScriptConnectorTests.cs, ScriptConnectorIntegrationTests.cs, TileRuntimeRestartTests.cs (modified)
  SampleScriptsTests.cs                          deleted (its sample-run integration tests move to BundledKindsTests)
  TaskbarTiles.Tests.csproj                      copies tiles\ and the media plugin (tiles\media\) to the test output
```

### API changes

| Change | Why |
|---|---|
| `ITileContext` + `string TileDirectory => ConfigDirectory;` | The script connector resolves `run` and the working directory against the kind folder. |
| `TileRuntimeOptions` + `string TileDirectory { get; init; } = ""`; `TileRuntime` + `string TileDirectory` (options value, else `ConfigDirectory`); `TileContext(…, string configDirectory, string tileDirectory)` | Per-tile folder; "Show tile folder" reads it. |
| `CommandResolver(string configDirectory, …)` → `CommandResolver(string baseDirectory, …)` | Same behaviour, new base. |
| `TileConfig(string Connector, JsonElement Settings)` → `TileConfig(string Kind, JsonElement Settings)` | Instances name a kind. |
| `ConfigStore(string path, ITileLog log)` → `ConfigStore(string path, ITileLog log, Func<HostConfig, ConfigError?>? validate = null)`; + `void Recheck()` | Kind check on load, reload and save; re-check after the kind table changes. |
| `+ static ConfigError? KindValidation.Check(HostConfig config, KindTable kinds)` | Missing-kind notices. |
| `ConfigJson.SetTile` writes `{ "kind": … }` and `"settings"` only when it has keys | Picking a kind writes `"<id>": { "kind": "<kind>" }`. |
| `internal static string ConfigParser.TrimJsonMessage(string)` (was private) | `tile.json` errors use the same short JSON messages. |
| `+ static ITileConnector ConnectorCatalog.Instantiate(ConnectorDescriptor)`; `+ internal static IEnumerable<ConnectorDescriptor> PluginLoader.Connectors(Assembly, string path, ITileLog)` | Kind DLL connectors are created from their descriptor; one attribute scan for both loaders. |
| `+ Plugins/TileKinds.cs`: `TileKind`, `KindDlls`, `KindTable`, `TileJson`; `+ KindLoader { Load(string tilesDir), Rescan(KindTable previous) }`; `+ KindStore` | The kind table. |
| `HostController(…, IReadOnlyList<ScriptTemplate>? templates = null)` → `HostController(…, Func<KindTable>? kinds = null)` | Runtimes and gallery come from kinds. |
| `TileGallery.Build(HostConfig, DisplayLayout, IEnumerable<string> connectorIds, IReadOnlyList<ScriptTemplate>)` → `Build(HostConfig, DisplayLayout, KindTable)`; `ScriptTemplate`, `ScriptTemplates`, `TileGallery.ScriptConnectorId` removed (`TileKind.ScriptConnectorId` replaces the constant) | One entry per kind. |
| `TileCommand.Configure` → `TileCommand.ShowFolder` | "Show tile folder". |
| `AppPaths.PluginRoots`, `ScriptsDir`, `SampleScriptsDir` removed; `+ TilesDir`, `+ PluginsDir`; `SampleScripts` removed | Kind folders replace scripts and the user plugin root. |
| `+ ConfigNotice.DllsChangedTitle`, `+ ConfigNotice.DllsChangedBody(IReadOnlyList<string> kinds)` | Restart notice. |

### Decisions

| Decision | Why |
|---|---|
| The kind folder reaches connectors as `ITileContext.TileDirectory`, a default interface member returning `ConfigDirectory`. `ConfigDirectory` keeps meaning the data folder. | One additive Contracts change; old plugins still load; the script connector only swaps which property it reads. |
| Kind ids are folder names, looked up case-insensitively (`StringComparer.OrdinalIgnoreCase`), as Windows folders are. Tile ids stay case-sensitive. | `"kind": "Ping"` finds `tiles\ping`. |
| In a kind folder, every top-level `*.dll` that references `TaskbarTiles.Contracts` (checked with `PEReader` metadata, without loading) loads in its own `PluginLoadContext`; other DLLs are dependencies resolved on demand from the folder; a file that is not a PE image is a load failure. Connector ids are collected per folder; any id declared twice in one folder breaks the kind. | Media's folder has only its own DLL, but a user plugin may ship NuGet dependencies or native DLLs that must not be scanned for connectors. |
| A kind whose `connector` is `"script"` uses the app's built-in script connector even if a DLL in its folder declares `script`. | The spec: `"script"` means the built-in one. |
| A broken kind (`tile.json` invalid or missing `name`/`connector`, a DLL that fails to load, a duplicate id, or `connector` not declared by a DLL in the folder) stays in the table with `Error`. Its picker entry is titled with the folder name and its subtitle is the error; its tiles show the error look with the error as message (`BrokenKindConnector` calls `ReportError`, so it is not restarted with backoff). A tile whose kind is not in the table at all (possible only with the last good config after a folder was removed, or in tests) shows `There is no tiles\<kind> folder with a tile.json.` | One error path; the picker shows broken kinds so they can be found. |
| The spec's "`tile.json` missing" row and its "a folder without `tile.json` is not a kind" sentence are read together: a folder without `tile.json` is never in the table (`KindTable.FoldersWithoutTileJson` lists it), and an instance naming it gets the config notice `Tile 'x' uses kind 'k', but tiles\k has no tile.json.` | The two spec statements otherwise conflict. |
| Missing-kind notice text: `Tile 'x' uses kind 'k', but there is no tiles\k folder.` The `"connector"` and `hooks` checks are in `ConfigParser` (they need no kind table); the folder check is `KindValidation`, run by `ConfigStore` through its validator on `Load`, reload, `Save` (before and after the edit) and `Recheck`. `FakeConfigWriter` and `ConfigJson.RemoveUnplacedTiles` keep using plain `ConfigParser`. | Pure parsing stays pure; tests that do not care about kinds keep working. |
| A tile without `"kind"` (and without `"connector"`): `Tile 'x' needs a "kind": the name of its folder in tiles\.` Non-object `"settings"` in an instance is treated as `{}`, as today. In `tile.json`, `"settings"` that is not an object breaks the kind. | Matches the current parser's leniency for instances. |
| Effective settings = `TileKind.SettingsFor(instanceSettings)`: the kind's settings object with each top-level instance key replacing (or appended after) the kind's key of the same name; the result is a compact `JsonElement`. | The spec's shallow merge; what connectors see and what `TILE_SETTINGS_JSON` carries. |
| A kind's `Stamp` covers name, size and write time of its top-level `tile.json` and script files (`.ps1`, `.psm1`, `.py`, `.cmd`, `.bat`); `DllStamp` covers its top-level `*.dll`. Files a script writes next to itself (logs, caches) change neither, so they never restart it. | "A saved `tile.json` or script change restarts only the tiles of that kind", without restart loops. |
| A runtime is kept while its fingerprint `kindFolder \n Stamp \n Error \n effectiveSettings` is unchanged (missing kinds: `missing \n kind \n instanceSettings`). The watcher only rescans and calls `HostController.ConfigChanged()`; `Sync` restarts exactly the tiles whose fingerprint changed. | One restart rule for config edits and kind edits. |
| `KindStore` watches `tiles\` (created if missing) with subfolders, debounced 300 ms, and raises `Changed` only when a kind's `Stamp`/`DllStamp`, the set of kinds or the set of folders without `tile.json` changed. A rescan never loads a DLL: a known kind keeps the DLLs it loaded at startup (`TileKind.NeedsRestart` when they differ on disk now); a kind folder that appears with DLLs is broken with `The DLLs in tiles\<kind> were added while Taskbar Tiles was running; restart it to load them.` | .NET cannot unload a DLL safely; the spec asks for a notice. |
| After a kind change the app calls `ConfigStore.Recheck()` (applies a config that was rejected for a missing kind, or rejects one naming a removed kind; raises `Changed`/`ReloadFailed` only when the outcome changed), then `HostController.ConfigChanged()`, then shows `Restart Taskbar Tiles` once per kind newly in `KindTable.DllsChanged` (queued after any notice-closing `Changed` handler). Clicking it selects that kind's folder in Explorer. | New kind folders become usable without editing `config.json` again. |
| The picker lists existing instances this display does not show (subtitle `Your tile · <kind name>`), then every kind (broken ones too) ordered by `name` (current culture, case-insensitive), then by id. Picking a kind writes `"<id>": { "kind": "<kind>" }` with `TileGallery.UniqueId(kind, taken)`. | Spec; ids are the kind id, `-2`, `-3`, … |
| **Show tile folder** passes the tile's kind folder to the existing reveal callback (`explorer.exe /select,"<folder>"` selects the folder in its parent). When the folder does not exist it reveals `config.json` instead. | Same mechanism as **Show config file**; never opens Explorer on a missing path. |
| The media plugin is a `PluginProject` with `Kind="media"`: `CopyPlugins.targets` copies its output to `tiles\media\` (build and publish) instead of `plugins\TaskbarTiles.Plugins.Media\`. The host copies the repo's `tiles\**` to `tiles\` next to the exe (build and publish). The installed app's own `app\tiles\` is only the source `install.ps1` copies from; the host reads `%LOCALAPPDATA%\TaskbarTiles\tiles`. | Spec layout; `app\plugins` keeps only the script connector. |
| Nothing copies kinds on first run (`SampleScripts` is deleted). A dev run without an install shows the default config's missing-kind notice until `tiles\` is populated. | The spec drops the first-run copy; `install.ps1` populates `tiles\` before it starts the app. |
| The default `config.json` (written only when none exists): `"spotify": { "kind": "media" }` (pinned) and `"uptime": { "kind": "uptime" }` on `primary`, `"*"` empty. `tiles\media\tile.json` defaults `"app": "Spotify"`. | Same first-run tiles as today. |
| `install.ps1` refreshes `uptime`, `ping`, `media` (delete, then copy from `<AppDir>\tiles\<kind>`) right after the app swap and before the shortcut and start. A failure stops the install with an error naming the kind (the new app is installed but not started). | The spec; the app is stopped during the swap, and `install.cmd` keeps its window open on failure. |

---

### Task 1: Kind table: tile.json parsing and kind DLLs

**Files:**
- Create: `src/TaskbarTiles.Host/Plugins/TileKinds.cs`, `src/TaskbarTiles.Host/Plugins/KindLoader.cs`
- Modify: `src/TaskbarTiles.Host/Plugins/PluginLoader.cs`, `src/TaskbarTiles.Host/Config/ConfigParser.cs`
- Test: `tests/TaskbarTiles.Tests/KindLoaderTests.cs` (new)

**Interfaces:**
- Consumes: `PluginLoadContext(string mainAssemblyPath)`, `ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath)`, `ConfigParser` (its JSON message trimming), test output `plugins\TaskbarTiles.Tests.SamplePlugin\` (connectors `sample` width 150, `badwidth`)
- Produces (namespace `TaskbarTiles.Host.Plugins`):
  - `sealed record KindDlls(IReadOnlyDictionary<string, ConnectorDescriptor> Connectors, string? Error, string Stamp)` with `static KindDlls None`
  - `sealed record TileKind(string Id, string Directory, string Name, string Description, string Connector, JsonElement Settings, KindDlls Dlls, string? Error, string Stamp, string DllStamp)` with `const string ScriptConnectorId = "script"`, `ConnectorDescriptor? Plugin`, `bool NeedsRestart`
  - `sealed record KindTable(string TilesDir, IReadOnlyDictionary<string, TileKind> Kinds, IReadOnlySet<string> FoldersWithoutTileJson)` with `static KindTable Empty`, `TileKind? Find(string id)`, `IReadOnlyList<string> DllsChanged`
  - `sealed record TileJson(string Name, string Description, string Connector, JsonElement Settings, string? Error)` with `static TileJson Read(string dir, string id)`, `static TileJson Parse(string text, string shownPath)`
  - `sealed class KindLoader(ITileLog log)`: `const string TileJsonName = "tile.json"`, `KindTable Load(string tilesDir)`, `KindTable Rescan(KindTable previous)`, `internal static bool ReferencesContracts(string path)`
  - `static ITileConnector ConnectorCatalog.Instantiate(ConnectorDescriptor d)`; `internal static IEnumerable<ConnectorDescriptor> PluginLoader.Connectors(Assembly assembly, string path, ITileLog log)`
  - `internal static string ConfigParser.TrimJsonMessage(string message)`

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/KindLoaderTests.cs`:
```csharp
using System.IO;
using System.Runtime.Loader;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindLoaderTests
{
    static readonly string SamplePluginDir = Path.Combine(AppContext.BaseDirectory, "plugins", "TaskbarTiles.Tests.SamplePlugin");

    const string PingJson = """
    {
      // comments are allowed
      "name": "Ping a host",
      "description": "Pings a host every 15 s",
      "connector": "script",
      "settings": { "host": "1.1.1.1", "hooks": { "click": "ping.ps1" } },
      "unknown": "ignored",
    }
    """;

    /// <summary>Creates tiles\&lt;id&gt; (with tile.json unless null) and returns its path.</summary>
    static string Kind(TempDir tiles, string id, string? tileJson)
    {
        var dir = Path.Combine(tiles.Path, id);
        Directory.CreateDirectory(dir);
        if (tileJson is not null) File.WriteAllText(Path.Combine(dir, "tile.json"), tileJson);
        return dir;
    }

    static void CopySamplePlugin(string kindDir)
    {
        foreach (var file in Directory.GetFiles(SamplePluginDir))
            File.Copy(file, Path.Combine(kindDir, Path.GetFileName(file)));
    }

    static string SampleKindJson(string connector) => $$"""{ "name": "Sample", "connector": "{{connector}}" }""";

    [Fact]
    public void Scans_a_script_kind()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "ping", PingJson);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        var kind = table.Kinds["ping"];
        Assert.Equal("ping", kind.Id);
        Assert.Equal(dir, kind.Directory);
        Assert.Equal("Ping a host", kind.Name);
        Assert.Equal("Pings a host every 15 s", kind.Description);
        Assert.Equal(TileKind.ScriptConnectorId, kind.Connector);
        Assert.Equal("1.1.1.1", kind.Settings.GetProperty("host").GetString());
        Assert.Null(kind.Error);
        Assert.Null(kind.Plugin);
        Assert.Same(kind, table.Find("PING")); // folder names are case-insensitive
        Assert.Equal(tiles.Path, table.TilesDir);
    }

    [Fact]
    public void Description_and_settings_are_optional()
    {
        using var tiles = new TempDir();
        Kind(tiles, "uptime", """{ "name": "Uptime", "connector": "script" }""");

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["uptime"];

        Assert.Equal("", kind.Description);
        Assert.Equal("{}", kind.Settings.GetRawText());
        Assert.Null(kind.Error);
    }

    [Fact]
    public void A_folder_without_tile_json_is_not_a_kind()
    {
        using var tiles = new TempDir();
        Kind(tiles, "notes", null);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        Assert.Empty(table.Kinds);
        Assert.Contains("notes", table.FoldersWithoutTileJson);
    }

    [Fact]
    public void A_missing_tiles_folder_gives_an_empty_table() =>
        Assert.Empty(new KindLoader(new CollectingLog()).Load(Path.Combine(Path.GetTempPath(), "tt-no-such-" + Guid.NewGuid().ToString("N"))).Kinds);

    [Fact]
    public void Invalid_json_keeps_the_kind_as_broken_naming_the_file_and_line_and_others_still_load()
    {
        using var tiles = new TempDir();
        Kind(tiles, "bad", "{\n  \"name\": \"Bad\",\n  \"connector\": \n}");
        Kind(tiles, "ping", PingJson);
        var log = new CollectingLog();

        var table = new KindLoader(log).Load(tiles.Path);

        var bad = table.Kinds["bad"];
        Assert.StartsWith(@"tiles\bad\tile.json is not valid JSON: ", bad.Error);
        Assert.EndsWith("(line 4)", bad.Error);
        Assert.Equal("bad", bad.Name);          // the picker shows the folder name
        Assert.Equal(bad.Error, bad.Description); // and the error as subtitle
        Assert.Null(table.Kinds["ping"].Error);
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR") && m.Contains("bad"));
    }

    [Theory]
    [InlineData("""{ "connector": "script" }""", "\"name\"")]
    [InlineData("""{ "name": "X" }""", "\"connector\"")]
    [InlineData("""{ "name": "  ", "connector": "script" }""", "\"name\"")]
    [InlineData("""{ "name": "X", "connector": "script", "settings": 5 }""", "\"settings\" must be an object")]
    [InlineData("""[1]""", "must be a JSON object")]
    public void Tile_json_without_name_or_connector_is_broken(string json, string expected)
    {
        using var tiles = new TempDir();
        Kind(tiles, "x", json);

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Contains(@"tiles\x\tile.json", kind.Error);
        Assert.Contains(expected, kind.Error);
    }

    [Fact]
    public void A_dll_kind_resolves_its_connector_from_its_own_folder()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "sample", SampleKindJson("sample"));
        CopySamplePlugin(dir);

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["sample"];

        Assert.Null(kind.Error);
        Assert.Equal("sample", kind.Plugin!.Id);
        Assert.Equal(150, kind.Plugin.Width);
        Assert.StartsWith(dir, kind.Plugin.SourcePath);
        Assert.IsAssignableFrom<ITileConnector>(ConnectorCatalog.Instantiate(kind.Plugin));
        Assert.Contains("badwidth", kind.Dlls.Connectors.Keys); // every id the folder declares, private to it
        Assert.False(kind.NeedsRestart);
    }

    [Fact]
    public void Two_kinds_with_the_same_connector_id_do_not_clash()
    {
        using var tiles = new TempDir();
        CopySamplePlugin(Kind(tiles, "one", SampleKindJson("sample")));
        CopySamplePlugin(Kind(tiles, "two", SampleKindJson("sample")));

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        var one = table.Kinds["one"].Plugin!;
        var two = table.Kinds["two"].Plugin!;
        Assert.NotEqual(one.SourcePath, two.SourcePath);
        Assert.NotSame(one.Type, two.Type);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(one.Type.Assembly), AssemblyLoadContext.GetLoadContext(two.Type.Assembly));
    }

    [Fact]
    public void A_connector_no_dll_in_the_folder_declares_breaks_the_kind()
    {
        using var tiles = new TempDir();
        CopySamplePlugin(Kind(tiles, "x", SampleKindJson("nope")));

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Equal(@"tiles\x\tile.json names connector 'nope', but no DLL in tiles\x declares it.", kind.Error);
        Assert.Null(kind.Plugin);
    }

    [Fact]
    public void A_dll_that_fails_to_load_breaks_only_its_kind()
    {
        using var tiles = new TempDir();
        File.WriteAllText(Path.Combine(Kind(tiles, "x", SampleKindJson("sample")), "broken.dll"), "not a dll");
        Kind(tiles, "ping", PingJson);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        Assert.StartsWith(@"Could not load broken.dll from tiles\x: ", table.Kinds["x"].Error);
        Assert.Null(table.Kinds["ping"].Error);
    }

    [Fact]
    public void Two_dlls_in_one_folder_declaring_the_same_id_break_the_kind()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "x", SampleKindJson("sample"));
        CopySamplePlugin(dir);
        File.Copy(Path.Combine(dir, "TaskbarTiles.Tests.SamplePlugin.dll"), Path.Combine(dir, "Copy.dll"));

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Contains(@"in tiles\x both declare connector", kind.Error);
    }

    [Fact]
    public void Only_dlls_that_reference_the_contracts_are_connector_dlls()
    {
        Assert.True(KindLoader.ReferencesContracts(Path.Combine(SamplePluginDir, "TaskbarTiles.Tests.SamplePlugin.dll")));
        Assert.False(KindLoader.ReferencesContracts(typeof(ITileConnector).Assembly.Location)); // the contracts themselves
    }

    [Fact]
    public void Rescan_rereads_tile_json_and_scripts_but_not_data_files()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "ping", PingJson);
        File.WriteAllText(Path.Combine(dir, "ping.ps1"), "'{}'");
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);

        File.WriteAllText(Path.Combine(dir, "cache.txt"), "written by the script");
        var afterData = loader.Rescan(first);
        File.WriteAllText(Path.Combine(dir, "ping.ps1"), "'{ }' # edited");
        var afterScript = loader.Rescan(afterData);
        File.WriteAllText(Path.Combine(dir, "tile.json"), """{ "name": "Ping 2", "connector": "script" }""");
        var afterJson = loader.Rescan(afterScript);

        Assert.Equal(first.Kinds["ping"].Stamp, afterData.Kinds["ping"].Stamp);
        Assert.NotEqual(afterData.Kinds["ping"].Stamp, afterScript.Kinds["ping"].Stamp);
        Assert.Equal("Ping 2", afterJson.Kinds["ping"].Name);
        Assert.NotEqual(afterScript.Kinds["ping"].Stamp, afterJson.Kinds["ping"].Stamp);
    }

    [Fact]
    public void Rescan_keeps_the_loaded_dlls_and_flags_a_kind_whose_dlls_changed()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "sample", SampleKindJson("sample"));
        CopySamplePlugin(dir);
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);

        File.WriteAllText(Path.Combine(dir, "extra.dll"), "added while running");
        var next = loader.Rescan(first);

        var kind = next.Kinds["sample"];
        Assert.Same(first.Kinds["sample"].Dlls, kind.Dlls); // nothing loaded again, extra.dll not even read
        Assert.Null(kind.Error);
        Assert.True(kind.NeedsRestart);
        Assert.Equal(new[] { "sample" }, next.DllsChanged);
        Assert.Empty(first.DllsChanged);
    }

    [Fact]
    public void A_kind_folder_that_appears_with_dlls_is_broken_until_restart()
    {
        using var tiles = new TempDir();
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);
        CopySamplePlugin(Kind(tiles, "late", SampleKindJson("sample")));
        Kind(tiles, "script-only", """{ "name": "S", "connector": "script" }""");

        var next = loader.Rescan(first);

        Assert.Equal(@"The DLLs in tiles\late were added while Taskbar Tiles was running; restart it to load them.", next.Kinds["late"].Error);
        Assert.True(next.Kinds["late"].NeedsRestart);
        Assert.Null(next.Kinds["script-only"].Error); // no DLLs: usable at once
        Assert.False(next.Kinds["script-only"].NeedsRestart);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~KindLoaderTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'KindLoader' could not be found` (and `TileKind`).

- [ ] **Step 3: Write the implementation**

**Edit** `src/TaskbarTiles.Host/Config/ConfigParser.cs`, find:
```csharp
    static string TrimJsonMessage(string message)
```
Replace with:
```csharp
    /// <summary>System.Text.Json's message without its " Path: …" / " LineNumber: …" tail. Also used for tile.json.</summary>
    internal static string TrimJsonMessage(string message)
```

**Replace the whole file** `src/TaskbarTiles.Host/Plugins/PluginLoader.cs` with:
```csharp
using System.IO;
using System.Reflection;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

public sealed record ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath);

public sealed class ConnectorCatalog(IReadOnlyDictionary<string, ConnectorDescriptor> connectors)
{
    public static ConnectorCatalog Empty { get; } = new(new Dictionary<string, ConnectorDescriptor>());

    public IReadOnlyDictionary<string, ConnectorDescriptor> Connectors => connectors;

    /// <summary>Runs the plugin's constructor; its exception propagates unwrapped.</summary>
    public ITileConnector? Create(string id) => connectors.TryGetValue(id, out var d) ? Instantiate(d) : null;

    /// <summary>Runs the plugin's constructor; its exception propagates unwrapped.</summary>
    public static ITileConnector Instantiate(ConnectorDescriptor d) =>
        (ITileConnector)Activator.CreateInstance(d.Type,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions, binder: null, args: null, culture: null)!;
}

/// <summary>Loads plugins laid out as &lt;root&gt;\&lt;Name&gt;\&lt;Name&gt;.dll. Earlier roots win on duplicate ids.</summary>
public sealed class PluginLoader(ITileLog log)
{
    public const double DefaultTileWidth = 132;

    public ConnectorCatalog LoadFrom(IEnumerable<string> roots)
    {
        var map = new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal);

        foreach (var root in roots.Where(Directory.Exists))
        foreach (var dir in Directory.GetDirectories(root))
        {
            var main = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (!File.Exists(main))
            {
                log.Warn($"Plugin folder {dir} has no {Path.GetFileName(main)}; skipped.");
                continue;
            }

            try
            {
                var assembly = new PluginLoadContext(main).LoadFromAssemblyPath(main);
                foreach (var connector in Connectors(assembly, main, log))
                {
                    if (map.TryGetValue(connector.Id, out var existing))
                    {
                        log.Warn($"Connector '{connector.Id}' in {main} ignored; already provided by {existing.SourcePath}.");
                        continue;
                    }
                    map[connector.Id] = connector;
                    log.Info($"Loaded connector '{connector.Id}' from {main}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load plugin {main}", ex);
            }
        }

        return new ConnectorCatalog(map);
    }

    /// <summary>The [TileConnector] types an assembly exports; an invalid width is logged and replaced by the default.</summary>
    internal static IEnumerable<ConnectorDescriptor> Connectors(Assembly assembly, string path, ITileLog log)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(ITileConnector).IsAssignableFrom(type)) continue;
            var attr = type.GetCustomAttribute<TileConnectorAttribute>();
            if (attr is null) continue;

            var width = attr.Width;
            if (!double.IsFinite(width) || width <= 0)
            {
                log.Warn($"Connector '{attr.Id}' in {path} declares invalid width {width}; using {DefaultTileWidth}.");
                width = DefaultTileWidth;
            }
            yield return new ConnectorDescriptor(attr.Id, type, width, path);
        }
    }
}
```

**Create** `src/TaskbarTiles.Host/Plugins/TileKinds.cs`:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// The connectors the DLLs of one kind folder declare (ids private to that folder), why loading them failed, and the
/// stamp of the DLL files that were loaded.
/// </summary>
public sealed record KindDlls(IReadOnlyDictionary<string, ConnectorDescriptor> Connectors, string? Error, string Stamp)
{
    public static KindDlls None { get; } = new(new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal), null, "");
}

/// <summary>
/// One folder under tiles\: a tile kind. Id is the folder name. Error is set when the kind is broken (bad tile.json, a DLL
/// that does not load, a connector nobody in the folder declares); its tiles then show that error and other kinds keep
/// working. Stamp changes when tile.json or a script in the folder changes; DllStamp when a DLL on disk does.
/// </summary>
public sealed record TileKind(
    string Id,
    string Directory,
    string Name,
    string Description,
    string Connector,
    JsonElement Settings,
    KindDlls Dlls,
    string? Error,
    string Stamp,
    string DllStamp)
{
    /// <summary>The connector built into the app; script kinds only need it to run their scripts.</summary>
    public const string ScriptConnectorId = "script";

    /// <summary>The connector a DLL in this folder declares for <see cref="Connector"/>; null for script kinds and broken kinds.</summary>
    public ConnectorDescriptor? Plugin =>
        Error is null && Connector != ScriptConnectorId ? Dlls.Connectors.GetValueOrDefault(Connector) : null;

    /// <summary>The DLLs on disk differ from the ones loaded at startup; only an app restart loads them.</summary>
    public bool NeedsRestart => DllStamp != Dlls.Stamp;
}

/// <summary>Every kind in tiles\, by folder name (case-insensitive), and the folders that have no tile.json.</summary>
public sealed record KindTable(string TilesDir, IReadOnlyDictionary<string, TileKind> Kinds, IReadOnlySet<string> FoldersWithoutTileJson)
{
    public static KindTable Empty { get; } = new("",
        new Dictionary<string, TileKind>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public TileKind? Find(string id) => Kinds.GetValueOrDefault(id);

    /// <summary>Kinds whose DLLs changed on disk since startup, by id.</summary>
    public IReadOnlyList<string> DllsChanged =>
        Kinds.Values.Where(k => k.NeedsRestart).Select(k => k.Id).Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What a kind's tile.json says. Error names the file (and the line for JSON errors) when it cannot be used.</summary>
public sealed record TileJson(string Name, string Description, string Connector, JsonElement Settings, string? Error)
{
    static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads dir\tile.json; messages call it tiles\&lt;id&gt;\tile.json. Never throws.</summary>
    public static TileJson Read(string dir, string id)
    {
        var shown = $@"tiles\{id}\{KindLoader.TileJsonName}";
        for (var attempt = 1; ; attempt++)
        {
            try { return Parse(File.ReadAllText(Path.Combine(dir, KindLoader.TileJsonName)), shown); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); } // an editor may hold the file while saving
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Broken($"Could not read {shown}: {ex.Message}"); }
        }
    }

    public static TileJson Parse(string text, string shownPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            return Broken($"{shownPath} is not valid JSON: {ConfigParser.TrimJsonMessage(ex.Message)} (line {ex.LineNumber + 1})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Broken($"{shownPath} must be a JSON object.");
            if (Text(root, "name") is not { } name) return Broken($"{shownPath} needs a \"name\", shown in the + picker.");
            if (Text(root, "connector") is not { } connector)
                return Broken($"{shownPath} needs a \"connector\": \"script\" or a connector id declared by a DLL in its folder.");

            JsonElement settings;
            if (!root.TryGetProperty("settings", out var s)) settings = EmptyObject();
            else if (s.ValueKind == JsonValueKind.Object) settings = s.Clone();
            else return Broken($"{shownPath}: \"settings\" must be an object.");

            return new TileJson(name, Text(root, "description") ?? "", connector, settings, null);
        }
    }

    static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    static TileJson Broken(string error) => new("", "", "", EmptyObject(), error);

    static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }
}
```

**Create** `src/TaskbarTiles.Host/Plugins/KindLoader.cs`:
```csharp
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// Builds the kind table from tiles\*\tile.json. Every top-level DLL of a kind folder that references the contracts loads
/// in its own PluginLoadContext, and the connector ids it declares belong to that kind only. A bad kind never stops the
/// scan: it is kept with an Error and logged, and the other kinds load.
/// </summary>
public sealed class KindLoader(ITileLog log)
{
    public const string TileJsonName = "tile.json";

    /// <summary>Files whose change restarts a kind's tiles, besides tile.json. Data a script writes next to itself does not.</summary>
    static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".py", ".cmd", ".bat"];

    static readonly string ContractsName = typeof(ITileConnector).Assembly.GetName().Name!;

    /// <summary>The first scan: reads every tile.json and loads every kind's DLLs.</summary>
    public KindTable Load(string tilesDir) => Scan(tilesDir, previous: null);

    /// <summary>
    /// A later scan: reads every tile.json again but never loads a DLL (.NET cannot unload one safely). A known kind keeps
    /// the DLLs it loaded at startup even when they changed on disk (<see cref="TileKind.NeedsRestart"/>); a kind folder
    /// that appeared with DLLs is broken until the app restarts.
    /// </summary>
    public KindTable Rescan(KindTable previous) => Scan(previous.TilesDir, previous);

    KindTable Scan(string tilesDir, KindTable? previous)
    {
        var kinds = new Dictionary<string, TileKind>(StringComparer.OrdinalIgnoreCase);
        var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] dirs;
        try
        {
            dirs = Directory.Exists(tilesDir) ? Directory.GetDirectories(tilesDir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Could not list {tilesDir}", ex);
            dirs = [];
        }

        foreach (var dir in dirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(dir);
            try
            {
                if (!File.Exists(Path.Combine(dir, TileJsonName)))
                {
                    bare.Add(id);
                    continue;
                }
                var kind = LoadKind(id, dir, previous);
                if (kind.Error is not null) log.Error($"Tile kind '{id}' is broken: {kind.Error}");
                else if (previous is null) log.Info($"Loaded tile kind '{id}' ({kind.Connector}) from {dir}");
                kinds[id] = kind;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Could not read tile kind folder {dir}", ex); // deleted or locked mid-scan; the next scan sees it
            }
        }

        return new KindTable(tilesDir, kinds, bare);
    }

    TileKind LoadKind(string id, string dir, KindTable? previous)
    {
        var json = TileJson.Read(dir, id);
        var dllStamp = Stamp(dir, path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var dlls = previous is null ? LoadDlls(id, dir, dllStamp)
            : previous.Find(id) is { } before ? before.Dlls
            : dllStamp.Length == 0 ? KindDlls.None
            : new KindDlls(KindDlls.None.Connectors,
                $@"The DLLs in tiles\{id} were added while Taskbar Tiles was running; restart it to load them.", Stamp: "");

        return new TileKind(id, dir,
            Name: json.Error is null ? json.Name : id,
            Description: json.Error ?? json.Description,
            json.Connector, json.Settings, dlls, ErrorOf(id, json, dlls), Stamp(dir, IsWatched), dllStamp);
    }

    static string? ErrorOf(string id, TileJson json, KindDlls dlls)
    {
        if (json.Error is not null) return json.Error;
        if (dlls.Error is not null) return dlls.Error;
        if (json.Connector == TileKind.ScriptConnectorId || dlls.Connectors.ContainsKey(json.Connector)) return null;
        return $@"tiles\{id}\tile.json names connector '{json.Connector}', but no DLL in tiles\{id} declares it.";
    }

    /// <summary>Loads the folder's connector DLLs; the first failure or duplicate id ends the load with an Error.</summary>
    KindDlls LoadDlls(string id, string dir, string stamp)
    {
        var connectors = new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            try
            {
                if (!ReferencesContracts(path)) continue; // a dependency or a native DLL: the plugin loads it when it needs it
                var assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
                foreach (var connector in PluginLoader.Connectors(assembly, path, log))
                {
                    if (connectors.TryGetValue(connector.Id, out var other))
                        return new KindDlls(connectors,
                            $@"{Path.GetFileName(other.SourcePath)} and {file} in tiles\{id} both declare connector '{connector.Id}'.", stamp);
                    connectors[connector.Id] = connector;
                    log.Info($"Loaded connector '{connector.Id}' for tile kind '{id}' from {path}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load {path}", ex);
                return new KindDlls(connectors, $@"Could not load {file} from tiles\{id}: {ex.Message}", stamp);
            }
        }
        return new KindDlls(connectors, null, stamp);
    }

    /// <summary>True for a .NET assembly that references the contracts (without loading it); false for other assemblies and native DLLs.</summary>
    internal static bool ReferencesContracts(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return false; // throws BadImageFormatException for a file that is not a PE image
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences.Any(h => reader.GetString(reader.GetAssemblyReference(h).Name) == ContractsName);
    }

    static bool IsWatched(string path) =>
        Path.GetFileName(path).Equals(TileJsonName, StringComparison.OrdinalIgnoreCase)
        || ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Name, size and write time of the folder's top-level files that match; changes when any of them does.</summary>
    static string Stamp(string dir, Func<string, bool> include) =>
        string.Join("|", Directory.GetFiles(dir).Where(include).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var info = new FileInfo(path);
            return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~KindLoaderTests|FullyQualifiedName~PluginLoaderTests|FullyQualifiedName~ConfigParserTests"`
Expected: PASS, `Failed: 0`.

Then the whole suite: `dotnet test --disable-build-servers TaskbarTiles.sln`
Expected: `Failed: 0`. And `dotnet build TaskbarTiles.sln --disable-build-servers`: `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Plugins/TileKinds.cs src/TaskbarTiles.Host/Plugins/KindLoader.cs src/TaskbarTiles.Host/Plugins/PluginLoader.cs src/TaskbarTiles.Host/Config/ConfigParser.cs tests/TaskbarTiles.Tests/KindLoaderTests.cs
git commit -m "feat(kinds): kind table from tiles\*\tile.json with per-folder connector DLLs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Scripts run from the tile folder (ITileContext.TileDirectory)

**Files:**
- Modify: `src/TaskbarTiles.Contracts/ITileContext.cs`, `src/TaskbarTiles.Host/Runtime/TileRuntime.cs`, `src/TaskbarTiles.Host/Runtime/TileContext.cs`, `src/TaskbarTiles.Plugins.Script/CommandResolver.cs`, `src/TaskbarTiles.Plugins.Script/ScriptConnector.cs`, `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`
- Test: `tests/TaskbarTiles.Tests/TestDoubles.cs`, `tests/TaskbarTiles.Tests/ScriptConnectorTests.cs`, `tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs`, `tests/TaskbarTiles.Tests/TileRuntimeRestartTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1
- Produces:
  - `ITileContext.TileDirectory` (`string TileDirectory => ConfigDirectory;`)
  - `TileRuntimeOptions.TileDirectory { get; init; } = ""`; `TileRuntime.TileDirectory` (the option, or `ConfigDirectory` when it is empty)
  - `TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory, string tileDirectory)`
  - `CommandResolver(string baseDirectory, string powerShell, string python = "python")`
  - `TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null, string? tileDirectory = null)` with `TileDirectory => tileDirectory ?? ConfigDirectory`

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TestDoubles.cs`, find:
```csharp
public sealed class TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null) : ITileContext
{
    readonly List<TileState> _updates = [];
    readonly List<(string Message, string Details)> _errors = [];

    public string TileId => tileId;
    public System.Text.Json.JsonElement Settings { get; } = System.Text.Json.JsonDocument.Parse(settingsJson).RootElement.Clone();
    public string ConfigDirectory { get; } = configDirectory ?? System.IO.Path.GetTempPath();
```
Replace with:
```csharp
public sealed class TestTileContext(string tileId = "t1", string settingsJson = "{}", string? configDirectory = null, string? tileDirectory = null) : ITileContext
{
    readonly List<TileState> _updates = [];
    readonly List<(string Message, string Details)> _errors = [];

    public string TileId => tileId;
    public System.Text.Json.JsonElement Settings { get; } = System.Text.Json.JsonDocument.Parse(settingsJson).RootElement.Clone();
    public string ConfigDirectory { get; } = configDirectory ?? System.IO.Path.GetTempPath();
    public string TileDirectory => tileDirectory ?? ConfigDirectory;
```

**Edit** `tests/TaskbarTiles.Tests/ScriptConnectorTests.cs`, find:
```csharp
    [Fact]
    public async Task Startup_runs_before_the_first_interval_run()
```
Replace with:
```csharp
    [Fact]
    public async Task Run_paths_and_the_working_directory_resolve_against_the_tile_folder()
    {
        var runner = new FakeRunner();
        var ctx = new TestTileContext(settingsJson: """{ "hooks": { "click": "ping.ps1" } }""",
            configDirectory: @"C:\data", tileDirectory: @"C:\data\tiles\ping");
        await using var c = Create(runner);
        await c.StartAsync(ctx, CancellationToken.None);

        await c.OnActionAsync(TileActions.Click, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Contains("\"C:\\data\\tiles\\ping\\ping.ps1\"", call.Command.Arguments);
        Assert.Equal(@"C:\data\tiles\ping", call.Command.WorkingDirectory);
    }

    [Fact]
    public async Task Startup_runs_before_the_first_interval_run()
```

**Edit** `tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs`, find:
```csharp
    [Fact]
    public async Task Settings_reach_the_script()
```
Replace with:
```csharp
    [Fact]
    public async Task Scripts_run_with_the_tile_folder_as_working_directory()
    {
        using var data = new TempDir();
        ScriptFiles.Write(_dir, "where.ps1", "@{ title = (Get-Location).Path } | ConvertTo-Json -Compress");
        var ctx = new TestTileContext("t1", """{ "hooks": { "startup": "where.ps1" } }""", data.Path, _dir.Path);
        await using var c = new ScriptConnector();

        await c.StartAsync(ctx, CancellationToken.None);

        await Eventually.Holds(() => ctx.Updates.Count == 1, Slow);
        Assert.Equal(_dir.Path, ctx.Updates[0].Title, ignoreCase: true);
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public async Task Settings_reach_the_script()
```

**Edit** `tests/TaskbarTiles.Tests/TileRuntimeRestartTests.cs`, find:
```csharp
    static TileRuntime Create(Func<ITileConnector> factory, IUiDispatcher? ui = null, BackoffSchedule? backoff = null,
        CollectingLog? log = null, string configDirectory = "") =>
        new("tile1", "fake", 132, factory, NoSettings, ui ?? new ImmediateDispatcher(), log ?? new CollectingLog(),
            new TileRuntimeOptions { Backoff = backoff ?? Never, ConfigDirectory = configDirectory });
```
Replace with:
```csharp
    static TileRuntime Create(Func<ITileConnector> factory, IUiDispatcher? ui = null, BackoffSchedule? backoff = null,
        CollectingLog? log = null, string configDirectory = "", string tileDirectory = "") =>
        new("tile1", "fake", 132, factory, NoSettings, ui ?? new ImmediateDispatcher(), log ?? new CollectingLog(),
            new TileRuntimeOptions { Backoff = backoff ?? Never, ConfigDirectory = configDirectory, TileDirectory = tileDirectory });
```
In the same file, find:
```csharp
    [Fact]
    public async Task Throwing_resumed_handler_does_not_skip_the_others()
```
Replace with:
```csharp
    [Fact]
    public async Task Context_exposes_the_tile_directory_or_falls_back_to_the_config_directory()
    {
        var withFolder = new FakeConnector();
        var without = new FakeConnector();
        var a = Create(() => withFolder, configDirectory: @"C:\cfg", tileDirectory: @"C:\cfg\tiles\ping");
        var b = Create(() => without, configDirectory: @"C:\cfg");

        await a.StartAsync();
        await b.StartAsync();

        Assert.Equal(@"C:\cfg\tiles\ping", withFolder.Context!.TileDirectory);
        Assert.Equal(@"C:\cfg\tiles\ping", a.TileDirectory);
        Assert.Equal(@"C:\cfg", without.Context!.TileDirectory);
        Assert.Equal(@"C:\cfg", b.TileDirectory);
    }

    [Fact]
    public async Task Throwing_resumed_handler_does_not_skip_the_others()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ScriptConnector|FullyQualifiedName~TileRuntimeRestartTests"`
Expected: build FAILS with `CS0117: 'TileRuntimeOptions' does not contain a definition for 'TileDirectory'` (and `'TileRuntime' does not contain a definition for 'TileDirectory'`).

- [ ] **Step 3: Write the implementation**

**Edit** `src/TaskbarTiles.Contracts/ITileContext.cs`, find:
```csharp
    string ConfigDirectory { get; }
```
Replace with:
```csharp
    string ConfigDirectory { get; }

    /// <summary>
    /// The tile's kind folder (normally %LOCALAPPDATA%\TaskbarTiles\tiles\&lt;kind&gt;), which holds its tile.json,
    /// scripts and DLLs. Resolve the tile's own files against it. Hosts that predate tile folders return ConfigDirectory.
    /// </summary>
    string TileDirectory => ConfigDirectory;
```

**Edit** `src/TaskbarTiles.Host/Runtime/TileRuntime.cs`, find:
```csharp
    /// <summary>Handed to connectors as ITileContext.ConfigDirectory.</summary>
    public string ConfigDirectory { get; init; } = "";
}
```
Replace with:
```csharp
    /// <summary>Handed to connectors as ITileContext.ConfigDirectory.</summary>
    public string ConfigDirectory { get; init; } = "";

    /// <summary>The tile's kind folder, handed to connectors as ITileContext.TileDirectory. Empty: ConfigDirectory.</summary>
    public string TileDirectory { get; init; } = "";
}
```
In the same file, find:
```csharp
    public string ConnectorId { get; }
    public double Width { get; }
```
Replace with:
```csharp
    public string ConnectorId { get; }
    public double Width { get; }

    /// <summary>The tile's kind folder ("Show tile folder"); ConfigDirectory when the options name none.</summary>
    public string TileDirectory => _options.TileDirectory.Length > 0 ? _options.TileDirectory : _options.ConfigDirectory;
```
In the same file, find:
```csharp
            var context = new TileContext(this, generation, _settings, _log, _options.ConfigDirectory);
```
Replace with:
```csharp
            var context = new TileContext(this, generation, _settings, _log, _options.ConfigDirectory, TileDirectory);
```

**Edit** `src/TaskbarTiles.Host/Runtime/TileContext.cs`, find:
```csharp
sealed class TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public string ConfigDirectory => configDirectory;
```
Replace with:
```csharp
sealed class TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory, string tileDirectory) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public string ConfigDirectory => configDirectory;
    public string TileDirectory => tileDirectory;
```

**Replace the whole file** `src/TaskbarTiles.Plugins.Script/CommandResolver.cs` with:
```csharp
namespace TaskbarTiles.Plugins.Script;

/// <summary>A process to start: what ProcessStartInfo needs.</summary>
public sealed record ScriptCommand(string FileName, string Arguments, string WorkingDirectory);

/// <summary>
/// Turns a hook's "run" into a process. The first token is the script (quote it if it has spaces), resolved
/// against the base folder (the tile's kind folder), which is also the working directory; .ps1 runs via PowerShell,
/// .py via python, other existing files and plain command lines via cmd.exe. The rest of the line is passed on unchanged.
/// </summary>
public sealed class CommandResolver(string baseDirectory, string powerShell, string python = "python")
{
    const string Shell = "cmd.exe";

    public ScriptCommand Resolve(string run)
    {
        var line = run.Trim();
        var (first, rest) = SplitFirst(line);
        string path;
        try
        {
            path = Path.IsPathFullyQualified(first) ? first : Path.GetFullPath(Path.Combine(baseDirectory, RelativePart(first)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(Shell, $"/d /s /c \"{line}\"", baseDirectory); // the first token is no path at all
        }
        var tail = rest.Length == 0 ? "" : " " + rest;

        return Path.GetExtension(first).ToLowerInvariant() switch
        {
            ".ps1" => new(powerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(path)}{tail}", baseDirectory),
            ".py" => new(python, $"-u {Quote(path)}{tail}", baseDirectory),
            _ when File.Exists(path) => new(Shell, $"/d /s /c \"{Quote(path)}{tail}\"", baseDirectory),
            _ => new(Shell, $"/d /s /c \"{line}\"", baseDirectory),
        };
    }

    /// <summary>Drops a drive ("C:x") and leading separators so a rooted but not fully qualified path stays under the base folder.</summary>
    static string RelativePart(string path)
    {
        if (path.Length >= 2 && path[1] == ':') path = path[2..];
        return path.TrimStart('\\', '/');
    }

    internal static (string Script, string Remainder) SplitFirst(string run)
    {
        if (run.StartsWith('"'))
        {
            var end = run.IndexOf('"', 1);
            return end < 0 ? (run[1..], "") : (run[1..end], run[(end + 1)..].Trim());
        }
        var space = run.IndexOfAny([' ', '\t']);
        return space < 0 ? (run, "") : (run[..space], run[(space + 1)..].Trim());
    }

    static string Quote(string path) => "\"" + path + "\"";
}
```

**Edit** `src/TaskbarTiles.Plugins.Script/ScriptConnector.cs`, find:
```csharp
        _resolver = new CommandResolver(ctx.ConfigDirectory, _powerShell);
```
Replace with:
```csharp
        _resolver = new CommandResolver(ctx.TileDirectory, _powerShell); // run paths and the working directory: the kind folder
```

**Edit** `src/TaskbarTiles.Plugins.Script/ScriptSettings.cs`, find:
```csharp
/// <summary>The script connector's settings: { "hooks": { "interval": { "every": "30s", "run": "scripts/x.ps1" }, ... } }.</summary>
```
Replace with:
```csharp
/// <summary>The script connector's settings, from tile.json: { "hooks": { "interval": { "every": "30s", "run": "x.ps1" }, ... } }.</summary>
```
In the same file, find:
```csharp
            errors.Add("settings.hooks must be an object, e.g. { \"interval\": { \"every\": \"30s\", \"run\": \"scripts/status.ps1\" } }.");
```
Replace with:
```csharp
            errors.Add("settings.hooks must be an object, e.g. { \"interval\": { \"every\": \"30s\", \"run\": \"status.ps1\" } }.");
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~ScriptConnector|FullyQualifiedName~TileRuntimeRestartTests|FullyQualifiedName~CommandResolverTests|FullyQualifiedName~ScriptSettingsTests"`
Expected: PASS, `Failed: 0`.

Then: `dotnet test --disable-build-servers TaskbarTiles.sln` → `Failed: 0`; `dotnet build TaskbarTiles.sln --disable-build-servers` → `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Contracts/ITileContext.cs src/TaskbarTiles.Host/Runtime/TileRuntime.cs src/TaskbarTiles.Host/Runtime/TileContext.cs src/TaskbarTiles.Plugins.Script tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/ScriptConnectorTests.cs tests/TaskbarTiles.Tests/ScriptConnectorIntegrationTests.cs tests/TaskbarTiles.Tests/TileRuntimeRestartTests.cs
git commit -m "feat(script): run paths and working directory resolve against ITileContext.TileDirectory

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Tile instances name a kind (config, host runtimes, picker)

**Files:**
- Create: `src/TaskbarTiles.Host/Config/KindValidation.cs`, `src/TaskbarTiles.Host/Runtime/BrokenKindConnector.cs`
- Modify: `src/TaskbarTiles.Host/Config/HostConfig.cs`, `src/TaskbarTiles.Host/Config/ConfigParser.cs`, `src/TaskbarTiles.Host/Config/ConfigJson.cs`, `src/TaskbarTiles.Host/Config/ConfigStore.cs`, `src/TaskbarTiles.Host/Plugins/TileKinds.cs`, `src/TaskbarTiles.Host/HostController.cs`, `src/TaskbarTiles.Host/Layout/TileGallery.cs`, `src/TaskbarTiles.Host/AppPaths.cs`, `src/TaskbarTiles.Host/App.xaml.cs`
- Test: `tests/TaskbarTiles.Tests/KindValidationTests.cs`, `tests/TaskbarTiles.Tests/TileKindTests.cs` (new); `tests/TaskbarTiles.Tests/TestDoubles.cs`, `ConfigParserTests.cs`, `ConfigJsonTests.cs`, `ConfigStoreTests.cs`, `ConfigStoreSaveTests.cs`, `HostControllerTests.cs`, `HostControllerLayoutTests.cs`, `TileGalleryTests.cs` (modified)

**Interfaces:**
- Consumes: Task 1 (`KindTable`, `TileKind`, `KindDlls`, `KindLoader.Load`, `ConnectorCatalog.Instantiate`, `TileKind.ScriptConnectorId`), Task 2 (`TileRuntimeOptions.TileDirectory`, `ITileContext.TileDirectory`)
- Produces:
  - `sealed record TileConfig(string Kind, JsonElement Settings)`
  - `static ConfigError? KindValidation.Check(HostConfig config, KindTable kinds)` (namespace `TaskbarTiles.Host.Config`)
  - `JsonElement TileKind.SettingsFor(JsonElement instanceSettings)`
  - `ConfigStore(string path, ITileLog log, Func<HostConfig, ConfigError?>? validate = null)`
  - `HostController(ConnectorCatalog catalog, Func<HostConfig> config, ITaskbarSource taskbars, IUiDispatcher ui, Func<string, ITileLog> logFor, Func<string, ITileBarView> createBar, string? configDirectory = null, IConfigWriter? writer = null, Func<KindTable>? kinds = null)`
  - `static IReadOnlyList<GalleryItem> TileGallery.Build(HostConfig config, DisplayLayout layout, KindTable kinds)`; `ScriptTemplate`, `ScriptTemplates`, `TileGallery.ScriptConnectorId` no longer exist
  - `sealed class BrokenKindConnector(string message) : ITileConnector` (namespace `TaskbarTiles.Host.Runtime`)
  - `AppPaths.TilesDir`, `AppPaths.PluginsDir`; `AppPaths.PluginRoots` no longer exists
  - Test helper `static class TestKinds { const string TilesDir; Of(params TileKind[]); Script(...); Plugin(...); Broken(...) }`

- [ ] **Step 1: Switch the existing tests to the kind form**

Run (Git Bash, repo root) — rewrites every `"connector": "` (plain and escaped) to `"kind": "` and `].Connector` to `].Kind` in the tests that build configs:
```bash
sed -i -e 's/"connector": "/"kind": "/g' -e 's/\\"connector\\": \\"/\\"kind\\": \\"/g' -e 's/\]\.Connector\b/].Kind/g' \
  tests/TaskbarTiles.Tests/ConfigParserTests.cs tests/TaskbarTiles.Tests/ConfigJsonTests.cs \
  tests/TaskbarTiles.Tests/ConfigStoreTests.cs tests/TaskbarTiles.Tests/ConfigStoreSaveTests.cs \
  tests/TaskbarTiles.Tests/HostControllerTests.cs tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs
grep -n "connector\"" tests/TaskbarTiles.Tests/ConfigParserTests.cs tests/TaskbarTiles.Tests/ConfigJsonTests.cs tests/TaskbarTiles.Tests/ConfigStore*.cs tests/TaskbarTiles.Tests/HostController*.cs
```
Expected: the grep prints only `ConfigParserTests.cs` lines for `Invalid_json_reports_one_based_line_and_column` (`\"connector\": }`, still invalid JSON on line 3, left as is) and `Tile_without_connector_is_an_error` (replaced below). `ConfigNoticeTests.cs` is not touched: it only formats a message string.

- [ ] **Step 2: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TestDoubles.cs`, find:
```csharp
using System.IO;
using TaskbarTiles.Contracts;
```
Replace with:
```csharp
using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;
```
At the end of the same file, append:
```csharp

/// <summary>Kind tables for tests, under a fake C:\tiles folder.</summary>
public static class TestKinds
{
    public const string TilesDir = @"C:\tiles";

    public static KindTable Of(params TileKind[] kinds) => new(TilesDir,
        kinds.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static TileKind Script(string id, string settingsJson = "{}", string stamp = "1", string? name = null, string description = "") =>
        new(id, System.IO.Path.Combine(TilesDir, id), name ?? id, description, TileKind.ScriptConnectorId, Json(settingsJson),
            KindDlls.None, null, stamp, "");

    /// <summary>A kind whose DLL declares connector <paramref name="id"/> as <paramref name="connectorType"/>.</summary>
    public static TileKind Plugin(string id, Type connectorType, double width = 100, string settingsJson = "{}") =>
        new(id, System.IO.Path.Combine(TilesDir, id), id, "", id, Json(settingsJson),
            new KindDlls(new Dictionary<string, ConnectorDescriptor>
            {
                [id] = new(id, connectorType, width, System.IO.Path.Combine(TilesDir, id, id + ".dll")),
            }, null, ""),
            null, "1", "");

    public static TileKind Broken(string id, string error) =>
        new(id, System.IO.Path.Combine(TilesDir, id), id, error, "", Json("{}"), KindDlls.None, error, "1", "");

    static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
}
```

**Edit** `tests/TaskbarTiles.Tests/ConfigParserTests.cs`, find:
```csharp
    [Fact]
    public void Tile_without_connector_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { } } }""");
        Assert.False(result.Success);
        Assert.Contains("connector", result.Error!.Message);
    }
```
Replace with:
```csharp
    [Fact]
    public void Tile_without_kind_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { } } }""");
        Assert.False(result.Success);
        Assert.Equal("Tile 'a' needs a \"kind\": the name of its folder in tiles\\.", result.Error!.Message);
    }

    [Fact]
    public void The_old_connector_form_is_an_error_that_shows_the_new_form()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "x": { "connector": "script", "settings": { "hooks": {} } } } }""");

        Assert.False(result.Success);
        Assert.Equal("Tile 'x' uses \"connector\"; tiles now name a kind: \"kind\": \"<folder>\"", result.Error!.Message);
    }

    [Fact]
    public void Hooks_in_instance_settings_are_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "x": { "kind": "ping", "settings": { "host": "a", "hooks": {} } } } }""");

        Assert.False(result.Success);
        Assert.Equal(@"hooks belong in tiles\ping\tile.json", result.Error!.Message);
    }

    [Fact]
    public void Instance_settings_are_optional()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "ping-dns": { "kind": "ping" } } }""");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal("ping", result.Config!.Tiles["ping-dns"].Kind);
        Assert.Equal("{}", result.Config.Tiles["ping-dns"].Settings.GetRawText());
    }
```

**Edit** `tests/TaskbarTiles.Tests/ConfigJsonTests.cs` (after Step 1), find:
```csharp
    [Fact]
    public void Set_tile_writes_connector_and_settings()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "x" } } }""");
        var settings = JsonDocument.Parse("""{ "hooks": { "click": "scripts/a.ps1" } }""").RootElement.Clone();

        ConfigJson.SetTile(root, "b", new TileConfig("script", settings));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new[] { "a", "b" }, parsed.Config!.Tiles.Keys);
        Assert.Equal("scripts/a.ps1", parsed.Config.Tiles["b"].Settings.GetProperty("hooks").GetProperty("click").GetString());
    }
```
Replace with:
```csharp
    [Fact]
    public void Set_tile_writes_the_kind_and_settings_only_when_there_are_any()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "x" } } }""");
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var host = JsonDocument.Parse("""{ "host": "192.168.1.1" }""").RootElement.Clone();

        ConfigJson.SetTile(root, "b", new TileConfig("ping", empty));
        ConfigJson.SetTile(root, "c", new TileConfig("ping", host));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.Equal("""{"kind":"ping"}""", root["tiles"]!["b"]!.ToJsonString());
        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new[] { "a", "b", "c" }, parsed.Config!.Tiles.Keys);
        Assert.Equal("192.168.1.1", parsed.Config.Tiles["c"].Settings.GetProperty("host").GetString());
    }
```

**Edit** `tests/TaskbarTiles.Tests/ConfigStoreTests.cs` (after Step 1), find:
```csharp
        Assert.Equal("media", store.Current.Tiles["spotify"].Kind);
        Assert.Equal("script", store.Current.Tiles["uptime"].Kind);
        Assert.Null(store.LastError);
    }
```
Replace with:
```csharp
        Assert.Equal("media", store.Current.Tiles["spotify"].Kind);
        Assert.Equal("uptime", store.Current.Tiles["uptime"].Kind);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_and_save_run_the_validator()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "a": { "kind": "ping" } }, "displays": { "primary": { "slots": [] } } }""");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog(),
            cfg => cfg.Tiles.ContainsKey("bad") ? new ConfigError("no bad tiles") : null);
        store.Load();

        var error = store.Save(root => ConfigJson.SetTile(root, "bad", new TileConfig("ping", store.Current.Tiles["a"].Settings)));

        Assert.Null(store.LastError);
        Assert.Equal("The change was not saved: no bad tiles", error!.Message);
        Assert.DoesNotContain("bad", File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Load_rejects_a_config_the_validator_refuses()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "a": { "kind": "ghost" } } }""");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog(), _ => new ConfigError("missing kind"));

        store.Load();

        Assert.Equal("missing kind", store.LastError!.Message);
        Assert.Empty(store.Current.Tiles);
    }
```

**Create** `tests/TaskbarTiles.Tests/KindValidationTests.cs`:
```csharp
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindValidationTests
{
    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    [Fact]
    public void Every_kind_with_a_folder_passes_even_a_broken_one()
    {
        var kinds = TestKinds.Of(TestKinds.Script("ping"), TestKinds.Broken("media", @"tiles\media\tile.json is not valid JSON"));

        Assert.Null(KindValidation.Check(Parse("""{ "tiles": { "a": { "kind": "Ping" }, "m": { "kind": "media" } } }"""), kinds));
    }

    [Fact]
    public void A_kind_without_a_folder_names_the_tile_and_the_folder()
    {
        var error = KindValidation.Check(Parse("""{ "tiles": { "a": { "kind": "ping" }, "w": { "kind": "weather" } } }"""),
            TestKinds.Of(TestKinds.Script("ping")));

        Assert.Equal(@"Tile 'w' uses kind 'weather', but there is no tiles\weather folder.", error!.Message);
    }

    [Fact]
    public void A_folder_without_tile_json_gets_a_hint()
    {
        var kinds = TestKinds.Of() with { FoldersWithoutTileJson = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "meeting" } };

        var error = KindValidation.Check(Parse("""{ "tiles": { "m": { "kind": "meeting" } } }"""), kinds);

        Assert.Equal(@"Tile 'm' uses kind 'meeting', but tiles\meeting has no tile.json.", error!.Message);
    }
}
```

**Create** `tests/TaskbarTiles.Tests/TileKindTests.cs`:
```csharp
using System.Text.Json;

namespace TaskbarTiles.Tests;

public class TileKindTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Instance_keys_replace_the_kinds_keys_one_by_one()
    {
        var kind = TestKinds.Script("ping", """{ "host": "1.1.1.1", "count": 4, "hooks": { "interval": { "every": "15s", "run": "ping.ps1" } } }""");

        var settings = kind.SettingsFor(Json("""{ "host": "192.168.1.1", "label": "home" }"""));

        Assert.Equal("""{"host":"192.168.1.1","count":4,"hooks":{"interval":{"every":"15s","run":"ping.ps1"}},"label":"home"}""",
            settings.GetRawText());
    }

    [Fact]
    public void The_merge_is_shallow()
    {
        var kind = TestKinds.Script("x", """{ "opts": { "a": 1, "b": 2 } }""");

        Assert.Equal("""{"opts":{"a":5}}""", kind.SettingsFor(Json("""{ "opts": { "a": 5 } }""")).GetRawText());
    }

    [Fact]
    public void No_instance_settings_means_the_kinds_settings() =>
        Assert.Equal("""{"host":"1.1.1.1"}""", TestKinds.Script("ping", """{ "host": "1.1.1.1" }""").SettingsFor(Json("{}")).GetRawText());
}
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerTests.cs` (after Step 1). In the class, replace the four tests from the `[Fact]` above `Unknown_connector_gets_placeholder_runtime` through the closing brace of `DisposeAsync_does_not_deadlock_under_single_threaded_context` with:
```csharp
    [Fact]
    public async Task A_tile_whose_kind_is_missing_shows_a_tile_error()
    {
        var (host, _, _, _, _) = Create(Parse(TwoTiles));
        host.Start();

        Assert.Equal("ghost", host.Runtimes["a"].ConnectorId);
        await Eventually.Holds(() => host.Runtimes["a"].LastError is not null);
        Assert.Equal(@"There is no tiles\ghost folder with a tile.json.", host.Runtimes["a"].LastError!.Message);
        Assert.False(host.Runtimes["a"].LastError!.Fatal);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_broken_kind_shows_its_error_on_its_tiles_only()
    {
        var kinds = TestKinds.Of(TestKinds.Broken("bad", @"tiles\bad\tile.json is not valid JSON: x (line 2)"), TestKinds.Script("ok"));
        var cfg = Parse("""{ "tiles": { "a": { "kind": "bad" }, "b": { "kind": "ok" } }, "displays": { "primary": { "slots": ["a", "b"] } } }""");
        var host = new HostController(ConnectorCatalog.Empty, () => cfg,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), kinds: () => kinds);

        host.Start();

        await Eventually.Holds(() => host.Runtimes["a"].LastError is not null && host.Runtimes["b"].State.Title == "Unknown connector");
        Assert.Equal(@"tiles\bad\tile.json is not valid JSON: x (line 2)", host.Runtimes["a"].LastError!.Message);
        Assert.Null(host.Runtimes["b"].LastError); // the other kind still runs (here: no script plugin, so the placeholder)
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Throwing_plugin_constructor_shows_an_error_tile()
    {
        var kinds = TestKinds.Of(TestKinds.Plugin("bad", typeof(ThrowingConnector)));
        var cfg = Parse("""{ "tiles": { "a": { "kind": "bad" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var bars = new Dictionary<string, FakeBar>();
        var log = new CollectingLog();
        var host = new HostController(ConnectorCatalog.Empty, () => cfg, source, new ImmediateDispatcher(),
            _ => log, id => bars[id] = new FakeBar(id), kinds: () => kinds);

        host.Start();

        Assert.Equal("bad", host.Runtimes["a"].ConnectorId);
        Assert.Equal(new[] { "a" }, bars["MAIN"].Tiles.Select(t => t.TileId));
        await Eventually.Holds(() => host.Runtimes["a"].State.Title == "Error");
        Assert.Equal("boom", host.Runtimes["a"].LastError!.Message); // not "Exception has been thrown by the target of an invocation."
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Connectors_are_built_off_the_calling_thread_and_get_effective_settings_and_folders()
    {
        var kinds = TestKinds.Of(TestKinds.Plugin("cap", typeof(CapturingConnector), settingsJson: """{ "a": 1, "b": 2 }"""));
        var cfg = Parse("""{ "tiles": { "x": { "kind": "cap", "settings": { "b": 3 } } }, "displays": { "primary": { "slots": ["x"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var host = new HostController(ConnectorCatalog.Empty, () => cfg, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), configDirectory: @"C:\cfg", kinds: () => kinds);

        CapturingConnector.InsideStart = true;
        try { host.Start(); }
        finally { CapturingConnector.InsideStart = false; }

        var ctx = await CapturingConnector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"C:\cfg", ctx.ConfigDirectory);
        Assert.Equal(@"C:\tiles\cap", ctx.TileDirectory);
        Assert.Equal(@"C:\tiles\cap", host.Runtimes["x"].TileDirectory);
        Assert.Equal("""{"a":1,"b":3}""", ctx.Settings.GetRawText());
        Assert.Equal(100, host.Runtimes["x"].Width);
        Assert.False(CapturingConnector.ConstructedInsideStart);
        await host.DisposeAsync();
    }

    [Fact]
    public void Changing_a_kind_restarts_only_its_tiles()
    {
        var kinds = TestKinds.Of(TestKinds.Script("ping"), TestKinds.Script("uptime"));
        var cfg = Parse("""
            { "tiles": { "p1": { "kind": "ping" }, "p2": { "kind": "ping" }, "u": { "kind": "uptime" } },
              "displays": { "primary": { "slots": ["p1", "p2", "u"] } } }
            """);
        var host = new HostController(ConnectorCatalog.Empty, () => cfg,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), kinds: () => kinds);
        host.Start();
        var before = host.Runtimes;

        kinds = TestKinds.Of(TestKinds.Script("ping", stamp: "2"), TestKinds.Script("uptime"));
        host.ConfigChanged();

        Assert.NotSame(before["p1"], host.Runtimes["p1"]);
        Assert.NotSame(before["p2"], host.Runtimes["p2"]);
        Assert.Same(before["u"], host.Runtimes["u"]);
    }

    [Fact]
    public async Task DisposeAsync_does_not_deadlock_under_single_threaded_context()
    {
        var kinds = TestKinds.Of(TestKinds.Plugin("slow", typeof(SlowDisposeConnector)));
        var cfg = Parse("""{ "tiles": { "a": { "kind": "slow" } }, "displays": { "primary": { "slots": ["a"] } } }""");
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var host = new HostController(ConnectorCatalog.Empty, () => cfg, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), kinds: () => kinds);
        host.Start();

        var previous = SynchronizationContext.Current;
        Task disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            disposal = host.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await disposal.WaitAsync(TimeSpan.FromSeconds(2)); // throws TimeoutException if it needs the blocked context
    }
```

**Edit** `tests/TaskbarTiles.Tests/HostControllerLayoutTests.cs`, find:
```csharp
using System.Text.Json;
using TaskbarTiles.Host;
```
Replace with:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Host;
```
In the same file, find:
```csharp
    [Fact]
    public void A_stack_is_one_slot_and_all_its_members_get_runtimes()
```
Replace with:
```csharp
    [Fact]
    public void Removing_a_tile_never_touches_its_kind_folder()
    {
        using var tiles = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(tiles.Path, "ghost")).FullName;
        File.WriteAllText(Path.Combine(folder, "tile.json"), """{ "name": "Ghost", "connector": "script" }""");
        File.WriteAllText(Path.Combine(folder, "ghost.ps1"), "'{}'");
        var kinds = new KindLoader(new CollectingLog()).Load(tiles.Path);
        var writer = new FakeConfigWriter(Config);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, kinds: () => kinds);
        host.Start();

        Assert.Null(host.RemoveTile("MAIN", 0, null));

        Assert.False(writer.Current.Tiles.ContainsKey("a")); // the instance is gone (no display places it)
        Assert.True(File.Exists(Path.Combine(folder, "tile.json")));
        Assert.True(File.Exists(Path.Combine(folder, "ghost.ps1")));
    }

    [Fact]
    public void A_stack_is_one_slot_and_all_its_members_get_runtimes()
```

**Replace the whole file** `tests/TaskbarTiles.Tests/TileGalleryTests.cs` with:
```csharp
using System.Text.Json.Nodes;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class TileGalleryTests
{
    const string Config = """
    { "tiles": { "spotify": { "kind": "media" }, "ci": { "kind": "ping" }, "uptime": { "kind": "uptime" } },
      "displays": { "primary": { "slots": [ { "stack": ["spotify"] } ] } } }
    """;

    static readonly KindTable Kinds = TestKinds.Of(
        TestKinds.Script("ping", name: "Ping a host", description: "Pings a host every 15 s"),
        TestKinds.Script("uptime", name: "Clock and uptime", description: "The time, and how long Windows has been up"),
        TestKinds.Plugin("media", typeof(FakeConnector)) with { Name = "Media", Description = "Now playing" });

    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    static HostController Host(FakeConfigWriter writer, Dictionary<string, FakeBar>? bars = null)
    {
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars is null ? new FakeBar(id) : (bars[id] = new FakeBar(id)), writer: writer, kinds: () => Kinds);
        host.Start();
        return host;
    }

    [Fact]
    public void Lists_unplaced_tiles_then_one_entry_per_kind_by_name()
    {
        var cfg = Parse(Config);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], Kinds);

        Assert.Equal(new[] { "ci", "uptime", "Clock and uptime", "Media", "Ping a host" }, items.Select(i => i.Title));
        Assert.Null(items[0].NewTile);
        Assert.Equal("Your tile · Ping a host", items[0].Subtitle);
        Assert.Equal("Pings a host every 15 s", items[4].Subtitle);
        Assert.Equal("ping", items[4].TileId);
        Assert.Equal("ping", items[4].NewTile!.Kind);
        Assert.Equal("{}", items[4].NewTile!.Settings.GetRawText());
    }

    [Fact]
    public void An_instance_of_a_kind_that_is_gone_shows_the_kind_id()
    {
        var cfg = Parse("""{ "tiles": { "w": { "kind": "weather" } } }""");

        Assert.Equal("Your tile · weather", TileGallery.Build(cfg, DisplayLayout.Empty, Kinds)[0].Subtitle);
    }

    [Theory]
    [InlineData(new string[0], "ping")]
    [InlineData(new[] { "ping" }, "ping-2")]
    [InlineData(new[] { "ping", "ping-2", "ping-4" }, "ping-3")]
    public void Unique_id_appends_the_first_free_number(string[] taken, string expected) =>
        Assert.Equal(expected, TileGallery.UniqueId("ping", taken));

    [Fact]
    public void Picking_a_kind_whose_id_is_free_writes_only_the_kind()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Ping a host");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal("""{"kind":"ping"}""", JsonNode.Parse(writer.Json)!["tiles"]!["ping"]!.ToJsonString());
        Assert.Equal(new TileSlot("ping", false), writer.Current.Displays["primary"].Slots[^1]);
    }

    [Fact]
    public void Picking_a_kind_whose_id_is_taken_writes_it_under_a_free_id_and_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var bars = new Dictionary<string, FakeBar>();
        var host = Host(writer, bars);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal("""{"kind":"uptime"}""", JsonNode.Parse(writer.Json)!["tiles"]!["uptime-2"]!.ToJsonString());
        Assert.Equal(new TileSlot("uptime-2", false), writer.Current.Displays["primary"].Slots[^1]);
        Assert.Equal(new[] { "spotify", "uptime-2" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Adding_an_existing_tile_only_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal(3, writer.Current.Tiles.Count);
        Assert.Equal(new TileSlot("ci", false), writer.Current.Displays["primary"].Slots[^1]);
        Assert.DoesNotContain(host.GalleryFor("MAIN"), i => i.Title == "ci");
    }

    [Fact]
    public void Adding_a_new_tile_to_a_stack_puts_it_into_that_slot()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        Assert.Equal("uptime", writer.Current.Tiles["uptime-2"].Kind);
        var slot = Assert.IsType<StackSlot>(Assert.Single(writer.Current.Displays["primary"].Slots));
        Assert.Equal(new[] { "spotify", "uptime-2" }, slot.TileIds);
    }

    [Fact]
    public void Adding_an_existing_tile_to_a_plain_tile_makes_a_stack_in_its_place()
    {
        var writer = new FakeConfigWriter("""
            { "tiles": { "spotify": { "kind": "media" }, "ci": { "kind": "ping" }, "uptime": { "kind": "uptime" } },
              "displays": { "primary": { "slots": [ { "tile": "spotify" }, { "tile": "uptime" } ] } } }
            """);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        var slots = writer.Current.Displays["primary"].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { "spotify", "ci" }, Assert.IsType<StackSlot>(slots[0]).TileIds);
        Assert.Equal(new TileSlot("uptime", false), slots[1]);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~Config|FullyQualifiedName~Kind|FullyQualifiedName~HostController|FullyQualifiedName~TileGallery"`
Expected: build FAILS with `CS1061: 'TileConfig' does not contain a definition for 'Kind'` (and `KindValidation`, `SettingsFor`, the `kinds` parameter not found).

- [ ] **Step 4: Write the implementation**

**Edit** `src/TaskbarTiles.Host/Config/HostConfig.cs`, find:
```csharp
/// <summary>Settings is a cloned element, safe to keep after parsing.</summary>
public sealed record TileConfig(string Connector, JsonElement Settings);
```
Replace with:
```csharp
/// <summary>
/// A tile instance: the kind (a folder in tiles\) and the settings that replace the kind's own, key by key.
/// Settings is a cloned element, safe to keep after parsing.
/// </summary>
public sealed record TileConfig(string Kind, JsonElement Settings);
```

**Edit** `src/TaskbarTiles.Host/Config/ConfigParser.cs`, find:
```csharp
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
```
Replace with:
```csharp
    static TileConfig BuildTile(string id, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ConfigException($"Tile '{id}' must be an object.");
        if (el.TryGetProperty("connector", out _)) // the inline form before tile folders; no migration
            throw new ConfigException($"Tile '{id}' uses \"connector\"; tiles now name a kind: \"kind\": \"<folder>\"");
        if (!el.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(k.GetString()))
            throw new ConfigException($"Tile '{id}' needs a \"kind\": the name of its folder in tiles\\.");

        var kind = k.GetString()!;
        var settings = el.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.Clone()
            : EmptyObject();
        if (settings.TryGetProperty("hooks", out _)) throw new ConfigException($@"hooks belong in tiles\{kind}\tile.json");
        return new TileConfig(kind, settings);
    }
```

**Edit** `src/TaskbarTiles.Host/Config/ConfigJson.cs`, find:
```csharp
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
```
Replace with:
```csharp
    /// <summary>Adds or replaces tiles[id] with { "kind": ... }, plus "settings" when it has any keys.</summary>
    public static void SetTile(JsonObject root, string id, TileConfig tile)
    {
        var tiles = ChildObject(root, "tiles");
        var node = new JsonObject { ["kind"] = tile.Kind };
        if (tile.Settings.ValueKind == JsonValueKind.Object && tile.Settings.EnumerateObject().Any())
            node["settings"] = JsonNode.Parse(tile.Settings.GetRawText());
        tiles[id] = node;
    }
```

**Create** `src/TaskbarTiles.Host/Config/KindValidation.cs`:
```csharp
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.Config;

/// <summary>
/// The check of config.json that needs the kind table: every tile names a folder in tiles\ that has a tile.json.
/// A broken kind passes; only mistakes in config.json reject it.
/// </summary>
public static class KindValidation
{
    /// <summary>The first tile (in config order) whose kind has no folder with tile.json, or null when all have one.</summary>
    public static ConfigError? Check(HostConfig config, KindTable kinds)
    {
        foreach (var (id, tile) in config.Tiles)
        {
            if (kinds.Find(tile.Kind) is not null) continue;
            return kinds.FoldersWithoutTileJson.Contains(tile.Kind)
                ? new ConfigError($@"Tile '{id}' uses kind '{tile.Kind}', but tiles\{tile.Kind} has no tile.json.")
                : new ConfigError($@"Tile '{id}' uses kind '{tile.Kind}', but there is no tiles\{tile.Kind} folder.");
        }
        return null;
    }
}
```

**Edit** `src/TaskbarTiles.Host/Config/ConfigStore.cs`, find:
```csharp
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
```
Replace with:
```csharp
/// <summary>
/// Owns config.json: creates it on first run, parses it, reloads it when it changes on disk, and saves edits.
/// <paramref name="validate"/> runs after every successful parse (load, reload, save, recheck); a non-null result rejects
/// the config like a parse error. The app passes the kind check (<see cref="KindValidation"/>).
/// </summary>
public sealed class ConfigStore(string path, ITileLog log, Func<HostConfig, ConfigError?>? validate = null) : IConfigWriter, IDisposable
{
    public const string DefaultJson = """
    {
      // Each tile is an instance of a tile kind: a folder in tiles\ next to this file with its tile.json, scripts and DLLs.
      // "settings" here replaces the kind's settings key by key. Place tiles on displays under "displays".
      // Edit layout (right-click a tile) rewrites this file without comments; the previous file is kept as config.json.bak.
      "tiles": {
        "spotify": { "kind": "media" },
        "uptime":  { "kind": "uptime" }
      },
```
In the same file, find:
```csharp
        var result = ConfigParser.Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
            _appliedText = text;
        }
```
Replace with:
```csharp
        var result = Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
            _appliedText = text;
        }
```
In the same file, find:
```csharp
                var text = ReadWithRetry();
                var before = ConfigParser.Parse(text);
```
Replace with:
```csharp
                var text = ReadWithRetry();
                var before = Parse(text);
```
In the same file, find:
```csharp
                var after = ConfigParser.Parse(newText);
```
Replace with:
```csharp
                var after = Parse(newText);
```
In the same file, find:
```csharp
                var result = ConfigParser.Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
                    LastError = null;
                    _appliedText = text;
                    log.Info("Reloaded config.json");
```
Replace with:
```csharp
                var result = Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
                    LastError = null;
                    _appliedText = text;
                    log.Info("Reloaded config.json");
```
In the same file, find:
```csharp
    // Editors often hold the file briefly while saving.
    string ReadWithRetry()
```
Replace with:
```csharp
    /// <summary>ConfigParser, then the validator.</summary>
    ConfigParseResult Parse(string text)
    {
        var result = ConfigParser.Parse(text);
        if (!result.Success || validate?.Invoke(result.Config!) is not { } error) return result;
        return new ConfigParseResult(null, error);
    }

    // Editors often hold the file briefly while saving.
    string ReadWithRetry()
```

**Edit** `src/TaskbarTiles.Host/Plugins/TileKinds.cs`, find:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Host.Config;
```
Replace with:
```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskbarTiles.Host.Config;
```
In the same file, find:
```csharp
    /// <summary>The DLLs on disk differ from the ones loaded at startup; only an app restart loads them.</summary>
    public bool NeedsRestart => DllStamp != Dlls.Stamp;
}
```
Replace with:
```csharp
    /// <summary>The DLLs on disk differ from the ones loaded at startup; only an app restart loads them.</summary>
    public bool NeedsRestart => DllStamp != Dlls.Stamp;

    /// <summary>
    /// Effective settings: this kind's settings with each top-level key of the instance's settings replacing the kind's
    /// key of the same name (shallow; new keys are appended). What connectors get and the script's TILE_SETTINGS_JSON.
    /// </summary>
    public JsonElement SettingsFor(JsonElement instanceSettings)
    {
        var merged = JsonNode.Parse(Settings.GetRawText()) as JsonObject ?? new JsonObject();
        if (instanceSettings.ValueKind == JsonValueKind.Object)
            foreach (var property in instanceSettings.EnumerateObject())
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }
}
```

**Create** `src/TaskbarTiles.Host/Runtime/BrokenKindConnector.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>
/// Stands in for a tile whose kind is missing or broken: shows the error look with what is wrong. A reported error (not a
/// throw), so it is not restarted with backoff; a fixed tile.json changes the kind's stamp and the host restarts the tile.
/// </summary>
public sealed class BrokenKindConnector(string message) : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.ReportError(message);
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

**Replace the whole file** `src/TaskbarTiles.Host/Layout/TileGallery.cs` with:
```csharp
using System.Text.Json;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One choice in the "+" / stack picker. <see cref="NewTile"/> null: place the existing tile <see cref="TileId"/>.
/// Otherwise: add an instance of the kind <c>NewTile.Kind</c> under a free id based on <see cref="TileId"/>.
/// </summary>
public sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile);

public static class TileGallery
{
    /// <summary>
    /// Existing tiles this display does not place yet (in config order), then one new tile per kind in tiles\, by name
    /// (broken kinds too: titled with their folder name, the error as subtitle). A new tile is just { "kind": id }.
    /// </summary>
    public static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayLayout layout, KindTable kinds)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        var items = new List<GalleryItem>();

        foreach (var (id, tile) in config.Tiles)
            if (!placed.Contains(id))
                items.Add(new GalleryItem(id, $"Your tile · {kinds.Find(tile.Kind)?.Name ?? tile.Kind}", id, null));

        foreach (var kind in kinds.Kinds.Values
                     .OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(k => k.Id, StringComparer.OrdinalIgnoreCase))
            items.Add(new GalleryItem(kind.Name, kind.Description, kind.Id, new TileConfig(kind.Id, EmptySettings())));

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

    static JsonElement EmptySettings()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
```

**Edit** `src/TaskbarTiles.Host/HostController.cs`, find:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
```
Replace with:
```csharp
using System.IO;
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
```
In the same file, find:
```csharp
    /// <summary>
    /// Places the gallery item at the end of the display's layout, adding its tile definition when it is new. With
```
Replace with:
```csharp
    /// <summary>
    /// Places the gallery item at the end of the display's layout, adding an instance of its kind when it is new. With
```
In the same file, find:
```csharp
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
```
Replace with:
```csharp
/// <summary>
/// Keeps runtimes and bars in sync with config, the tile kinds and connected displays.
/// One runtime per tile id placed on any display (stack members included); one bar per display. Runs on the UI thread.
/// <paramref name="catalog"/> holds the connectors built into the app (script); <paramref name="kinds"/> the kind table.
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
    Func<KindTable>? kinds = null) : ILayoutEditor, IAsyncDisposable
{
    /// <summary>How one tile runs; a runtime is kept while its Fingerprint is unchanged.</summary>
    sealed record TilePlan(string ConnectorId, double Width, Func<ITileConnector> Create, JsonElement Settings, string Directory, string Fingerprint);

    readonly Dictionary<string, (TileRuntime Runtime, string Fingerprint)> _runtimes = new(StringComparer.Ordinal);
```
In the same file, find:
```csharp
    /// <summary>Call on the UI thread after config.json reloaded.</summary>
    public void ConfigChanged() => Sync();
```
Replace with:
```csharp
    /// <summary>Call on the UI thread after config.json reloaded or the kind table changed.</summary>
    public void ConfigChanged() => Sync();
```
In the same file, find:
```csharp
        return TileGallery.Build(cfg, layout, catalog.Connectors.Keys, templates ?? ScriptTemplates.Samples);
    }
```
Replace with:
```csharp
        return TileGallery.Build(cfg, layout, Kinds());
    }

    KindTable Kinds() => kinds?.Invoke() ?? KindTable.Empty;
```
In the same file, find:
```csharp
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
```
Replace with:
```csharp
    void SyncRuntimes(HostConfig cfg, HashSet<string> wanted)
    {
        var table = Kinds();
        var plans = new Dictionary<string, TilePlan>(StringComparer.Ordinal);
        foreach (var id in wanted)
            if (cfg.Tiles.TryGetValue(id, out var tile)) plans[id] = Plan(tile, table);

        foreach (var (id, entry) in _runtimes.ToList())
        {
            if (plans.TryGetValue(id, out var plan) && plan.Fingerprint == entry.Fingerprint) continue;
            _runtimes.Remove(id);
            _ = entry.Runtime.DisposeAsync().AsTask();
        }

        foreach (var (id, plan) in plans)
        {
            if (_runtimes.ContainsKey(id)) continue;
            var runtime = new TileRuntime(id, plan.ConnectorId, plan.Width, plan.Create, plan.Settings, ui, logFor($"tile-{id}"),
                _runtimeOptions with { TileDirectory = plan.Directory });
            _runtimes[id] = (runtime, plan.Fingerprint);
            _ = runtime.StartAsync();
        }
    }

    /// <summary>
    /// The tile's kind connector with the effective settings and the kind folder, or a tile error when the kind is missing
    /// or broken. The fingerprint changes with the kind's tile.json and scripts (its stamp), so editing one kind restarts
    /// only that kind's tiles.
    /// </summary>
    TilePlan Plan(TileConfig tile, KindTable table)
    {
        if (table.Find(tile.Kind) is not { } kind)
        {
            var missing = $@"There is no tiles\{tile.Kind} folder with a tile.json.";
            return new(tile.Kind, PluginLoader.DefaultTileWidth, () => new BrokenKindConnector(missing), tile.Settings,
                Path.Combine(table.TilesDir, tile.Kind), $"missing\n{tile.Kind}\n{tile.Settings.GetRawText()}");
        }

        var settings = kind.SettingsFor(tile.Settings);
        var fingerprint = $"{kind.Directory}\n{kind.Stamp}\n{kind.Error}\n{settings.GetRawText()}";
        if (kind.Error is { } error)
            return new(kind.Connector.Length > 0 ? kind.Connector : kind.Id, PluginLoader.DefaultTileWidth,
                () => new BrokenKindConnector(error), settings, kind.Directory, fingerprint);

        // The runtime calls Create on the thread pool (plugin constructors are plugin code) and again for every restart.
        // A throwing constructor shows the error look and is retried with backoff.
        if (kind.Plugin is { } plugin)
            return new(plugin.Id, plugin.Width, () => ConnectorCatalog.Instantiate(plugin), settings, kind.Directory, fingerprint);

        var connectorId = kind.Connector; // "script": built into the app
        var width = catalog.Connectors.TryGetValue(connectorId, out var d) ? d.Width : PluginLoader.DefaultTileWidth;
        return new(connectorId, width, () => catalog.Create(connectorId) ?? new UnknownConnector(connectorId), settings,
            kind.Directory, fingerprint);
    }
```

**Edit** `src/TaskbarTiles.Host/AppPaths.cs`, find:
```csharp
    /// <summary>Built-in plugins next to the exe first, then the user's plugins.</summary>
    public static IReadOnlyList<string> PluginRoots =>
        [Path.Combine(AppContext.BaseDirectory, "plugins"), Path.Combine(DataDir, "plugins")];
```
Replace with:
```csharp
    /// <summary>One folder per tile kind (tile.json, scripts, DLLs); the folder name is the kind's id.</summary>
    public static string TilesDir => Path.Combine(DataDir, "tiles");

    /// <summary>
    /// Connectors built into the app: only the script connector (plugins\TaskbarTiles.Plugins.Script\). Every other
    /// connector DLL lives in a kind folder; %LOCALAPPDATA%\TaskbarTiles\plugins is no longer scanned.
    /// </summary>
    public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "plugins");
```

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
            var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
```
Replace with:
```csharp
            var catalog = new PluginLoader(log).LoadFrom([AppPaths.PluginsDir]);
            var kinds = new KindLoader(log).Load(AppPaths.TilesDir);
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
            _config = new ConfigStore(AppPaths.ConfigFile, log, cfg => KindValidation.Check(cfg, kinds));
```
In the same file, find:
```csharp
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir, _config);
```
Replace with:
```csharp
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir, _config, () => kinds);
```
In the same file, find:
```csharp
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
```
Replace with:
```csharp
            log.Info($"Started with {catalog.Connectors.Count} built-in connector(s) and {kinds.Kinds.Count} tile kind(s): {string.Join(", ", kinds.Kinds.Keys)}");
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~Config|FullyQualifiedName~Kind|FullyQualifiedName~HostController|FullyQualifiedName~TileGallery"`
Expected: PASS, `Failed: 0`.

Then: `dotnet test --disable-build-servers TaskbarTiles.sln` → `Failed: 0`; `dotnet build TaskbarTiles.sln --disable-build-servers` → `0 Warning(s)`; `git grep -n "ScriptTemplate\|PluginRoots" -- src tests` prints nothing.

- [ ] **Step 6: Commit**

```bash
git add -A src/TaskbarTiles.Host tests/TaskbarTiles.Tests
git commit -m "feat(kinds): tile instances name a kind; runtimes, validation and the picker come from the kind table

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Show tile folder

**Files:**
- Modify: `src/TaskbarTiles.Host/UI/TileCommands.cs`
- Test: `tests/TaskbarTiles.Tests/TileCommandsTests.cs`

**Interfaces:**
- Consumes: `TileRuntime.TileDirectory` (Task 2), `TileRuntimeOptions.TileDirectory` (Task 2)
- Produces: `enum TileCommand { ShowFolder, Refresh, ShowLog, Restart }`; menu header `"Show tile folder"`

- [ ] **Step 1: Write the failing tests**

**Edit** `tests/TaskbarTiles.Tests/TileCommandsTests.cs`, find:
```csharp
        public Fixture(Action<FakeConnector, int>? setup = null)
        {
```
Replace with:
```csharp
        public Fixture(Action<FakeConnector, int>? setup = null, string tileDirectory = "")
        {
```
In the same file, find:
```csharp
            }, NoSettings, new ImmediateDispatcher(), Log, new TileRuntimeOptions { Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]) });
```
Replace with:
```csharp
            }, NoSettings, new ImmediateDispatcher(), Log,
                new TileRuntimeOptions { Backoff = new BackoffSchedule([TimeSpan.FromMinutes(10)]), TileDirectory = tileDirectory });
```
In the same file, find:
```csharp
                "Configure connector…", "Refresh now", "Show connector log", "Restart tile", "",
```
Replace with:
```csharp
                "Show tile folder", "Refresh now", "Show connector log", "Restart tile", "",
```
In the same file, find:
```csharp
    [Fact]
    public async Task Configure_opens_the_config_file()
    {
        using var f = new Fixture();
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.Configure);

        Assert.Equal(new[] { f.Dir.File("config.json") }, f.Opened);
    }
```
Replace with:
```csharp
    [Fact]
    public async Task Show_tile_folder_reveals_the_kind_folder()
    {
        using var kind = new TempDir();
        using var f = new Fixture(tileDirectory: kind.Path);
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);

        Assert.Equal(new[] { kind.Path }, f.Opened);
    }

    [Fact]
    public async Task Show_tile_folder_reveals_the_config_file_when_the_folder_is_gone()
    {
        using var f = new Fixture(tileDirectory: Path.Combine(Path.GetTempPath(), "tt-gone-" + Guid.NewGuid().ToString("N")));
        await f.Runtime.StartAsync();

        await f.Commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);

        Assert.Equal(new[] { f.Dir.File("config.json") }, f.Opened);
    }
```
In the same file, find:
```csharp
        await commands.ExecuteAsync(f.Runtime, TileCommand.Configure);
```
Replace with:
```csharp
        await commands.ExecuteAsync(f.Runtime, TileCommand.ShowFolder);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests"`
Expected: build FAILS with `CS0117: 'TileCommand' does not contain a definition for 'ShowFolder'`.

- [ ] **Step 3: Write the implementation**

**Edit** `src/TaskbarTiles.Host/UI/TileCommands.cs`, find:
```csharp
public enum TileCommand { Configure, Refresh, ShowLog, Restart }
```
Replace with:
```csharp
public enum TileCommand { ShowFolder, Refresh, ShowLog, Restart }
```
In the same file, find:
```csharp
            entries.Add(new("Configure connector…", TileCommand.Configure));
```
Replace with:
```csharp
            entries.Add(new("Show tile folder", TileCommand.ShowFolder));
```
In the same file, find:
```csharp
                case TileCommand.Configure:
                    Open(configFile);
                    break;
```
Replace with:
```csharp
                case TileCommand.ShowFolder:
                    // The kind folder holds tile.json and the scripts; config.json when the folder is gone.
                    Open(Directory.Exists(runtime.TileDirectory) ? runtime.TileDirectory : configFile);
                    break;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~TileCommandsTests"`
Expected: PASS, `Failed: 0`. Then `dotnet build TaskbarTiles.sln --disable-build-servers` → `0 Warning(s)` (`TileBar` uses `entry.Tile` generically and needs no change).

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/UI/TileCommands.cs tests/TaskbarTiles.Tests/TileCommandsTests.cs
git commit -m "feat(ui): Configure connector… becomes Show tile folder (selects the kind folder in Explorer)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Watching tiles\ (restart a kind's tiles, new kinds, DLL notice)

**Files:**
- Create: `src/TaskbarTiles.Host/Plugins/KindStore.cs`
- Modify: `src/TaskbarTiles.Host/Config/ConfigStore.cs`, `src/TaskbarTiles.Host/UI/ConfigNotice.cs`, `src/TaskbarTiles.Host/App.xaml.cs`
- Test: `tests/TaskbarTiles.Tests/KindStoreTests.cs` (new), `tests/TaskbarTiles.Tests/ConfigStoreTests.cs`, `tests/TaskbarTiles.Tests/ConfigNoticeTests.cs`

**Interfaces:**
- Consumes: `KindLoader.Load/Rescan`, `KindTable.DllsChanged` (Task 1); `ConfigStore(path, log, validate)`, `KindValidation.Check`, `HostController(…, kinds)`, `HostController.ConfigChanged()` (Task 3)
- Produces:
  - `sealed class KindStore(string tilesDir, ITileLog log) : IDisposable` with `KindTable Current`, `event EventHandler<KindTable>? Changed`, `void Load()`, `void StartWatching()`, `void Rescan()`
  - `void ConfigStore.Recheck()`
  - `const string ConfigNotice.DllsChangedTitle = "Restart Taskbar Tiles"`, `static string ConfigNotice.DllsChangedBody(IReadOnlyList<string> kinds)`

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/KindStoreTests.cs`:
```csharp
using System.IO;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindStoreTests
{
    static void WriteKind(string tiles, string id, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(tiles, id)).FullName;
        File.WriteAllText(Path.Combine(dir, "tile.json"), $$"""{ "name": "{{name}}", "connector": "script", "settings": { "hooks": { "click": "x.ps1" } } }""");
        File.WriteAllText(Path.Combine(dir, "x.ps1"), "'{}'");
    }

    [Fact]
    public async Task Saving_a_tile_json_restarts_only_that_kinds_tiles()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        WriteKind(tiles.Path, "uptime", "Uptime");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changed = new TaskCompletionSource<KindTable>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, table) => changed.TrySetResult(table);
        var writer = new FakeConfigWriter("""
            { "tiles": { "p1": { "kind": "ping" }, "p2": { "kind": "ping" }, "u": { "kind": "uptime" } },
              "displays": { "primary": { "slots": [ "p1", "p2", "u" ] } } }
            """);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, kinds: () => store.Current);
        host.Start();
        var before = host.Runtimes;
        store.StartWatching();

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "tile.json"),
            """{ "name": "Ping again", "connector": "script", "settings": { "hooks": { "click": "x.ps1" } } }""");
        var table = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.ConfigChanged(); // what the app does on Changed (on the UI thread)

        Assert.Equal("Ping again", table.Kinds["ping"].Name);
        Assert.NotSame(before["p1"], host.Runtimes["p1"]);
        Assert.NotSame(before["p2"], host.Runtimes["p2"]);
        Assert.Same(before["u"], host.Runtimes["u"]);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_script_change_is_a_change_but_a_data_file_is_not()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);
        store.StartWatching();

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "cache.txt"), "written by the script");
        await Task.Delay(1000);
        Assert.Equal(0, Volatile.Read(ref changes));

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "x.ps1"), "'{ }' # edited");
        await Eventually.Holds(() => Volatile.Read(ref changes) >= 1);
    }

    [Fact]
    public async Task A_new_kind_folder_appears_in_the_table()
    {
        using var tiles = new TempDir();
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        store.StartWatching();

        WriteKind(tiles.Path, "meeting", "Meeting");

        await Eventually.Holds(() => store.Current.Find("meeting")?.Name == "Meeting");
    }

    [Fact]
    public void Rescan_without_changes_raises_nothing()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Rescan();

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Start_watching_creates_a_missing_tiles_folder()
    {
        using var data = new TempDir();
        var tiles = Path.Combine(data.Path, "tiles");
        using var store = new KindStore(tiles, new CollectingLog());
        store.Load();

        store.StartWatching();

        Assert.True(Directory.Exists(tiles));
    }
}
```

**Edit** `tests/TaskbarTiles.Tests/ConfigStoreTests.cs`, find:
```csharp
    [Fact]
    public void Load_rejects_a_config_the_validator_refuses()
```
Replace with:
```csharp
    [Fact]
    public void Recheck_applies_a_config_once_its_kind_exists_and_reports_each_outcome_once()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "a": { "kind": "ping" } } }""");
        var kinds = new HashSet<string>();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog(),
            cfg => cfg.Tiles.Values.All(t => kinds.Contains(t.Kind)) ? null : new ConfigError("missing kind"));
        store.Load();
        var changed = 0;
        var failed = 0;
        store.Changed += (_, _) => changed++;
        store.ReloadFailed += (_, _) => failed++;

        store.Recheck(); // still missing: already reported at load
        kinds.Add("ping");
        store.Recheck(); // now valid: applied
        store.Recheck(); // unchanged: nothing
        kinds.Clear();
        store.Recheck(); // the kind is gone: rejected, the last good config stays

        Assert.Equal(1, changed);
        Assert.Equal(1, failed);
        Assert.Equal("missing kind", store.LastError!.Message);
        Assert.Equal("ping", store.Current.Tiles["a"].Kind);
    }

    [Fact]
    public void Load_rejects_a_config_the_validator_refuses()
```

**Edit** `tests/TaskbarTiles.Tests/ConfigNoticeTests.cs`, find:
```csharp
    [Fact]
    public void Save_failure_says_the_file_is_unchanged()
```
Replace with:
```csharp
    [Fact]
    public void Dll_change_names_the_kind_folders_and_asks_for_a_restart()
    {
        var body = ConfigNotice.DllsChangedBody(["media", "meeting"]);

        Assert.Equal("Restart Taskbar Tiles", ConfigNotice.DllsChangedTitle);
        Assert.StartsWith(@"The DLLs in tiles\media, tiles\meeting changed while Taskbar Tiles was running.", body);
        Assert.EndsWith("Click to show the folder in Explorer.", body);
    }

    [Fact]
    public void Save_failure_says_the_file_is_unchanged()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~KindStoreTests|FullyQualifiedName~ConfigStoreTests|FullyQualifiedName~ConfigNoticeTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'KindStore' could not be found` (and `Recheck`, `DllsChangedBody` not found).

- [ ] **Step 3: Write the implementation**

**Create** `src/TaskbarTiles.Host/Plugins/KindStore.cs`:
```csharp
using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// Owns the kind table: loads it once (DLLs included) and scans tiles\ again whenever something under it changes
/// (debounced). A rescan never loads a DLL; see <see cref="KindLoader.Rescan"/>.
/// </summary>
public sealed class KindStore(string tilesDir, ITileLog log) : IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    readonly KindLoader _loader = new(log);
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;

    public KindTable Current { get; private set; } = KindTable.Empty;

    /// <summary>Raised on a thread-pool thread when a rescan changed the table: a kind's tile.json, scripts or DLLs, or the set of folders.</summary>
    public event EventHandler<KindTable>? Changed;

    public void Load()
    {
        lock (_gate) Current = _loader.Load(tilesDir);
    }

    /// <summary>Watches tiles\ and its subfolders, creating tiles\ first so kinds added later are seen.</summary>
    public void StartWatching()
    {
        Directory.CreateDirectory(tilesDir);
        _debounce = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(tilesDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.Error += (_, e) =>
        {
            log.Warn($"Watching {tilesDir} failed: {e.GetException().Message}; rescanning");
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Scans now and raises Changed when the table differs. Runs on the debounce timer; public for tests. Never throws.</summary>
    public void Rescan()
    {
        KindTable table;
        lock (_gate)
        {
            try
            {
                table = _loader.Rescan(Current);
                if (Same(Current, table)) return;
                Current = table;
            }
            catch (Exception ex)
            {
                log.Error($"Rescanning {tilesDir} failed", ex);
                return;
            }
        }
        log.Info($"Tile kinds changed: {string.Join(", ", table.Kinds.Keys)}");
        try { Changed?.Invoke(this, table); }
        catch (Exception ex) { log.Error("A tile kinds Changed handler failed", ex); }
    }

    static bool Same(KindTable a, KindTable b) =>
        a.Kinds.Count == b.Kinds.Count
        && a.FoldersWithoutTileJson.SetEquals(b.FoldersWithoutTileJson)
        && a.Kinds.All(kv => b.Kinds.TryGetValue(kv.Key, out var other)
                             && other.Stamp == kv.Value.Stamp && other.DllStamp == kv.Value.DllStamp);

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
```

**Edit** `src/TaskbarTiles.Host/Config/ConfigStore.cs`, find:
```csharp
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

                var result = Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
```
Replace with:
```csharp
    /// <summary>
    /// Validates the file again, e.g. after the kind table changed: a config rejected for a missing kind may apply now, and
    /// one in use may now name a kind that is gone (then the last good config stays). Raises Changed or ReloadFailed, on the
    /// calling thread, only when the outcome changed. Never throws.
    /// </summary>
    public void Recheck() => Reload(recheck: true);

    // Runs on a thread-pool timer (or from Recheck). Nothing may escape: an unhandled exception would kill the host.
    void Reload(bool recheck = false)
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
                if (!recheck && LastError is null && text == _appliedText) return;

                var result = Parse(text);
                if (result.Success && recheck && LastError is null && text == _appliedText) return; // still the config in use
                if (!result.Success && recheck && result.Error == LastError) return; // the same problem was already reported
                if (result.Success)
                {
                    Current = result.Config!;
```

**Edit** `src/TaskbarTiles.Host/UI/ConfigNotice.cs`, find:
```csharp
    public const string SaveFailedTitle = "Layout not saved";
```
Replace with:
```csharp
    public const string DllsChangedTitle = "Restart Taskbar Tiles";

    /// <summary>A kind's DLLs changed on disk. .NET cannot unload the loaded ones, so they load at the next start.</summary>
    public static string DllsChangedBody(IReadOnlyList<string> kinds) =>
        $"The DLLs in {string.Join(", ", kinds.Select(k => $@"tiles\{k}"))} changed while Taskbar Tiles was running. " +
        "Exit Taskbar Tiles and start it again to load them. Click to show the folder in Explorer.";

    public const string SaveFailedTitle = "Layout not saved";
```

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
    TileCommands? _commands;
    readonly TileBarRegistry _bars = new();
```
Replace with:
```csharp
    TileCommands? _commands;
    KindStore? _kinds;
    readonly TileBarRegistry _bars = new();
    readonly HashSet<string> _dllNotices = new(StringComparer.OrdinalIgnoreCase); // kinds the restart notice already named
```
In the same file, replace the lines from `var catalog = new PluginLoader(log).LoadFrom([AppPaths.PluginsDir]);` through the `log.Info($"Started with …` line (as written by Task 3) with:
```csharp
            var catalog = new PluginLoader(log).LoadFrom([AppPaths.PluginsDir]);
            var kinds = _kinds = new KindStore(AppPaths.TilesDir, log);
            kinds.Load();
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
            _config = new ConfigStore(AppPaths.ConfigFile, log, cfg => KindValidation.Check(cfg, kinds.Current));
            _config.Load();
            _commands = new TileCommands(AppPaths.ConfigFile, AppPaths.LogsDir, RevealInExplorer, log);

            var ui = new WpfUiDispatcher(Dispatcher);
            _tracker = new TaskbarTracker(Dispatcher);
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir, _config, () => kinds.Current);

            _config.Changed += (_, _) => ui.Post(() =>
            {
                NotificationWindow.CloseOpen(); // a notice about an earlier bad edit is stale now
                _host.ConfigChanged();
            });
            _config.ReloadFailed += (_, error) => ui.Post(() => ShowConfigNotice(error, atStartup: false));
            _config.StartWatching();
            kinds.Changed += (_, table) => ui.Post(() => OnKindsChanged(table));
            kinds.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            if (_config.LastError is { } startupError) ShowConfigNotice(startupError, atStartup: true);
            log.Info($"Started with {catalog.Connectors.Count} built-in connector(s) and {kinds.Current.Kinds.Count} tile kind(s): {string.Join(", ", kinds.Current.Kinds.Keys)}");
```
In the same file, find:
```csharp
    void ShowConfigNotice(ConfigError error, bool atStartup)
    {
        var log = FileLog.For(AppPaths.LogsDir, "host");
        try
        {
            var light = _tracker?.Current.FirstOrDefault(t => t.IsPrimary)?.IsLightTheme ?? false;
            NotificationWindow.Show(ConfigNotice.Title, ConfigNotice.Body(error, atStartup), light,
                () => _commands?.OpenConfig(), log);
        }
        catch (Exception ex) { log.Error("Could not show the config notice", ex); }
    }
```
Replace with:
```csharp
    void ShowConfigNotice(ConfigError error, bool atStartup) =>
        ShowNotice(ConfigNotice.Title, ConfigNotice.Body(error, atStartup), () => _commands?.OpenConfig());

    void ShowNotice(string title, string body, Action onClick)
    {
        var log = FileLog.For(AppPaths.LogsDir, "host");
        try
        {
            var light = _tracker?.Current.FirstOrDefault(t => t.IsPrimary)?.IsLightTheme ?? false;
            NotificationWindow.Show(title, body, light, onClick, log);
        }
        catch (Exception ex) { log.Error($"Could not show the notice \"{title}\"", ex); }
    }

    // UI thread, after something in tiles\ changed.
    void OnKindsChanged(KindTable table)
    {
        _config?.Recheck();     // a config rejected for a missing kind may apply now; one naming a removed kind is rejected
        _host?.ConfigChanged(); // restarts the tiles of kinds whose tile.json or scripts changed; new kinds reach the picker
        var fresh = table.DllsChanged.Where(_dllNotices.Add).ToList();
        if (fresh.Count == 0) return;
        // Queued, so it shows after the CloseOpen that a Recheck which fixed the config has queued.
        Dispatcher.BeginInvoke(() => ShowNotice(ConfigNotice.DllsChangedTitle, ConfigNotice.DllsChangedBody(fresh),
            () => RevealInExplorer(Path.Combine(AppPaths.TilesDir, fresh[0]))));
    }
```
In the same file, find:
```csharp
        _tracker?.Dispose();
        _config?.Dispose();
```
Replace with:
```csharp
        _tracker?.Dispose();
        _kinds?.Dispose();
        _config?.Dispose();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~KindStoreTests|FullyQualifiedName~ConfigStore|FullyQualifiedName~ConfigNoticeTests"`
Expected: PASS, `Failed: 0`.

Then: `dotnet test --disable-build-servers TaskbarTiles.sln` → `Failed: 0`; `dotnet build TaskbarTiles.sln --disable-build-servers` → `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Plugins/KindStore.cs src/TaskbarTiles.Host/Config/ConfigStore.cs src/TaskbarTiles.Host/UI/ConfigNotice.cs src/TaskbarTiles.Host/App.xaml.cs tests/TaskbarTiles.Tests/KindStoreTests.cs tests/TaskbarTiles.Tests/ConfigStoreTests.cs tests/TaskbarTiles.Tests/ConfigNoticeTests.cs
git commit -m "feat(kinds): watch tiles\; restart only the edited kind's tiles, recheck config, notice on DLL changes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Bundled kinds and the app layout (tiles\ in the build, no scripts\)

**Files:**
- Create: `tiles/uptime/tile.json`, `tiles/ping/tile.json`, `tiles/media/tile.json`, `tests/TaskbarTiles.Tests/BundledKindsTests.cs`
- Move: `scripts/uptime.ps1` → `tiles/uptime/uptime.ps1`, `scripts/ping.ps1` → `tiles/ping/ping.ps1` (`scripts/` is then gone)
- Delete: `src/TaskbarTiles.Host/SampleScripts.cs`, `tests/TaskbarTiles.Tests/SampleScriptsTests.cs`
- Modify: `build/CopyPlugins.targets`, `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`, `src/TaskbarTiles.Host/AppPaths.cs`, `src/TaskbarTiles.Host/App.xaml.cs`, `tools/install.ps1` (its verify list only)

**Interfaces:**
- Consumes: `KindLoader.Load` (Task 1), `PluginLoader.LoadFrom`, `ScriptSettings.Parse`, `CommandResolver`, `ProcessScriptRunner`, `HookResults`, `ExecutableLocator.PowerShell()`
- Produces: `PluginProject` item metadata `Kind` in `CopyPlugins.targets`; build and publish output `tiles\{uptime,ping,media}\` (media with `TaskbarTiles.Plugins.Media.dll`) and `plugins\TaskbarTiles.Plugins.Script\` only; `AppPaths.ScriptsDir`, `AppPaths.SampleScriptsDir`, `SampleScripts` no longer exist

- [ ] **Step 1: Write the failing tests**

**Create** `tests/TaskbarTiles.Tests/BundledKindsTests.cs`:
```csharp
using System.IO;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

/// <summary>The kinds in the repo's tiles\ folder, as the build copies them (with the media plugin) to the output.</summary>
public class BundledKindsTests
{
    static string BundledTiles => Path.Combine(AppContext.BaseDirectory, "tiles");

    [Fact]
    public void Every_bundled_kind_parses_and_names_a_connector_that_exists()
    {
        var kinds = new KindLoader(new CollectingLog()).Load(BundledTiles);
        var builtIn = new PluginLoader(new CollectingLog()).LoadFrom([Path.Combine(AppContext.BaseDirectory, "plugins")]);

        Assert.Equal(new[] { "media", "ping", "uptime" }, kinds.Kinds.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(kinds.FoldersWithoutTileJson);
        foreach (var kind in kinds.Kinds.Values)
        {
            Assert.True(kind.Error is null, $"{kind.Id}: {kind.Error}");
            Assert.False(string.IsNullOrWhiteSpace(kind.Description), $"{kind.Id} has no description");
            if (kind.Connector == TileKind.ScriptConnectorId) Assert.True(builtIn.Connectors.ContainsKey(TileKind.ScriptConnectorId));
            else Assert.Equal(kind.Connector, kind.Plugin!.Id);
        }
        Assert.Equal("media", kinds.Kinds["media"].Plugin!.Id);
    }

    [Fact]
    public void Script_kinds_have_valid_hooks_that_run_files_in_their_folder()
    {
        var kinds = new KindLoader(new CollectingLog()).Load(BundledTiles);

        foreach (var kind in kinds.Kinds.Values.Where(k => k.Connector == TileKind.ScriptConnectorId))
        {
            var settings = ScriptSettings.Parse(kind.Settings);
            Assert.Empty(settings.Errors);
            foreach (var hook in settings.Hooks.Values)
                Assert.True(File.Exists(Path.Combine(kind.Directory, hook.Run)), $"{kind.Id}: {hook.Run} is missing");
        }
    }

    static async Task<ScriptResult> RunBundled(string kind, string script, string hook, string settingsJson = "{}")
    {
        var command = new CommandResolver(Path.Combine(BundledTiles, kind), ExecutableLocator.PowerShell()).Resolve(script);
        var env = new Dictionary<string, string>
        {
            ["TILE_ID"] = kind, ["TILE_HOOK"] = hook, ["TILE_ACTION"] = "", ["TILE_SETTINGS_JSON"] = settingsJson,
        };
        return await new ProcessScriptRunner().RunAsync(command, env, TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_prints_a_clock_tile()
    {
        var outcome = HookResults.ForTile("interval", await RunBundled("uptime", "uptime.ps1", "interval"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Matches(@"^\d{2}:\d{2}$", outcome.Value!.Title);
        Assert.StartsWith("up ", outcome.Value.Subtitle);
        Assert.Equal("clock", outcome.Value.Icon!.Glyph);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Uptime_prints_a_popup()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunBundled("uptime", "uptime.ps1", "flyout"), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(new[] { "Time", "Booted", "Uptime" }, outcome.Value!.Rows.Select(r => r.Text));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_prints_a_status_tile_with_a_button()
    {
        var outcome = HookResults.ForTile("interval", await RunBundled("ping", "ping.ps1", "interval", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal("127.0.0.1", outcome.Value!.Title);
        Assert.EndsWith(" ms", outcome.Value.Subtitle);
        Assert.Equal("ping", Assert.Single(outcome.Value.Buttons).Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ping_prints_a_popup_with_an_action()
    {
        var outcome = HookResults.ForFlyout("flyout", await RunBundled("ping", "ping.ps1", "flyout", """{ "host": "127.0.0.1" }"""), TimeSpan.FromSeconds(30));

        Assert.False(outcome.Failed, $"{outcome.ErrorMessage}\n{outcome.ErrorDetails}");
        Assert.Empty(outcome.Warnings);
        Assert.Equal(4, outcome.Value!.Rows.Count);
        Assert.Equal("ping", Assert.Single(outcome.Value.Actions).Id);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~BundledKindsTests"`
Expected: FAIL — `Every_bundled_kind_parses_and_names_a_connector_that_exists` (`Assert.Equal() Failure`: no kinds, the output has no `tiles\`), `Script_kinds_…` passes vacuously, the four integration tests FAIL (script not found). That `app\plugins` no longer holds the media plugin is checked on the publish output in Step 5.

- [ ] **Step 3: Move the scripts and add the tile.json files**

Run:
```bash
mkdir -p tiles/uptime tiles/ping tiles/media
git mv scripts/uptime.ps1 tiles/uptime/uptime.ps1
git mv scripts/ping.ps1 tiles/ping/ping.ps1
git rm src/TaskbarTiles.Host/SampleScripts.cs tests/TaskbarTiles.Tests/SampleScriptsTests.cs
```
Expected: `scripts/` no longer exists (`git ls-files scripts` prints nothing).

**Edit** `tiles/uptime/uptime.ps1`, find:
```powershell
# Taskbar Tiles sample: clock and uptime. Hooks: interval, flyout. See docs/script-connectors.md.
```
Replace with:
```powershell
# Taskbar Tiles bundled kind "uptime": clock and uptime. Hooks (tile.json): interval, flyout. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
```

**Edit** `tiles/ping/ping.ps1`, find:
```powershell
# Taskbar Tiles sample: ping a host. Hooks: interval, flyout, action:ping. See docs/script-connectors.md.
```
Replace with:
```powershell
# Taskbar Tiles bundled kind "ping": ping a host. Hooks (tile.json): interval, flyout, action:ping. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
```

**Create** `tiles/uptime/tile.json`:
```json
{
  "name": "Clock and uptime",
  "description": "The time, and how long Windows has been up",
  "connector": "script",
  "settings": {
    "hooks": {
      "interval": { "every": "30s", "run": "uptime.ps1" },
      "flyout":   { "run": "uptime.ps1" }
    }
  }
}
```

**Create** `tiles/ping/tile.json`:
```json
{
  "name": "Ping a host",
  "description": "Pings a host every 15 s",
  "connector": "script",
  "settings": {
    "host": "1.1.1.1",
    "hooks": {
      "interval":    { "every": "15s", "run": "ping.ps1" },
      "flyout":      { "run": "ping.ps1" },
      "action:ping": { "run": "ping.ps1" }
    }
  }
}
```

**Create** `tiles/media/tile.json`:
```json
{
  "name": "Media",
  "description": "Now playing in an app's media controls (Spotify by default)",
  "connector": "media",
  "settings": { "app": "Spotify" }
}
```

- [ ] **Step 4: Build layout**

**Replace the whole file** `build/CopyPlugins.targets` with:
```xml
<Project>
  <!--
    Copies each @(PluginProject)'s build output next to this project's output: to plugins\<AssemblyName>\, or to tiles\<Kind>\
    when the item has Kind metadata (a connector that ships as a tile kind; the kind's tile.json comes from the repo's tiles\).
    The MSBuild task copies the item's metadata (Kind) onto its TargetOutputs.
  -->
  <Target Name="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <MSBuild Projects="@(PluginProject)" Targets="GetTargetPath" Properties="Configuration=$(Configuration)">
      <Output TaskParameter="TargetOutputs" ItemName="_PluginTargetPath" />
    </MSBuild>
    <ItemGroup>
      <_PluginFile Include="%(_PluginTargetPath.RootDir)%(_PluginTargetPath.Directory)**\*.*"
                   PluginDir="plugins\%(_PluginTargetPath.Filename)" Condition="'%(_PluginTargetPath.Kind)' == ''" />
      <_PluginFile Include="%(_PluginTargetPath.RootDir)%(_PluginTargetPath.Directory)**\*.*"
                   PluginDir="tiles\%(_PluginTargetPath.Kind)" Condition="'%(_PluginTargetPath.Kind)' != ''" />
    </ItemGroup>
  </Target>

  <Target Name="CopyPluginsToOutput" AfterTargets="Build" DependsOnTargets="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <Copy SourceFiles="@(_PluginFile)"
          DestinationFiles="@(_PluginFile->'$(OutDir)%(PluginDir)\%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>

  <Target Name="CopyPluginsToPublish" AfterTargets="Publish" DependsOnTargets="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <Copy SourceFiles="@(_PluginFile)"
          DestinationFiles="@(_PluginFile->'$(PublishDir)%(PluginDir)\%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>
</Project>
```

**Edit** `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, find:
```xml
  <ItemGroup>
    <!-- Build order only; each is copied to plugins\<AssemblyName>\ by CopyPlugins.targets. -->
    <ProjectReference Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" />
    <ProjectReference Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Sample scripts next to the exe; SampleScripts copies them to %LOCALAPPDATA%\TaskbarTiles\scripts on first run. -->
    <None Include="..\..\scripts\*.ps1" Link="scripts\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```
Replace with:
```xml
  <ItemGroup>
    <!-- Build order only; CopyPlugins.targets copies each one: media to tiles\media\, script to plugins\<AssemblyName>\. -->
    <ProjectReference Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" Kind="media" />
    <ProjectReference Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- The bundled kinds next to the exe (build and publish); install.ps1 copies them to %LOCALAPPDATA%\TaskbarTiles\tiles. -->
    <None Include="..\..\tiles\**\*" Link="tiles\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

**Edit** `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`, find:
```xml
    <!-- Also laid out like the host's plugins folder so the loader test can discover it. -->
    <PluginProject Include="..\..\src\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
  </ItemGroup>
```
Replace with:
```xml
    <!-- Also laid out like the host's plugins folder so the loader test can discover it. -->
    <PluginProject Include="..\..\src\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.csproj" />
    <!-- The bundled kinds, laid out as in the app (media plugin in tiles\media\), for BundledKindsTests. -->
    <ProjectReference Include="..\..\src\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\..\src\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" Kind="media" />
    <!-- tiles\**\* itself arrives through the Host reference: its None items with CopyToOutputDirectory are copied here too. -->
  </ItemGroup>
```

**Edit** `src/TaskbarTiles.Host/AppPaths.cs`, find:
```csharp
    /// <summary>The user's scripts; "run" paths such as scripts/x.ps1 resolve against DataDir.</summary>
    public static string ScriptsDir => Path.Combine(DataDir, "scripts");

    /// <summary>Sample scripts shipped next to the exe.</summary>
    public static string SampleScriptsDir => Path.Combine(AppContext.BaseDirectory, "scripts");

```
Replace with nothing (delete these six lines; an existing `%LOCALAPPDATA%\TaskbarTiles\scripts` folder is left alone, nothing reads or deletes it).

**Edit** `src/TaskbarTiles.Host/App.xaml.cs`, find:
```csharp
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
```
Replace with nothing (delete the line).

**Edit** `tools/install.ps1`, find:
```powershell
foreach ($required in @((Join-Path $stage 'TaskbarTiles.exe'),
        (Join-Path $stage 'plugins\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $stage 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'))) {
```
Replace with:
```powershell
foreach ($required in @((Join-Path $stage 'TaskbarTiles.exe'),
        (Join-Path $stage 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'),
        (Join-Path $stage 'tiles\media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $stage 'tiles\media\tile.json'),
        (Join-Path $stage 'tiles\ping\tile.json'),
        (Join-Path $stage 'tiles\uptime\tile.json'))) {
```

- [ ] **Step 5: Run the tests and check the publish layout**

Run: `dotnet test --disable-build-servers tests/TaskbarTiles.Tests --filter "FullyQualifiedName~BundledKindsTests|FullyQualifiedName~PluginLoaderTests"`
Expected: PASS, `Failed: 0` (the old build output may still hold `plugins\TaskbarTiles.Plugins.Media\` in `src\TaskbarTiles.Host\bin`; the tests project never had it).

Then (PowerShell, repo root):
```powershell
Remove-Item artifacts\publish-check -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src\TaskbarTiles.Host -c Release -o artifacts\publish-check --nologo --disable-build-servers
'tiles\media\TaskbarTiles.Plugins.Media.dll','tiles\media\tile.json','tiles\ping\ping.ps1','tiles\ping\tile.json','tiles\uptime\uptime.ps1','tiles\uptime\tile.json','plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll','plugins\TaskbarTiles.Plugins.Media','scripts' |
    ForEach-Object { '{0} {1}' -f (Test-Path (Join-Path artifacts\publish-check $_)), $_ }
```
Expected: `True` for the first seven lines, `False plugins\TaskbarTiles.Plugins.Media`, `False scripts`.

Then: `dotnet test --disable-build-servers TaskbarTiles.sln` → `Failed: 0`; `dotnet build TaskbarTiles.sln --disable-build-servers` → `0 Warning(s)`; `git grep -n "SampleScripts\|ScriptsDir" -- src tests build` and `git grep -n "scripts/" -- src build` print nothing (test scripts written to temp folders, such as `scripts/tick.ps1` in `ScriptConnectorIntegrationTests`, are fine).

- [ ] **Step 6: Commit**

```bash
git add -A tiles scripts build src/TaskbarTiles.Host tests/TaskbarTiles.Tests tools/install.ps1
git commit -m "feat(kinds): bundled kinds uptime, ping and media in tiles\; media plugin ships in tiles\media; scripts\ and SampleScripts removed

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: install.cmd and refreshing the bundled kinds

**Files:**
- Create: `install.cmd`
- Modify: `tools/install.ps1`

**Interfaces:**
- Consumes: the publish layout of Task 6 (`<stage>\tiles\{uptime,ping,media}\`)
- Produces: `install.ps1 -TilesDir <path>` (default `%LOCALAPPDATA%\TaskbarTiles\tiles`); after the swap, `<TilesDir>\uptime`, `ping`, `media` are deleted and copied from `<AppDir>\tiles\`; other folders in `<TilesDir>` are never touched

- [ ] **Step 1: Write install.cmd**

**Create** `install.cmd` (repo root), exactly:
```bat
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
if errorlevel 1 pause
```

- [ ] **Step 2: Update install.ps1**

**Edit** `tools/install.ps1`, find:
```powershell
.SYNOPSIS
    Builds Taskbar Tiles and installs it for the current user.

.DESCRIPTION
    Run it from your own terminal (not from an agent session: the Claude desktop app redirects an agent's writes
    under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the installed app.

    1. Publishes src\TaskbarTiles.Host (Release) into a staging folder next to the install folder and verifies it.
    2. Stops TaskbarTiles.exe if it runs from the install folder.
    3. Swaps the staging folder in for the install folder.
    4. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    5. Starts the installed app.

    If the publish fails, the running app and the installed copy are left untouched.

    Your config.json, scripts, plugins and logs in %LOCALAPPDATA%\TaskbarTiles are never touched.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.
```
Replace with:
```powershell
.SYNOPSIS
    Builds Taskbar Tiles and installs it for the current user. install.cmd in the repository root runs this script.

.DESCRIPTION
    Run it from your own terminal or by double-clicking install.cmd (not from an agent session: the Claude desktop app
    redirects an agent's writes under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the
    installed app and the bundled tile kinds.

    1. Publishes src\TaskbarTiles.Host (Release) into a staging folder next to the install folder and verifies it.
    2. Stops TaskbarTiles.exe if it runs from the install folder.
    3. Swaps the staging folder in for the install folder.
    4. Replaces the bundled tile kinds uptime, ping and media in the tiles folder with the new copies.
    5. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    6. Starts the installed app.

    If the publish fails, the running app and the installed copy are left untouched.

    Your config.json, logs and every other folder in tiles\ (your own kinds) are never touched. To change a bundled
    kind, copy its folder under a new name first: install replaces tiles\uptime, tiles\ping and tiles\media.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.

.PARAMETER TilesDir
    Tile kinds folder whose bundled kinds are replaced. Default: %LOCALAPPDATA%\TaskbarTiles\tiles.
```
In the same file, find:
```powershell
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install.ps1
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [switch]$NoShortcut,
    [switch]$NoStart
)
```
Replace with:
```powershell
.EXAMPLE
    install.cmd

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install.ps1 -AppDir D:\tmp\app -TilesDir D:\tmp\tiles -NoShortcut -NoStart
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [string]$TilesDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\tiles'),
    [switch]$NoShortcut,
    [switch]$NoStart
)
```
In the same file, find:
```powershell
$stage = "$AppDir.new"
$old = "$AppDir.old"
```
Replace with:
```powershell
$stage = "$AppDir.new"
$old = "$AppDir.old"
$TilesDir = [System.IO.Path]::GetFullPath($TilesDir).TrimEnd('\')
$bundledKinds = @('uptime', 'ping', 'media') # replaced on every install; other folders in tiles\ are the user's
```
In the same file, find:
```powershell
Write-Host "Installed $exe"

# 4. Startup shortcut (shows in Task Manager > Startup apps).
```
Replace with:
```powershell
Write-Host "Installed $exe"

# 4. Replace the bundled tile kinds (the app is stopped, as for the swap). Other folders in tiles\ are never touched.
New-Item -ItemType Directory -Force -Path $TilesDir | Out-Null
foreach ($kind in $bundledKinds) {
    $source = Join-Path $AppDir "tiles\$kind"
    $target = Join-Path $TilesDir $kind
    try {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Copy-Item -LiteralPath $source -Destination $target -Recurse
    } catch {
        throw "Could not replace $target : $($_.Exception.Message) The new app is installed but was not started; close whatever uses that folder and run the install again."
    }
    Write-Host "Replaced tile kind $kind in $TilesDir"
}

# 5. Startup shortcut (shows in Task Manager > Startup apps).
```
In the same file, find:
```powershell
# 5. Start.
```
Replace with:
```powershell
# 6. Start.
```

- [ ] **Step 3: Test the install against a scratch tiles folder**

Never pass the default `-TilesDir` or `-AppDir` from an agent session. Run (PowerShell, repo root):
```powershell
$check = 'D:\Projects\taskbar-tiles\artifacts\tiles-check'
Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$check\ping", "$check\meeting" | Out-Null
Set-Content "$check\ping\stale.txt" 'from an older install'
Set-Content "$check\meeting\tile.json" '{ "name": "Meeting", "connector": "script" }'
cmd /c install.cmd -AppDir D:\Projects\taskbar-tiles\artifacts\app -TilesDir $check -NoShortcut -NoStart
"exit $LASTEXITCODE"
'ping\stale.txt','ping\tile.json','ping\ping.ps1','uptime\tile.json','media\tile.json','media\TaskbarTiles.Plugins.Media.dll','meeting\tile.json' |
    ForEach-Object { '{0} {1}' -f (Test-Path (Join-Path $check $_)), $_ }
Get-Content "$check\meeting\tile.json"
```
Expected: the output ends with `Replaced tile kind uptime in D:\Projects\taskbar-tiles\artifacts\tiles-check` (also `ping`, `media`), `Skipped the Startup shortcut (-NoShortcut).`, `Not started (-NoStart).`, `exit 0`; then `False ping\stale.txt` and `True` for the other six; the last line is `{ "name": "Meeting", "connector": "script" }` (untouched).

- [ ] **Step 4: Commit**

```bash
git add install.cmd tools/install.ps1
git commit -m "feat(install): install.cmd; install.ps1 -TilesDir refreshes the bundled kinds after the app swap

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Docs: tile kinds, tile.json, install.cmd, Show tile folder

**Files:**
- Modify: `docs/configuration.md`, `docs/script-connectors.md`

**Interfaces:**
- Consumes: the behaviour of Tasks 1-7 (texts must match: error messages, menu labels, folder names)
- Produces: user docs

- [ ] **Step 1: docs/configuration.md**

**Edit** `docs/configuration.md`, find:
```markdown
Everything lives in `%LOCALAPPDATA%\TaskbarTiles\config.json`. Right-click a tile → **Show config file** shows it in Explorer. The file is watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).
```
Replace with:
```markdown
Your tiles and layout live in `%LOCALAPPDATA%\TaskbarTiles\config.json`; what each kind of tile does lives in its own folder under `%LOCALAPPDATA%\TaskbarTiles\tiles\`. Right-click a tile → **Show config file** shows `config.json` in Explorer, **Show tile folder** that tile's kind folder. Both are watched: a saved edit applies within a second, and an edit with a mistake is rejected with a notice (the last good config stays active).

Script tiles are described in [script-connectors.md](script-connectors.md).
```

In the same file, find:
```markdown
## Install or update

From your own terminal, in the repository:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

It publishes a Release build into a staging folder next to the install folder and verifies it (a failed build changes nothing), then stops the installed copy, swaps the new folder in for `%LOCALAPPDATA%\TaskbarTiles\app` (so no stale files stay), creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, scripts, plugins and logs are never touched. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.
```
Replace with:
```markdown
## Install or update

Double-click `install.cmd` in the repository. On success its window closes and the app starts; on failure it stays open so you can read the error. (From a terminal: `install.cmd`, or `powershell -ExecutionPolicy Bypass -File tools\install.ps1`.)

It publishes a Release build into a staging folder next to the install folder and verifies it (a failed build changes nothing), then stops the installed copy, swaps the new folder in for `%LOCALAPPDATA%\TaskbarTiles\app` (so no stale files stay), replaces the bundled tile kinds `tiles\uptime`, `tiles\ping` and `tiles\media` with the new copies, creates or updates the `TaskbarTiles` shortcut in your Startup folder, and starts the app. Run it again after every update. Your `config.json`, your logs and every other folder in `tiles\` are never touched. To change a bundled kind, copy its folder under a new name first (for example `tiles\ping` to `tiles\my-ping`): install replaces the bundled ones. A `scripts` folder left over from an older version is not used any more and is left alone. Do not run it from an agent session in the Claude desktop app: the app redirects an agent's writes under `%LOCALAPPDATA%` to a private copy, so the install would not land where Windows starts it.
```

In the same file, find:
```markdown
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
```
Replace with:
```markdown
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
```

In the same file, find:
```markdown
## Stacks
```
Replace with:
```markdown
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
- **Edits apply by themselves:** saving `tile.json` or a script restarts only the tiles of that kind. Files a script writes into its own folder (logs, caches) do not restart it.
- **Mistakes:** a `tile.json` that is not valid JSON or lacks `name` or `connector`, a DLL that does not load, two DLLs declaring the same connector id, or a `connector` no DLL in the folder declares makes the kind broken: its tiles show the error (naming the file, and the line for JSON errors), it is written to `logs\host.log`, and every other kind keeps working. `config.json` is rejected only for its own mistakes: a tile naming a kind with no folder in `tiles\` (or a folder without `tile.json`), `hooks` in a tile's settings, or the old form `{ "connector": … }` (tiles from before tile folders: rewrite them as `{ "kind": "<folder>" }`).
- A folder in `tiles\` without `tile.json` is not a kind.

## Stacks
```

In the same file, find:
```markdown
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its definition under `tiles` is deleted in the same save; the previous version stays in `config.json.bak` until the next save. Tiles you defined by hand but have not placed anywhere yet are never deleted, and `+` lists them first.
- `+` adds a tile at the end: a tile you already have that this display does not show, a new tile from a loaded plugin, or a new script tile from one of the sample scripts. New tiles get a free id such as `ping-2`.
```
Replace with:
```markdown
- `×` removes the tile from this display. On a stack it removes the member shown (scroll the wheel to pick one); a stack left with one member becomes a plain tile. When no display entry (including `*`, and entries of monitors that are not connected) places the tile any more, its entry under `tiles` is deleted in the same save; the previous version stays in `config.json.bak` until the next save. Its kind folder in `tiles\` is never deleted. Tiles you listed by hand but have not placed anywhere yet are never deleted, and `+` lists them first.
- `+` adds a tile at the end: a tile you already have that this display does not show, or a new tile of any kind in `tiles\` (listed by its `name`, with its `description` below). A new tile is written as `"ping": { "kind": "ping" }`, or with a free id such as `ping-2` when that id is taken.
```

In the same file, find:
```markdown
- **Remove tile** works like `×`.
```
Replace with:
```markdown
- **Remove tile** works like `×`.
- **Show tile folder** selects the tile's kind folder in Explorer; **Show config file** selects `config.json`.
```

- [ ] **Step 2: docs/script-connectors.md**

**Edit** `docs/script-connectors.md`, find:
```markdown
The built-in `script` connector drives a tile from scripts. You wire scripts to *hooks* in `config.json`; each script prints JSON that becomes the tile or its popup. PowerShell and Python get special handling; anything else runs through `cmd.exe`, so batch files and any program on `PATH` work too.

## Quick start

1. Put a script in `%LOCALAPPDATA%\TaskbarTiles\scripts\`. On first run (only when that folder does not exist yet) Taskbar Tiles copies two samples there: `uptime.ps1` (clock and uptime) and `ping.ps1` (ping a host, with a button and a popup). They are never copied again, so edit or delete them freely.
2. Add a tile and place it on a display in `%LOCALAPPDATA%\TaskbarTiles\config.json` (right-click a tile → Configure connector… shows it in Explorer):

```jsonc
{
  "tiles": {
    "uptime": { "connector": "script", "settings": { "hooks": {
                  "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                  "flyout":   { "run": "scripts/uptime.ps1" } } } },
    "ping":   { "connector": "script", "settings": { "host": "1.1.1.1", "hooks": {
                  "interval":    { "every": "15s", "run": "scripts/ping.ps1" },
                  "flyout":      { "run": "scripts/ping.ps1" },
                  "action:ping": { "run": "scripts/ping.ps1" } } } }
  },
  "displays": {
    "primary": { "slots": [ "uptime", "ping" ] }
  }
}
```

The default `config.json` (written only when none exists) already contains the `uptime` tile.

3. Save. The file is reloaded automatically. If it does not parse, a notice above the taskbar names the line and column, and the last good config stays active.

## Hooks

`settings.hooks` maps hook names to `{ "run": "...", "timeout": "20s" }` (`interval` also takes `every`; any other key is an error). Except for `interval`, a plain string is short for `{ "run": "..." }`.
```
Replace with:
```markdown
The built-in `script` connector drives a tile from scripts. A script tile kind is a folder in `%LOCALAPPDATA%\TaskbarTiles\tiles\` with a `tile.json` whose `"connector"` is `"script"`; its `settings.hooks` wire scripts in that folder to *hooks*, and each script prints JSON that becomes the tile or its popup. PowerShell and Python get special handling; anything else runs through `cmd.exe`, so batch files and any program on `PATH` work too. Tile kinds in general are described in [configuration.md](configuration.md#tile-kinds).

## Quick start

Two script kinds come with the app: `tiles\uptime` (clock and uptime) and `tiles\ping` (ping a host, with a button and a popup). Install replaces them, so to change one, copy its folder under a new name first. To make your own:

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
```

In the same file, find:
```markdown
`run` is a script path relative to the config folder (`%LOCALAPPDATA%\TaskbarTiles`), an absolute path, or a command line. The first word is the script; quote it if it contains spaces. Everything after it is passed on as arguments.
```
Replace with:
```markdown
`run` is a script path relative to the kind folder (`%LOCALAPPDATA%\TaskbarTiles\tiles\<kind>`), an absolute path, or a command line. The first word is the script; quote it if it contains spaces. Everything after it is passed on as arguments.
```

In the same file, find:
```markdown
Scripts run hidden, with the config folder as the working directory and no input (stdin is closed).
```
Replace with:
```markdown
Scripts run hidden, with the kind folder as the working directory and no input (stdin is closed).
```

In the same file, find:
```markdown
| `TILE_SETTINGS_JSON` | The tile's whole `settings` object as JSON, including `hooks`. Put your own options next to `hooks` (like `"host"` above). |
```
Replace with:
```markdown
| `TILE_SETTINGS_JSON` | The tile's effective settings as JSON: the kind's `settings` from `tile.json` (including `hooks`) with the tile's own `settings` from `config.json` replacing keys of the same name. Put your own options next to `hooks` (like `"host"` in `tiles\ping\tile.json`). |
```

In the same file, find:
```markdown
One script can serve several hooks by looking at `TILE_HOOK`, as both samples do.
```
Replace with:
```markdown
One script can serve several hooks by looking at `TILE_HOOK`, as both bundled kinds do.
```

In the same file, find:
```markdown
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`, an unknown setting inside a hook such as `"timout"`, or `every` on a hook other than `interval`) put the tile in the error look and nothing runs until `config.json` is fixed.
```
Replace with:
```markdown
- Invalid hook settings (unknown hook name, `interval` without `every`, a missing `run`, an unknown setting inside a hook such as `"timout"`, or `every` on a hook other than `interval`) put the tile in the error look and nothing runs until `tile.json` is fixed.
- A `tile.json` that is not valid JSON or lacks `name` or `connector` shows its error (with the line for JSON errors) on every tile of that kind; other kinds keep working.
```

In the same file, find:
```markdown
- Every failure is written to `%LOCALAPPDATA%\TaskbarTiles\logs\tile-<id>.log`. Right-click the tile → **Show connector log** shows it selected in Explorer.
```
Replace with:
```markdown
- Every failure is written to `%LOCALAPPDATA%\TaskbarTiles\logs\tile-<id>.log`. Right-click the tile → **Show connector log** shows it selected in Explorer; **Show tile folder** shows the kind folder with its `tile.json` and scripts.
```

In the same file, find:
```markdown
Python (`interval`), `scripts/disk.py`, with `"run": "scripts/disk.py"`:
```
Replace with:
```markdown
Python (`interval`), `tiles\disk\disk.py`, with `"run": "disk.py"` in `tiles\disk\tile.json`:
```

In the same file, find:
```markdown
A command line instead of a script file:

```jsonc
"hello": { "connector": "script", "settings": { "hooks": {
             "startup": "echo {\"title\":\"Hello\",\"icon\":\"info\"}" } } }
```

A `watch` script that pushes an update every 5 seconds, `scripts/watch-time.ps1`:
```
Replace with:
```markdown
A command line instead of a script file, as the whole `tiles\hello\tile.json`:

```jsonc
{ "name": "Hello", "connector": "script", "settings": { "hooks": {
    "startup": "echo {\"title\":\"Hello\",\"icon\":\"info\"}" } } }
```

A `watch` script that pushes an update every 5 seconds, `tiles\clock\watch-time.ps1`, with `"watch": { "run": "watch-time.ps1" }`:
```

- [ ] **Step 3: Check the docs**

Run: `git grep -n "connector\": \"script\", \"settings\": { \"host\|scripts/\|scripts\\\\\|Configure connector\|sample script\|\"connector\": \"media\"" -- docs/configuration.md docs/script-connectors.md`
Expected: no output (the only `scripts` left is the sentence about a leftover `scripts` folder, which has no slash).

- [ ] **Step 4: Commit**

```bash
git add docs/configuration.md docs/script-connectors.md
git commit -m "docs: tile kinds, tile.json, install.cmd and Show tile folder

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Verify on screen with the user

**Files:**
- Outside the repo (user only): `%LOCALAPPDATA%\TaskbarTiles\app\`, `%LOCALAPPDATA%\TaskbarTiles\tiles\`, `%LOCALAPPDATA%\TaskbarTiles\config.json`

**Interfaces:**
- Consumes: everything above
- Produces: the user's confirmation that tile folders work on the real taskbars

The agent prepares; the user installs and checks (Steps 2-6) and reports back. Do not edit the user's `config.json`, do not run `install.cmd`/`tools\install.ps1` against `%LOCALAPPDATA%` and do not start or stop `TaskbarTiles.exe` from an agent session.

- [ ] **Step 1 (agent): Final local check**

```powershell
dotnet build TaskbarTiles.sln --disable-build-servers
dotnet test TaskbarTiles.sln --disable-build-servers
```
Expected: `0 Warning(s)`; `Failed: 0`.

- [ ] **Step 2 (user): Back up, rewrite the config, install**

The old `config.json` uses `"connector"` and will be rejected (no migration). In the user's own terminal:
```powershell
Copy-Item "$env:LOCALAPPDATA\TaskbarTiles\config.json" "$env:LOCALAPPDATA\TaskbarTiles\config.before-tile-folders.json"
```
Then double-click `install.cmd` in the repo. Expected: the window closes, the app starts, and a notice says `Tile '<id>' uses "connector"; tiles now name a kind: "kind": "<folder>"` (the last good config is the empty one at startup, so no tiles show). Rewrite each tile in `config.json` as `{ "kind": "uptime" | "ping" | "media" }` (moving a `"host"` or `"app"` into the tile's `settings`), or delete `config.json` to get the default; save. Expected: the tiles appear within a second.

- [ ] **Step 3 (user): Picker and menu**

Edit layout → `+`: existing unplaced tiles first, then "Clock and uptime", "Media", "Ping a host" with their descriptions. Pick "Ping a host" twice: `config.json` gets `"ping": { "kind": "ping" }` and `"ping-2": { "kind": "ping" }`. Right-click a tile: **Show tile folder** selects `tiles\ping` (or the right kind) in Explorer; **Show config file** still selects `config.json`. `×` on `ping-2` removes it from `config.json`; `tiles\ping` stays.

- [ ] **Step 4 (user): Your own kind and live edits**

Copy `tiles\ping` to `tiles\my-ping`, change `"name"` in its `tile.json` to "My ping" and save: within a second "My ping" is in the picker. Add it. Change `"host"` in `tiles\my-ping\tile.json`: only the my-ping tile restarts (the ping tiles keep their state). Break the JSON (delete a quote) and save: the my-ping tile shows an error naming `tiles\my-ping\tile.json` and the line; other tiles keep working; fix it and the tile recovers. Put `"settings": { "hooks": {} }` on a tile in `config.json`: notice `hooks belong in tiles\<kind>\tile.json`, last good config stays.

- [ ] **Step 5 (user): Reinstall keeps your kinds**

Run `install.cmd` again. Expected: `tiles\my-ping` is untouched; `tiles\ping`, `tiles\uptime`, `tiles\media` are fresh copies; `app\plugins\` holds only `TaskbarTiles.Plugins.Script`; the old `%LOCALAPPDATA%\TaskbarTiles\scripts` folder (if any) is still there and unused.

- [ ] **Step 6 (user): Record**

Ask the user to confirm Steps 2-5 and note anything that failed in `.superpowers/sdd/progress.md` for a fix round. Merge `feat/tile-folders` only when the user asks.

---

## Self-review notes

- Spec coverage:
  - Folder layout, folder name = kind id, kind table at startup with DLLs loaded then: Task 1 (`KindLoader.Load`), Task 3 (`App` uses `AppPaths.TilesDir`), Task 5 (`KindStore.Load`).
  - Script connector stays in `app\plugins`, other connector DLLs only in kind folders, user plugin root not scanned: Task 3 (`AppPaths.PluginsDir`), Task 6 (`Kind="media"`, the publish-layout check in Step 5).
  - `scripts\` and `SampleScripts` removed, an existing data `scripts` folder left alone: Task 6 (nothing deletes it), Task 8 (docs), Task 9 Step 5.
  - Repo `tiles\<kind>\` with scripts and `tile.json`, media `tile.json` in the repo, build copies the media plugin into `tiles\media\`: Task 6.
  - `tile.json` rules (required `name`/`connector`, optional `description`/`settings`, unknown keys ignored): Task 1 tests. `run` relative to the kind folder and working directory = kind folder: Task 2 (unit + integration), Task 3 (host passes the folder). DLLs per kind in their own context, private ids, `connector` must match: Task 1.
  - `config.json` instances, optional `settings`, effective settings (shallow), `hooks` forbidden, old form is an error, layout unchanged: Task 3.
  - Watching: `tile.json`/script change restarts only that kind within a second (300 ms debounce), new kind folder in the picker, DLL change needs a restart with a notice, validation against the kind table: Task 5 (+ fingerprints in Task 3).
  - Picker (instances first, then kinds by name/description, `UniqueId`, `ScriptTemplates` gone): Task 3. **Show tile folder** (and **Show config file** kept): Task 4. × keeps the kind folder: Task 3 test.
  - `install.cmd` exactly as specified, refresh of `uptime`/`ping`/`media` after the swap while the app is stopped, `-TilesDir`, others untouched: Task 7. Docs (double-click install, copy a bundled kind under a new name first): Task 8.
  - Errors table: `"connector"` and `hooks` texts (Task 3 `ConfigParserTests`), missing folder with tile name and `tiles\<kind>` (Task 3 `KindValidationTests`, plus the "no tile.json" hint), broken `tile.json` with file and line (Task 1; tile error in Task 3), connector not declared / DLL fails / duplicate ids (Task 1; tile error via `BrokenKindConnector` in Task 3). A broken kind never rejects the config (`KindValidation` only checks existence).
  - Testing section: scan good/broken/missing `tile.json` (Task 1), merge (Task 3 `TileKindTests`), config errors (Task 3), script paths and working directory (Task 2), gallery by name and free ids (Task 3), × leaves the folder (Task 3), same connector id in two folders (Task 1), watch test (Task 5 `KindStoreTests`), bundled `tile.json` files parse and name existing connectors (Task 6), `install.ps1 -TilesDir` (Task 7 Step 3), on-screen check (Task 9).
- Existing tests changed: `ConfigParserTests` (sed + `Tile_without_connector_is_an_error` replaced by four tests), `ConfigJsonTests` (sed + `Set_tile_writes_connector_and_settings` replaced), `ConfigStoreTests` (sed, default-config assertion, three new tests), `ConfigStoreSaveTests` (sed only), `HostControllerTests` (sed + four tests replaced by six), `HostControllerLayoutTests` (sed + one new test), `TileGalleryTests` (rewritten; `Sample_templates_point_at_shipped_scripts_and_have_valid_hooks` deleted), `TileCommandsTests` (menu header, `Configure_opens_the_config_file` replaced by two tests, `Failing_open…` uses `ShowFolder`), `ScriptConnectorTests`, `ScriptConnectorIntegrationTests`, `TileRuntimeRestartTests` (one new test each), `ConfigNoticeTests` (one new test). Deleted: `SampleScriptsTests` (the `InstallIfAbsent` tests go with `SampleScripts`; its four sample-run integration tests move to `BundledKindsTests`). Unchanged: `PluginLoaderTests` (the refactor keeps behaviour and messages).
- Type consistency checked: `TileKind` positional order `(Id, Directory, Name, Description, Connector, Settings, Dlls, Error, Stamp, DllStamp)` in `KindLoader.LoadKind` and `TestKinds`; `KindDlls(Connectors, Error, Stamp)`; `KindTable(TilesDir, Kinds, FoldersWithoutTileJson)` with `with { FoldersWithoutTileJson = … }` in `KindValidationTests`; `TileConfig(Kind, Settings)`; `HostController(…, configDirectory, writer, kinds)` named `kinds:` in tests and positional in `App`; `TileRuntimeOptions.TileDirectory`/`TileRuntime.TileDirectory` used by `HostController`, `TileCommands` and tests; `TestTileContext(tileId, settingsJson, configDirectory, tileDirectory)`; `ConfigNotice.DllsChangedBody(IReadOnlyList<string>)` called with a collection expression and a `List<string>`.
- Not verified while writing: test totals per task (each step expects `Failed: 0` instead of a count), and `FileSystemWatcher` timing on the user's machine (tests allow 5 s; the debounce is 300 ms).
