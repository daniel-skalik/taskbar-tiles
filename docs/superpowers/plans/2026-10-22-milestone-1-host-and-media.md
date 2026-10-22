# Taskbar Tiles — Milestone 1: Host + Media Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A working `TaskbarTiles` host that shows plugin-driven tiles in the free space of every Windows 11 taskbar, with the Spotify widget ported to a `media` plugin, so `SpotifyTaskbar.exe` can be retired.

**Architecture:** WPF host with small units: a pure `FreeZoneCalculator`, a Win32 `TaskbarTracker` (event hooks, no polling), a `ConfigStore` (watched `config.json`), a `PluginLoader` (one `AssemblyLoadContext` per plugin folder), one `TileRuntime` per tile (state coalescing, timeouts, error state) and one `TileBar` window per display rendering declarative `TileState`. Plugins reference only `TaskbarTiles.Contracts`.

**Tech Stack:** .NET 9, WPF, C# latest, xUnit 2.9, WinRT via `net9.0-windows10.0.19041.0` (`Windows.Media.Control`), Win32 P/Invoke.

Spec: `docs/superpowers/specs/2026-10-22-taskbar-tiles-design.md`. Milestones 2–4 (script plugin, stacks/overflow/edit mode, hot reload/watchdog) get their own plans.

## Global Constraints

- Target frameworks: host, tests and media plugin `net9.0-windows10.0.19041.0`; contracts and sample test plugin `net9.0`.
- Host is per-monitor-v2 DPI aware (`app.manifest`). All Win32 rectangles are physical pixels; WPF sizes are DIP; convert with the display's `DpiScale`.
- Tile look: corner radius 6, 1 px outline, faint fill, 4 px vertical inset inside the taskbar. Dark: fill `#0FFFFFFF`, outline `#24FFFFFF`.
- Tile widths: `TileConnectorAttribute.Width` in DIP, default 132; `media` uses 268. Gap between tiles 6 DIP. Free-zone margin 8 DIP. Secondary-taskbar clock reserve 120 DIP.
- No polling for taskbar changes: only WinEvent hooks, appbar notifications and window messages.
- Data folder: `%LOCALAPPDATA%\TaskbarTiles\` (`config.json`, `plugins\`, `logs\`). Built-in plugins ship in `<exe dir>\plugins\<AssemblyName>\<AssemblyName>.dll`.
- Plugins never run on the UI thread; `ITileContext.Update` is thread-safe and never touches WPF directly.
- Connector calls (`OnActionAsync`, `GetFlyoutAsync`) time out after 10 s; the tile shows "Not responding" for 3 s.
- Alignment comes from geometry (Start button position), never from the `TaskbarAl` registry value.
- Milestone 1 renders only the first tile of a stack; stacks, compact mode and overflow come in milestone 3. Tiles that don't fit are hidden from the right end.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File Structure

```
TaskbarTiles.sln
Directory.Build.props                      shared C# settings
build/CopyPlugins.targets                  copies @(PluginProject) outputs into plugins\<name>\ on build and publish
src/TaskbarTiles.Contracts/
  TileConnectorAttribute.cs                connector id + width
  ITileConnector.cs                        plugin entry point
  ITileContext.cs                          what the host gives a connector (+ ITileLog)
  TileState.cs                             TileState, TileIcon, TileButton, TileColor
  FlyoutState.cs                           FlyoutState, FlyoutRow, FlyoutAction
src/TaskbarTiles.Host/
  TaskbarTiles.Host.csproj, app.manifest, App.xaml(.cs), AppPaths.cs, HostController.cs
  Config/Duration.cs                       "30s" -> TimeSpan
  Config/HostConfig.cs                     config model records
  Config/ConfigParser.cs                   JSON -> HostConfig or ConfigError
  Config/DisplayResolver.cs                which layout a display gets
  Config/ConfigStore.cs                    file load, default file, watch + debounce
  Taskbar/PixelRect.cs                     PixelRect, TaskbarGeometry, FreeZone, TaskbarInfo
  Taskbar/FreeZoneCalculator.cs            pure geometry
  Taskbar/ITaskbarSource.cs                abstraction for the UI
  Taskbar/NativeMethods.cs                 P/Invoke
  Taskbar/TaskbarTracker.cs                Win32 tracking of all taskbars
  Plugins/PluginLoadContext.cs             per-plugin load context sharing host assemblies
  Plugins/PluginLoader.cs                  scan folders, discover connectors -> ConnectorCatalog
  Runtime/IUiDispatcher.cs                 IUiDispatcher + WpfUiDispatcher
  Runtime/Logs.cs                          FileLog, NullTileLog
  Runtime/TileContext.cs                   ITileContext implementation
  Runtime/TileRuntime.cs                   one tile's lifecycle and state
  Runtime/UnknownConnector.cs              placeholder for missing connectors
  UI/FitTiles.cs                           pure "how many tiles fit"
  UI/Theme.cs                              light/dark brushes, semantic colors, glyph map, image decode
  UI/Styles.xaml                           IconButton, FlyoutButton styles
  UI/TileView.xaml(.cs)                    renders one TileState
  UI/FlyoutWindow.cs                       renders FlyoutState above a tile
  UI/TileBar.cs                            one window per display
src/TaskbarTiles.Plugins.Media/
  MediaConnector.cs                        port of SpotifyTaskbar
tests/TaskbarTiles.Tests/                  xUnit tests (+ TestDoubles.cs)
tests/TaskbarTiles.Tests.SamplePlugin/     fixture plugin for loader tests
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `TaskbarTiles.sln`, `Directory.Build.props`
- Create: `src/TaskbarTiles.Contracts/TaskbarTiles.Contracts.csproj`
- Create: `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`, `src/TaskbarTiles.Host/app.manifest`, `src/TaskbarTiles.Host/App.xaml`, `src/TaskbarTiles.Host/App.xaml.cs`
- Create: `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`, `tests/TaskbarTiles.Tests/ScaffoldTests.cs`
- Commit as-is: `SpotifyTaskbar/` (reference for porting), `mockup/taskbar-tiles.html` (UX reference)

**Interfaces:**
- Consumes: nothing
- Produces: buildable solution; host assembly name `TaskbarTiles`, root namespace `TaskbarTiles.Host`; host internals visible to `TaskbarTiles.Tests`

- [ ] **Step 1: Generate projects**

Run from the repo root (`D:\Projects\taskbar-tiles`):
```bash
dotnet new sln -n TaskbarTiles
dotnet new classlib -n TaskbarTiles.Contracts -o src/TaskbarTiles.Contracts -f net9.0
dotnet new wpf -n TaskbarTiles.Host -o src/TaskbarTiles.Host -f net9.0
dotnet new xunit -n TaskbarTiles.Tests -o tests/TaskbarTiles.Tests -f net9.0
dotnet sln add src/TaskbarTiles.Contracts src/TaskbarTiles.Host tests/TaskbarTiles.Tests
```
Then delete the template files: `src/TaskbarTiles.Contracts/Class1.cs`, `src/TaskbarTiles.Host/MainWindow.xaml`, `src/TaskbarTiles.Host/MainWindow.xaml.cs`, `src/TaskbarTiles.Host/AssemblyInfo.cs`, `tests/TaskbarTiles.Tests/UnitTest1.cs`.

- [ ] **Step 2: Write shared settings and project files**

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- WPF projects drop System.IO from implicit usings; add it back everywhere. -->
    <Using Include="System.IO" />
  </ItemGroup>
</Project>
```

`src/TaskbarTiles.Contracts/TaskbarTiles.Contracts.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>
```

`src/TaskbarTiles.Host/TaskbarTiles.Host.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>TaskbarTiles</AssemblyName>
    <RootNamespace>TaskbarTiles.Host</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TaskbarTiles.Tests" />
  </ItemGroup>
</Project>
```

`src/TaskbarTiles.Host/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="TaskbarTiles.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/TaskbarTiles.Host/App.xaml` (no `StartupUri`; the host has no main window):
```xml
<Application x:Class="TaskbarTiles.Host.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
    </Application.Resources>
</Application>
```

`src/TaskbarTiles.Host/App.xaml.cs` (replaced in Tasks 8 and 11):
```csharp
using System.Windows;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
```

`tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" />
    <ProjectReference Include="..\..\src\TaskbarTiles.Host\TaskbarTiles.Host.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write a smoke test**

`tests/TaskbarTiles.Tests/ScaffoldTests.cs`:
```csharp
namespace TaskbarTiles.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Host_assembly_is_named_TaskbarTiles() =>
        Assert.Equal("TaskbarTiles", typeof(TaskbarTiles.Host.App).Assembly.GetName().Name);
}
```

- [ ] **Step 4: Build and test**

Run: `dotnet test TaskbarTiles.sln`
Expected: build succeeds, `Passed: 1`.

- [ ] **Step 5: Commit**

```bash
git add TaskbarTiles.sln Directory.Build.props src tests SpotifyTaskbar mockup
git commit -m "chore: scaffold TaskbarTiles solution, keep SpotifyTaskbar and mockup as references"
```

---

### Task 2: Plugin contract

**Files:**
- Create: `src/TaskbarTiles.Contracts/TileConnectorAttribute.cs`, `ITileConnector.cs`, `ITileContext.cs`, `TileState.cs`, `FlyoutState.cs`
- Test: `tests/TaskbarTiles.Tests/ContractsTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces (namespace `TaskbarTiles.Contracts`):
  - `TileConnectorAttribute(string id) { string Id; double Width = 132 }`
  - `ITileConnector : IAsyncDisposable { Task StartAsync(ITileContext ctx, CancellationToken ct); Task OnActionAsync(string actionId, CancellationToken ct); Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct); }`
  - `ITileContext { string TileId; JsonElement Settings; void Update(TileState state); event EventHandler? Resumed; ITileLog Log; }`
  - `ITileLog { void Info(string message); void Warn(string message); void Error(string message, Exception? ex = null); }`
  - `TileState` record (init props: `Title`, `Subtitle`, `Icon`, `Image`, `Buttons`, `Progress`, `Sparkline`, `Urgent`, `Tooltip`)
  - `TileIcon(string Glyph, TileColor Color = TileColor.Neutral)`, `TileButton(string Id, string Glyph, string? Tooltip = null, bool Enabled = true)`, `enum TileColor { Neutral, Accent, Ok, Warn, Bad }`
  - `FlyoutState(string Title, string? Subtitle, IReadOnlyList<FlyoutRow> Rows, IReadOnlyList<FlyoutAction> Actions, byte[]? Image = null)`, `FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null)`, `FlyoutAction(string Id, string Label, bool Primary = false)`

- [ ] **Step 1: Write the failing test**

`tests/TaskbarTiles.Tests/ContractsTests.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public class ContractsTests
{
    [Fact]
    public void TileState_defaults_to_no_buttons_and_not_urgent()
    {
        var state = new TileState { Title = "x" };
        Assert.Empty(state.Buttons);
        Assert.False(state.Urgent);
        Assert.Null(state.Progress);
    }

    [Fact]
    public void Attribute_width_defaults_to_132_and_can_be_set()
    {
        Assert.Equal(132, new TileConnectorAttribute("a").Width);
        Assert.Equal(268, new TileConnectorAttribute("media") { Width = 268 }.Width);
    }

    [Fact]
    public void TileButton_is_enabled_by_default()
    {
        Assert.True(new TileButton("next", "next").Enabled);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test TaskbarTiles.sln --filter ContractsTests`
Expected: build FAILS with `The type or namespace name 'TileState' could not be found`.

- [ ] **Step 3: Write the contract**

`src/TaskbarTiles.Contracts/TileConnectorAttribute.cs`:
```csharp
namespace TaskbarTiles.Contracts;

/// <summary>Marks a public ITileConnector class. config.json refers to it by <see cref="Id"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TileConnectorAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    /// <summary>Tile width in DIP at full size.</summary>
    public double Width { get; set; } = 132;
}
```

`src/TaskbarTiles.Contracts/ITileConnector.cs`:
```csharp
namespace TaskbarTiles.Contracts;

/// <summary>Drives one tile. The host creates one instance per tile in config.json.</summary>
public interface ITileConnector : IAsyncDisposable
{
    /// <summary>Called once. Keep the context and call ctx.Update whenever the tile should change.</summary>
    Task StartAsync(ITileContext ctx, CancellationToken ct);

    /// <summary>"click" for a plain click when no flyout is offered, otherwise a button or flyout action id.</summary>
    Task OnActionAsync(string actionId, CancellationToken ct);

    /// <summary>Content of the popup shown when the tile is clicked; null means no popup.</summary>
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);
}
```

`src/TaskbarTiles.Contracts/ITileContext.cs`:
```csharp
using System.Text.Json;

namespace TaskbarTiles.Contracts;

public interface ITileContext
{
    string TileId { get; }

    /// <summary>The tile's "settings" object from config.json (an empty object if absent).</summary>
    JsonElement Settings { get; }

    /// <summary>Thread-safe. Only the latest state is rendered if several arrive quickly.</summary>
    void Update(TileState state);

    /// <summary>Raised after the system resumes from sleep.</summary>
    event EventHandler? Resumed;

    ITileLog Log { get; }
}

public interface ITileLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
```

`src/TaskbarTiles.Contracts/TileState.cs`:
```csharp
namespace TaskbarTiles.Contracts;

public enum TileColor { Neutral, Accent, Ok, Warn, Bad }

/// <summary>Glyph names: play, pause, previous, next, check, cancel, warning, refresh, music, info, sync, cloud, clock.</summary>
public sealed record TileIcon(string Glyph, TileColor Color = TileColor.Neutral);

public sealed record TileButton(string Id, string Glyph, string? Tooltip = null, bool Enabled = true);

public sealed record TileState
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public TileIcon? Icon { get; init; }

    /// <summary>PNG or JPEG bytes, shown instead of the icon (e.g. album art).</summary>
    public byte[]? Image { get; init; }

    public IReadOnlyList<TileButton> Buttons { get; init; } = [];

    /// <summary>0..1, drawn as a thin bar along the bottom edge.</summary>
    public double? Progress { get; init; }

    public IReadOnlyList<double>? Sparkline { get; init; }
    public bool Urgent { get; init; }
    public string? Tooltip { get; init; }
}
```

`src/TaskbarTiles.Contracts/FlyoutState.cs`:
```csharp
namespace TaskbarTiles.Contracts;

public sealed record FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null);

public sealed record FlyoutAction(string Id, string Label, bool Primary = false);

public sealed record FlyoutState(
    string Title,
    string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows,
    IReadOnlyList<FlyoutAction> Actions,
    byte[]? Image = null);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test TaskbarTiles.sln --filter ContractsTests`
Expected: `Passed: 3`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Contracts tests/TaskbarTiles.Tests/ContractsTests.cs
git commit -m "feat(contracts): add plugin contract types"
```

---

### Task 3: Config model, parser and display resolution

**Files:**
- Create: `src/TaskbarTiles.Host/Config/Duration.cs`, `HostConfig.cs`, `ConfigParser.cs`, `DisplayResolver.cs`
- Test: `tests/TaskbarTiles.Tests/DurationTests.cs`, `ConfigParserTests.cs`, `DisplayResolverTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces (namespace `TaskbarTiles.Host.Config`):
  - `static class Duration { bool TryParse(string? text, out TimeSpan value); TimeSpan Parse(string text); }`
  - `record HostConfig(IReadOnlyDictionary<string, TileConfig> Tiles, IReadOnlyDictionary<string, DisplayLayout> Displays) { static HostConfig Empty; }`
  - `record TileConfig(string Connector, JsonElement Settings)`
  - `record DisplayLayout(IReadOnlyList<Slot> Slots) { static DisplayLayout Empty; }`
  - `abstract record Slot`; `record TileSlot(string TileId, bool Pin) : Slot`; `record StackSlot(IReadOnlyList<string> TileIds, TimeSpan Rotate) : Slot`
  - `record ConfigError(string Message, int? Line = null, int? Column = null)`
  - `record ConfigParseResult(HostConfig? Config, ConfigError? Error) { bool Success; }`
  - `static class ConfigParser { ConfigParseResult Parse(string json); }`
  - `static class DisplayResolver { const string PrimaryKey = "primary"; const string FallbackKey = "*"; DisplayLayout Resolve(HostConfig config, string displayId, bool isPrimary); IReadOnlyList<string> VisibleTileIds(DisplayLayout layout); }`

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/DurationTests.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class DurationTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("10m", 600_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData(" 1.5s ", 1_500)]
    public void Parses_supported_units(string text, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Duration.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("5 days")]
    [InlineData(null)]
    public void Rejects_invalid(string? text) => Assert.False(Duration.TryParse(text, out _));
}
```

`tests/TaskbarTiles.Tests/ConfigParserTests.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigParserTests
{
    const string Sample = """
    {
      // comments are allowed
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } },
        "ci":      { "connector": "script" },
        "weather": { "connector": "script" },
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true }, "ci", { "stack": ["weather", "ci"], "rotate": "8s" } ] },
        "*":       { "slots": [] }
      }
    }
    """;

    [Fact]
    public void Parses_tiles_and_slots()
    {
        var result = ConfigParser.Parse(Sample);

        Assert.True(result.Success, result.Error?.ToString());
        var cfg = result.Config!;
        Assert.Equal("media", cfg.Tiles["spotify"].Connector);
        Assert.Equal("Spotify", cfg.Tiles["spotify"].Settings.GetProperty("app").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, cfg.Tiles["ci"].Settings.ValueKind);

        var slots = cfg.Displays["primary"].Slots;
        Assert.Equal(new TileSlot("spotify", true), slots[0]);
        Assert.Equal(new TileSlot("ci", false), slots[1]);
        var stack = Assert.IsType<StackSlot>(slots[2]);
        Assert.Equal(new[] { "weather", "ci" }, stack.TileIds);
        Assert.Equal(TimeSpan.FromSeconds(8), stack.Rotate);
        Assert.Empty(cfg.Displays["*"].Slots);
    }

    [Fact]
    public void Stack_rotate_defaults_to_5_seconds()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { "connector": "x" } }, "displays": { "primary": { "slots": [ { "stack": ["a"] } ] } } }""");
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<StackSlot>(result.Config!.Displays["primary"].Slots[0]).Rotate);
    }

    [Fact]
    public void Invalid_json_reports_one_based_line_and_column()
    {
        var result = ConfigParser.Parse("{\n  \"tiles\": {\n    \"a\": { \"connector\": }\n  }\n}");

        Assert.False(result.Success);
        Assert.Equal(3, result.Error!.Line);
        Assert.NotNull(result.Error.Column);
    }

    [Fact]
    public void Slot_with_unknown_tile_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": {}, "displays": { "primary": { "slots": ["ghost"] } } }""");
        Assert.False(result.Success);
        Assert.Contains("ghost", result.Error!.Message);
    }

    [Fact]
    public void Tile_without_connector_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { } } }""");
        Assert.False(result.Success);
        Assert.Contains("connector", result.Error!.Message);
    }

    [Fact]
    public void Missing_displays_section_means_no_layouts()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { "connector": "x" } } }""");
        Assert.True(result.Success);
        Assert.Empty(result.Config!.Displays);
    }
}
```

`tests/TaskbarTiles.Tests/DisplayResolverTests.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class DisplayResolverTests
{
    static readonly DisplayLayout Primary = new([new TileSlot("p", false)]);
    static readonly DisplayLayout Exact = new([new TileSlot("e", false)]);
    static readonly DisplayLayout Any = new([new TileSlot("a", false)]);

    static HostConfig Config(params (string Key, DisplayLayout Layout)[] displays) =>
        new(new Dictionary<string, TileConfig>(), displays.ToDictionary(d => d.Key, d => d.Layout));

    [Fact]
    public void Exact_device_id_wins_over_primary()
    {
        var cfg = Config(("DEV1", Exact), ("primary", Primary), ("*", Any));
        Assert.Same(Exact, DisplayResolver.Resolve(cfg, "DEV1", isPrimary: true));
    }

    [Fact]
    public void Primary_key_applies_to_primary_display() =>
        Assert.Same(Primary, DisplayResolver.Resolve(Config(("primary", Primary), ("*", Any)), "DEV9", isPrimary: true));

    [Fact]
    public void Star_applies_to_other_displays() =>
        Assert.Same(Any, DisplayResolver.Resolve(Config(("primary", Primary), ("*", Any)), "DEV9", isPrimary: false));

    [Fact]
    public void No_match_gives_empty_layout() =>
        Assert.Empty(DisplayResolver.Resolve(Config(("primary", Primary)), "DEV9", isPrimary: false).Slots);

    [Fact]
    public void Visible_tile_ids_take_first_item_of_each_stack()
    {
        var layout = new DisplayLayout([new TileSlot("a", false), new StackSlot(["b", "c"], TimeSpan.FromSeconds(5)), new StackSlot([], TimeSpan.FromSeconds(5))]);
        Assert.Equal(new[] { "a", "b" }, DisplayResolver.VisibleTileIds(layout));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "DurationTests|ConfigParserTests|DisplayResolverTests"`
Expected: build FAILS with `The type or namespace name 'Config' does not exist in the namespace 'TaskbarTiles.Host'`.

- [ ] **Step 3: Write the implementation**

`src/TaskbarTiles.Host/Config/Duration.cs`:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskbarTiles.Host.Config;

/// <summary>Parses "500ms", "30s", "10m", "1h".</summary>
public static partial class Duration
{
    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(ms|s|m|h)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (text is null) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        value = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            _ => TimeSpan.FromHours(n),
        };
        return true;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid duration '{text}'. Use e.g. 500ms, 30s, 10m, 1h.");
}
```

`src/TaskbarTiles.Host/Config/HostConfig.cs`:
```csharp
using System.Text.Json;

namespace TaskbarTiles.Host.Config;

public sealed record HostConfig(
    IReadOnlyDictionary<string, TileConfig> Tiles,
    IReadOnlyDictionary<string, DisplayLayout> Displays)
{
    public static HostConfig Empty { get; } = new(new Dictionary<string, TileConfig>(), new Dictionary<string, DisplayLayout>());
}

/// <summary>Settings is a cloned element, safe to keep after parsing.</summary>
public sealed record TileConfig(string Connector, JsonElement Settings);

public sealed record DisplayLayout(IReadOnlyList<Slot> Slots)
{
    public static DisplayLayout Empty { get; } = new([]);
}

public abstract record Slot;

public sealed record TileSlot(string TileId, bool Pin) : Slot;

public sealed record StackSlot(IReadOnlyList<string> TileIds, TimeSpan Rotate) : Slot
{
    // Records compare lists by reference; compare items so layouts with equal content are equal.
    public bool Equals(StackSlot? other) =>
        other is not null && Rotate == other.Rotate && TileIds.SequenceEqual(other.TileIds);

    public override int GetHashCode() => HashCode.Combine(Rotate, TileIds.Count);
}
```

`src/TaskbarTiles.Host/Config/ConfigParser.cs`:
```csharp
using System.Text.Json;

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

    static readonly TimeSpan DefaultRotate = TimeSpan.FromSeconds(5);

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
            if (el.TryGetProperty("rotate", out var r) && !Duration.TryParse(r.GetString(), out rotate))
                throw new ConfigException($"Invalid \"rotate\" in slot {index} on display '{display}'. Use e.g. 5s.");
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

`src/TaskbarTiles.Host/Config/DisplayResolver.cs`:
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "DurationTests|ConfigParserTests|DisplayResolverTests"`
Expected: all pass (`Passed: 20`).

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Config tests/TaskbarTiles.Tests/DurationTests.cs tests/TaskbarTiles.Tests/ConfigParserTests.cs tests/TaskbarTiles.Tests/DisplayResolverTests.cs
git commit -m "feat(config): parse config.json and resolve per-display layouts"
```

---

### Task 4: Logging and ConfigStore

**Files:**
- Create: `src/TaskbarTiles.Host/Runtime/Logs.cs`, `src/TaskbarTiles.Host/Config/ConfigStore.cs`
- Test: `tests/TaskbarTiles.Tests/TestDoubles.cs`, `tests/TaskbarTiles.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `ConfigParser.Parse`, `HostConfig`, `ConfigError` (Task 3); `ITileLog` (Task 2)
- Produces:
  - `TaskbarTiles.Host.Runtime.FileLog : ITileLog` with `static FileLog For(string logsDir, string name)`
  - `TaskbarTiles.Host.Runtime.NullTileLog : ITileLog` with `static NullTileLog Instance`
  - `TaskbarTiles.Host.Config.ConfigStore : IDisposable` with ctor `(string path, ITileLog log)`, `HostConfig Current`, `ConfigError? LastError`, `event EventHandler? Changed`, `event EventHandler<ConfigError>? ReloadFailed`, `void Load()`, `void StartWatching()`, `const string DefaultJson`
  - Test double `TaskbarTiles.Tests.CollectingLog : ITileLog` with `List<string> Messages`

- [ ] **Step 1: Write the failing tests**

`tests/TaskbarTiles.Tests/TestDoubles.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public sealed class CollectingLog : ITileLog
{
    public List<string> Messages { get; } = [];
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Messages) Messages.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message)); }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
```

`tests/TaskbarTiles.Tests/ConfigStoreTests.cs`:
```csharp
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigStoreTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Load_creates_default_file_when_missing()
    {
        using var dir = new TempDir();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());

        store.Load();

        Assert.True(File.Exists(dir.File("config.json")));
        Assert.Equal("media", store.Current.Tiles["spotify"].Connector);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_of_broken_file_gives_empty_config_and_error()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), "{ broken");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());

        store.Load();

        Assert.Empty(store.Current.Tiles);
        Assert.NotNull(store.LastError);
        Assert.Equal("{ broken", File.ReadAllText(dir.File("config.json"))); // never overwritten
    }

    [Fact]
    public async Task Watch_reloads_valid_edits()
    {
        using var dir = new TempDir();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        store.StartWatching();
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "x": { "connector": "y" } } }""");

        await changed.Task.WaitAsync(Wait);
        Assert.Equal("y", store.Current.Tiles["x"].Connector);
    }

    [Fact]
    public async Task Watch_keeps_last_good_config_on_invalid_edit()
    {
        using var dir = new TempDir();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        store.StartWatching();
        var failed = new TaskCompletionSource<ConfigError>();
        store.ReloadFailed += (_, e) => failed.TrySetResult(e);

        File.WriteAllText(dir.File("config.json"), "{ \"tiles\": ");

        var error = await failed.Task.WaitAsync(Wait);
        Assert.NotNull(error.Line);
        Assert.True(store.Current.Tiles.ContainsKey("spotify"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter ConfigStoreTests`
Expected: build FAILS with `The type or namespace name 'ConfigStore' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/TaskbarTiles.Host/Runtime/Logs.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Appends timestamped lines to logs\{name}.log. Thread-safe.</summary>
public sealed class FileLog : ITileLog
{
    readonly string _path;
    readonly object _gate = new();

    FileLog(string path) => _path = path;

    public static FileLog For(string logsDir, string name)
    {
        Directory.CreateDirectory(logsDir);
        return new FileLog(Path.Combine(logsDir, name + ".log"));
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line); }
            catch (IOException) { /* logging must never take the host down */ }
        }
    }
}

public sealed class NullTileLog : ITileLog
{
    public static NullTileLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
```

`src/TaskbarTiles.Host/Config/ConfigStore.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Config;

/// <summary>Owns config.json: creates it on first run, parses it, and reloads it when it changes on disk.</summary>
public sealed class ConfigStore(string path, ITileLog log) : IDisposable
{
    public const string DefaultJson = """
    {
      // Each tile is driven by a connector (a plugin). Place tiles on displays under "displays".
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true } ] },
        "*":       { "slots": [] }
      }
    }
    """;

    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    FileSystemWatcher? _watcher;
    Timer? _debounce;

    public HostConfig Current { get; private set; } = HostConfig.Empty;
    public ConfigError? LastError { get; private set; }

    /// <summary>Raised on a thread-pool thread after a successful reload.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on a thread-pool thread when an edited file fails to parse. Current stays unchanged.</summary>
    public event EventHandler<ConfigError>? ReloadFailed;

    public void Load()
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultJson);
            log.Info($"Created default config at {path}");
        }

        var result = ConfigParser.Parse(ReadWithRetry());
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
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

    void Reload()
    {
        string text;
        try { text = ReadWithRetry(); }
        catch (IOException ex) { log.Warn($"Could not read config.json: {ex.Message}"); return; }

        var result = ConfigParser.Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter ConfigStoreTests`
Expected: `Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Runtime/Logs.cs src/TaskbarTiles.Host/Config/ConfigStore.cs tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/ConfigStoreTests.cs
git commit -m "feat(config): add ConfigStore with default file and hot reload"
```

---

### Task 5: Free-zone geometry and tile fitting

**Files:**
- Create: `src/TaskbarTiles.Host/Taskbar/PixelRect.cs`, `src/TaskbarTiles.Host/Taskbar/FreeZoneCalculator.cs`, `src/TaskbarTiles.Host/UI/FitTiles.cs`
- Test: `tests/TaskbarTiles.Tests/FreeZoneCalculatorTests.cs`, `tests/TaskbarTiles.Tests/FitTilesTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces (namespace `TaskbarTiles.Host.Taskbar` unless noted):
  - `readonly record struct PixelRect(int Left, int Top, int Right, int Bottom) { int Width; int Height; bool IsEmpty; }`
  - `record TaskbarGeometry(PixelRect Taskbar, PixelRect? Start, PixelRect? IconArea, PixelRect? Tray)`
  - `readonly record struct FreeZone(PixelRect Zone, bool IconsCentered)`
  - `record TaskbarInfo(string DisplayId, bool IsPrimary, PixelRect Taskbar, FreeZone FreeZone, bool Hidden, bool IsLightTheme, double DpiScale)`
  - `static class FreeZoneCalculator { FreeZone Compute(TaskbarGeometry g, int marginPx, int clockReservePx); }`
  - `TaskbarTiles.Host.UI.FitTiles.Count(IReadOnlyList<double> widths, double available, double gap) -> int`

- [ ] **Step 1: Write the failing tests**

Real numbers below come from the user's machine (3840×2160 main display at 150 %, second display to the right).

`tests/TaskbarTiles.Tests/FreeZoneCalculatorTests.cs`:
```csharp
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Tests;

public class FreeZoneCalculatorTests
{
    const int Margin = 12, Clock = 180;
    static readonly PixelRect MainBar = new(0, 2088, 3840, 2160);

    [Fact]
    public void Centered_icons_free_zone_ends_before_start()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(1589, 2088, 1656, 2160), IconArea: new(1656, 2088, 2052, 2160), Tray: new(3408, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.True(z.IconsCentered);
        Assert.Equal(new PixelRect(12, 2088, 1577, 2160), z.Zone);
    }

    [Fact]
    public void Left_aligned_icons_free_zone_runs_from_icons_to_tray()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(0, 2088, 68, 2160), IconArea: new(68, 2088, 398, 2160), Tray: new(3456, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.False(z.IconsCentered);
        Assert.Equal(new PixelRect(410, 2088, 3444, 2160), z.Zone);
    }

    [Fact]
    public void Secondary_taskbar_without_tray_reserves_clock_space()
    {
        var bar = new PixelRect(3840, 2088, 7680, 2160);
        var g = new TaskbarGeometry(bar, Start: new(3840, 2088, 3907, 2160), IconArea: new(3907, 2088, 4039, 2160), Tray: null);

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.Equal(new PixelRect(4051, 2088, 7488, 2160), z.Zone);
    }

    [Fact]
    public void Missing_start_and_icons_is_treated_as_left_aligned_from_taskbar_edge()
    {
        var z = FreeZoneCalculator.Compute(new TaskbarGeometry(MainBar, null, null, null), Margin, Clock);

        Assert.False(z.IconsCentered);
        Assert.Equal(new PixelRect(12, 2088, 3648, 2160), z.Zone);
    }

    [Fact]
    public void Crowded_taskbar_collapses_to_zero_width()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(0, 2088, 68, 2160), IconArea: new(68, 2088, 3450, 2160), Tray: new(3456, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.True(z.Zone.IsEmpty);
        Assert.Equal(z.Zone.Left, z.Zone.Right);
    }
}
```

`tests/TaskbarTiles.Tests/FitTilesTests.cs`:
```csharp
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class FitTilesTests
{
    [Fact]
    public void All_fit() => Assert.Equal(3, FitTiles.Count([100, 100, 100], available: 312, gap: 6));

    [Fact]
    public void Gap_is_counted_between_tiles() => Assert.Equal(2, FitTiles.Count([100, 100, 100], available: 311, gap: 6));

    [Fact]
    public void None_fit() => Assert.Equal(0, FitTiles.Count([268], available: 200, gap: 6));

    [Fact]
    public void Empty_list() => Assert.Equal(0, FitTiles.Count([], available: 500, gap: 6));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter "FreeZoneCalculatorTests|FitTilesTests"`
Expected: build FAILS with `The type or namespace name 'Taskbar' does not exist in the namespace 'TaskbarTiles.Host'`.

- [ ] **Step 3: Write the implementation**

`src/TaskbarTiles.Host/Taskbar/PixelRect.cs`:
```csharp
namespace TaskbarTiles.Host.Taskbar;

/// <summary>Physical-pixel rectangle (Win32 coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Raw window rectangles of one taskbar. Children are null when the window is missing or zero-sized.</summary>
public sealed record TaskbarGeometry(PixelRect Taskbar, PixelRect? Start, PixelRect? IconArea, PixelRect? Tray);

public readonly record struct FreeZone(PixelRect Zone, bool IconsCentered);

public sealed record TaskbarInfo(
    string DisplayId,
    bool IsPrimary,
    PixelRect Taskbar,
    FreeZone FreeZone,
    bool Hidden,
    bool IsLightTheme,
    double DpiScale);
```

`src/TaskbarTiles.Host/Taskbar/FreeZoneCalculator.cs`:
```csharp
namespace TaskbarTiles.Host.Taskbar;

public static class FreeZoneCalculator
{
    /// <summary>
    /// Centered icons (Start is away from the left edge): the zone is left of Start.
    /// Left-aligned icons: the zone runs from the end of the icons to the tray, or to the
    /// taskbar edge minus a clock reserve on secondary taskbars that have no tray window.
    /// </summary>
    public static FreeZone Compute(TaskbarGeometry g, int marginPx, int clockReservePx)
    {
        var tb = g.Taskbar;
        var centered = g.Start is { } s && s.Left - tb.Left > marginPx * 2;

        int left, right;
        if (centered)
        {
            left = tb.Left + marginPx;
            right = g.Start!.Value.Left - marginPx;
        }
        else
        {
            left = (g.IconArea?.Right ?? g.Start?.Right ?? tb.Left) + marginPx;
            right = (g.Tray?.Left ?? tb.Right - clockReservePx) - marginPx;
        }

        if (right < left) right = left;
        return new FreeZone(new PixelRect(left, tb.Top, right, tb.Bottom), centered);
    }
}
```

`src/TaskbarTiles.Host/UI/FitTiles.cs`:
```csharp
namespace TaskbarTiles.Host.UI;

/// <summary>Milestone 1 fitting: keep tiles in order until the next one would not fit.</summary>
public static class FitTiles
{
    public static int Count(IReadOnlyList<double> widths, double available, double gap)
    {
        double used = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            used += widths[i] + (i > 0 ? gap : 0);
            if (used > available) return i;
        }
        return widths.Count;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter "FreeZoneCalculatorTests|FitTilesTests"`
Expected: `Passed: 9`.

- [ ] **Step 5: Commit**

```bash
git add src/TaskbarTiles.Host/Taskbar src/TaskbarTiles.Host/UI/FitTiles.cs tests/TaskbarTiles.Tests/FreeZoneCalculatorTests.cs tests/TaskbarTiles.Tests/FitTilesTests.cs
git commit -m "feat(taskbar): compute free zone from taskbar geometry"
```

---

### Task 6: Plugin loading

**Files:**
- Create: `build/CopyPlugins.targets`
- Create: `src/TaskbarTiles.Host/Plugins/PluginLoadContext.cs`, `src/TaskbarTiles.Host/Plugins/PluginLoader.cs`
- Create: `tests/TaskbarTiles.Tests.SamplePlugin/TaskbarTiles.Tests.SamplePlugin.csproj`, `tests/TaskbarTiles.Tests.SamplePlugin/SampleConnector.cs`
- Modify: `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj` (reference sample plugin, import targets)
- Test: `tests/TaskbarTiles.Tests/PluginLoaderTests.cs`

**Interfaces:**
- Consumes: `ITileConnector`, `TileConnectorAttribute`, `ITileLog` (Task 2)
- Produces (namespace `TaskbarTiles.Host.Plugins`):
  - `record ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath)`
  - `sealed class ConnectorCatalog { static ConnectorCatalog Empty; IReadOnlyDictionary<string, ConnectorDescriptor> Connectors; ITileConnector? Create(string id); }`
  - `sealed class PluginLoader(ITileLog log) { ConnectorCatalog LoadFrom(IEnumerable<string> roots); }`
  - MSBuild: an item `<PluginProject Include="path.csproj" />` plus `<Import Project="...\build\CopyPlugins.targets" />` copies that project's output to `$(OutDir)plugins\<AssemblyName>\` on build and `$(PublishDir)plugins\<AssemblyName>\` on publish.

- [ ] **Step 1: Write the MSBuild targets and the sample plugin**

`build/CopyPlugins.targets`:
```xml
<Project>
  <!-- Copies each @(PluginProject)'s build output to plugins\<AssemblyName>\ next to this project's output. -->
  <Target Name="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <MSBuild Projects="@(PluginProject)" Targets="GetTargetPath" Properties="Configuration=$(Configuration)">
      <Output TaskParameter="TargetOutputs" ItemName="_PluginTargetPath" />
    </MSBuild>
    <ItemGroup>
      <_PluginFile Include="%(_PluginTargetPath.RootDir)%(_PluginTargetPath.Directory)**\*.*"
                   PluginName="%(_PluginTargetPath.Filename)" />
    </ItemGroup>
  </Target>

  <Target Name="CopyPluginsToOutput" AfterTargets="Build" DependsOnTargets="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <Copy SourceFiles="@(_PluginFile)"
          DestinationFiles="@(_PluginFile->'$(OutDir)plugins\%(PluginName)\%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>

  <Target Name="CopyPluginsToPublish" AfterTargets="Publish" DependsOnTargets="_ResolvePluginOutputs" Condition="'@(PluginProject)' != ''">
    <Copy SourceFiles="@(_PluginFile)"
          DestinationFiles="@(_PluginFile->'$(PublishDir)plugins\%(PluginName)\%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>
</Project>
```

`tests/TaskbarTiles.Tests.SamplePlugin/TaskbarTiles.Tests.SamplePlugin.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides Contracts at runtime; don't ship a copy with the plugin. -->
    <ProjectReference Include="..\..\src\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" Private="false" />
  </ItemGroup>
</Project>
```

`tests/TaskbarTiles.Tests.SamplePlugin/SampleConnector.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests.SamplePlugin;

[TileConnector("sample", Width = 150)]
public sealed class SampleConnector : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.Update(new TileState { Title = "hello from sample" });
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Not a connector: no attribute. The loader must ignore it.
public sealed class NotAConnector { }
```

Add it to the solution:
```bash
dotnet sln add tests/TaskbarTiles.Tests.SamplePlugin
```

Modify `tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj`: add inside `<Project>` after the existing `ItemGroup` with project references:
```xml
  <ItemGroup>
    <!-- Build order only; the plugin is copied to plugins\ by CopyPlugins.targets, not referenced. -->
    <ProjectReference Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Tests.SamplePlugin\TaskbarTiles.Tests.SamplePlugin.csproj" />
  </ItemGroup>
  <Import Project="..\..\build\CopyPlugins.targets" />
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/PluginLoaderTests.cs`:
```csharp
using System.Runtime.Loader;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class PluginLoaderTests
{
    static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    [Fact]
    public void Build_copies_sample_plugin_into_plugins_folder() =>
        Assert.True(File.Exists(Path.Combine(PluginsRoot, "TaskbarTiles.Tests.SamplePlugin", "TaskbarTiles.Tests.SamplePlugin.dll")));

    [Fact]
    public void Discovers_connector_by_attribute_with_width()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        var d = Assert.Single(catalog.Connectors).Value;
        Assert.Equal("sample", d.Id);
        Assert.Equal(150, d.Width);
    }

    [Fact]
    public void Created_connector_shares_contract_types_with_host()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        var connector = catalog.Create("sample");

        Assert.IsAssignableFrom<ITileConnector>(connector); // same ITileConnector type identity as the host
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(connector!.GetType().Assembly));
    }

    [Fact]
    public void Unknown_connector_id_creates_null() =>
        Assert.Null(new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]).Create("nope"));

    [Fact]
    public void Missing_root_is_ignored() =>
        Assert.Empty(new PluginLoader(new CollectingLog()).LoadFrom([Path.Combine(PluginsRoot, "does-not-exist")]).Connectors);

    [Fact]
    public void Folder_without_matching_dll_is_skipped_with_warning()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "broken"));
        var log = new CollectingLog();

        var catalog = new PluginLoader(log).LoadFrom([dir.Path]);

        Assert.Empty(catalog.Connectors);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN") && m.Contains("broken"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter PluginLoaderTests`
Expected: build FAILS with `The type or namespace name 'Plugins' does not exist in the namespace 'TaskbarTiles.Host'`.

- [ ] **Step 4: Write the implementation**

`src/TaskbarTiles.Host/Plugins/PluginLoadContext.cs`:
```csharp
using System.Reflection;
using System.Runtime.Loader;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// One context per plugin folder. Assemblies the host itself ships (Contracts, WinRT projections)
/// always come from the host so types unify across plugins; everything else from the plugin folder.
/// </summary>
sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath))
{
    readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll")))
            return null; // fall back to the default context

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
```

`src/TaskbarTiles.Host/Plugins/PluginLoader.cs`:
```csharp
using System.Reflection;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

public sealed record ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath);

public sealed class ConnectorCatalog(IReadOnlyDictionary<string, ConnectorDescriptor> connectors)
{
    public static ConnectorCatalog Empty { get; } = new(new Dictionary<string, ConnectorDescriptor>());

    public IReadOnlyDictionary<string, ConnectorDescriptor> Connectors => connectors;

    public ITileConnector? Create(string id) =>
        connectors.TryGetValue(id, out var d) ? (ITileConnector)Activator.CreateInstance(d.Type)! : null;
}

/// <summary>Loads plugins laid out as &lt;root&gt;\&lt;Name&gt;\&lt;Name&gt;.dll. Earlier roots win on duplicate ids.</summary>
public sealed class PluginLoader(ITileLog log)
{
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
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(ITileConnector).IsAssignableFrom(type)) continue;
                    var attr = type.GetCustomAttribute<TileConnectorAttribute>();
                    if (attr is null) continue;

                    if (map.TryGetValue(attr.Id, out var existing))
                    {
                        log.Warn($"Connector '{attr.Id}' in {main} ignored; already provided by {existing.SourcePath}.");
                        continue;
                    }
                    map[attr.Id] = new ConnectorDescriptor(attr.Id, type, attr.Width, main);
                    log.Info($"Loaded connector '{attr.Id}' from {main}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load plugin {main}", ex);
            }
        }

        return new ConnectorCatalog(map);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter PluginLoaderTests`
Expected: `Passed: 6`. If `Build_copies_sample_plugin_into_plugins_folder` fails, inspect `tests/TaskbarTiles.Tests/bin/Debug/net9.0-windows10.0.19041.0/plugins/` and fix `CopyPlugins.targets` before continuing.

- [ ] **Step 6: Commit**

```bash
git add build TaskbarTiles.sln src/TaskbarTiles.Host/Plugins tests/TaskbarTiles.Tests.SamplePlugin tests/TaskbarTiles.Tests/TaskbarTiles.Tests.csproj tests/TaskbarTiles.Tests/PluginLoaderTests.cs
git commit -m "feat(plugins): load connectors from plugin folders"
```

---

### Task 7: Tile runtime

**Files:**
- Create: `src/TaskbarTiles.Host/Runtime/IUiDispatcher.cs`, `TileContext.cs`, `TileRuntime.cs`, `UnknownConnector.cs`
- Modify: `tests/TaskbarTiles.Tests/TestDoubles.cs` (add dispatchers and fake connector)
- Test: `tests/TaskbarTiles.Tests/TileRuntimeTests.cs`

**Interfaces:**
- Consumes: contract types (Task 2), `NullTileLog` (Task 4)
- Produces (namespace `TaskbarTiles.Host.Runtime`):
  - `interface IUiDispatcher { void Post(Action action); }`; `sealed class WpfUiDispatcher(System.Windows.Threading.Dispatcher d) : IUiDispatcher`
  - `sealed class TileRuntime : IAsyncDisposable` with ctor `(string tileId, string connectorId, double width, ITileConnector connector, JsonElement settings, IUiDispatcher ui, ITileLog log, TimeSpan? callTimeout = null, TimeSpan? notRespondingFor = null)`; members `string TileId`, `string ConnectorId`, `double Width`, `TileState State` (UI thread), `Exception? LastError`, `event EventHandler? StateChanged` (UI thread), `Task StartAsync()`, `Task InvokeActionAsync(string actionId)`, `Task<FlyoutState?> GetFlyoutAsync()`, `void RaiseResumed()`, `static TileState Initial`
  - `sealed class UnknownConnector(string connectorId) : ITileConnector`

- [ ] **Step 1: Add test doubles**

Append to `tests/TaskbarTiles.Tests/TestDoubles.cs`:
```csharp
public sealed class ImmediateDispatcher : TaskbarTiles.Host.Runtime.IUiDispatcher
{
    public void Post(Action action) => action();
}

public sealed class ManualDispatcher : TaskbarTiles.Host.Runtime.IUiDispatcher
{
    readonly Queue<Action> _queue = new();
    public int Posted { get; private set; }
    public void Post(Action action) { lock (_queue) { _queue.Enqueue(action); Posted++; } }
    public void Pump()
    {
        while (true)
        {
            Action next;
            lock (_queue) { if (_queue.Count == 0) return; next = _queue.Dequeue(); }
            next();
        }
    }
}

public sealed class FakeConnector : ITileConnector
{
    public ITileContext? Context { get; private set; }
    public Func<ITileContext, CancellationToken, Task> OnStart { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task> OnAction { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<FlyoutState?>> OnFlyout { get; set; } = _ => Task.FromResult<FlyoutState?>(null);
    public List<string> Actions { get; } = [];
    public bool Disposed { get; private set; }

    public Task StartAsync(ITileContext ctx, CancellationToken ct) { Context = ctx; return OnStart(ctx, ct); }
    public Task OnActionAsync(string actionId, CancellationToken ct) { Actions.Add(actionId); return OnAction(actionId, ct); }
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => OnFlyout(ct);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/TaskbarTiles.Tests/TileRuntimeTests.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Tests;

public class TileRuntimeTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    static TileRuntime Create(FakeConnector c, IUiDispatcher ui, TimeSpan? timeout = null, TimeSpan? notResponding = null) =>
        new("tile1", "fake", 132, c, NoSettings, ui, new CollectingLog(), timeout, notResponding);

    [Fact]
    public async Task Update_from_connector_becomes_state_and_raises_event()
    {
        var c = new FakeConnector { OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "hi" }); return Task.CompletedTask; } };
        var rt = Create(c, new ImmediateDispatcher());
        var raised = 0;
        rt.StateChanged += (_, _) => raised++;

        await rt.StartAsync();

        Assert.Equal("hi", rt.State.Title);
        Assert.Equal(1, raised);
        Assert.Equal("tile1", c.Context!.TileId);
    }

    [Fact]
    public async Task Burst_of_updates_is_coalesced_into_one_post_with_latest_state()
    {
        var c = new FakeConnector();
        var ui = new ManualDispatcher();
        var rt = Create(c, ui);
        await rt.StartAsync();

        c.Context!.Update(new TileState { Title = "1" });
        c.Context.Update(new TileState { Title = "2" });
        c.Context.Update(new TileState { Title = "3" });
        ui.Pump();

        Assert.Equal(1, ui.Posted);
        Assert.Equal("3", rt.State.Title);
    }

    [Fact]
    public async Task Start_exception_shows_error_state()
    {
        var c = new FakeConnector { OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(c, new ImmediateDispatcher());

        await rt.StartAsync();

        Assert.Equal("Error", rt.State.Title);
        Assert.Equal(TileColor.Warn, rt.State.Icon!.Color);
        Assert.Equal("boom", rt.LastError!.Message);
    }

    [Fact]
    public async Task Successful_update_after_error_clears_error()
    {
        var c = new FakeConnector { OnAction = (_, _) => throw new Exception("x") };
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();
        await rt.InvokeActionAsync("click");
        Assert.NotNull(rt.LastError);

        c.Context!.Update(new TileState { Title = "ok" });

        Assert.Null(rt.LastError);
        Assert.Equal("ok", rt.State.Title);
    }

    [Fact]
    public async Task Action_is_forwarded()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        await rt.InvokeActionAsync("next");

        Assert.Equal(new[] { "next" }, c.Actions);
    }

    [Fact]
    public async Task Slow_action_shows_not_responding_then_restores()
    {
        var c = new FakeConnector
        {
            OnStart = (ctx, _) => { ctx.Update(new TileState { Title = "normal" }); return Task.CompletedTask; },
            OnAction = (_, ct) => Task.Delay(Timeout.Infinite, ct),
        };
        var rt = Create(c, new ImmediateDispatcher(), timeout: TimeSpan.FromMilliseconds(50), notResponding: TimeSpan.FromMilliseconds(100));
        await rt.StartAsync();

        await rt.InvokeActionAsync("click");
        Assert.Equal("Not responding", rt.State.Title);

        await Task.Delay(400);
        Assert.Equal("normal", rt.State.Title);
    }

    [Fact]
    public async Task Flyout_while_in_error_describes_the_error()
    {
        var c = new FakeConnector { OnStart = (_, _) => throw new InvalidOperationException("boom") };
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        var flyout = await rt.GetFlyoutAsync();

        Assert.Equal("Tile error", flyout!.Title);
        Assert.Contains(flyout.Rows, r => r.Text.Contains("boom"));
    }

    [Fact]
    public async Task Resumed_reaches_the_connector()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();
        var resumed = false;
        c.Context!.Resumed += (_, _) => resumed = true;

        rt.RaiseResumed();

        Assert.True(resumed);
    }

    [Fact]
    public async Task Dispose_disposes_connector()
    {
        var c = new FakeConnector();
        var rt = Create(c, new ImmediateDispatcher());
        await rt.StartAsync();

        await rt.DisposeAsync();

        Assert.True(c.Disposed);
    }

    [Fact]
    public async Task Unknown_connector_explains_itself()
    {
        var rt = new TileRuntime("t", "ghost", 132, new UnknownConnector("ghost"), NoSettings, new ImmediateDispatcher(), new CollectingLog());

        await rt.StartAsync();

        Assert.Equal("Unknown connector", rt.State.Title);
        Assert.Equal("ghost", rt.State.Subtitle);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter TileRuntimeTests`
Expected: build FAILS with `The type or namespace name 'IUiDispatcher' could not be found`.

- [ ] **Step 4: Write the implementation**

`src/TaskbarTiles.Host/Runtime/IUiDispatcher.cs`:
```csharp
using System.Windows.Threading;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Runs work on the UI thread. Abstracted so runtime logic is testable without WPF.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}
```

`src/TaskbarTiles.Host/Runtime/TileContext.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

sealed class TileContext(TileRuntime runtime, JsonElement settings, ITileLog log) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public ITileLog Log => log;
    public event EventHandler? Resumed;

    public void Update(TileState state) => runtime.Enqueue(state);

    internal void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}
```

`src/TaskbarTiles.Host/Runtime/TileRuntime.cs`:
```csharp
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>
/// One tile's connector and its latest state. Connector calls run on the thread pool with a timeout;
/// state changes are applied and announced on the UI thread.
/// </summary>
public sealed class TileRuntime : IAsyncDisposable
{
    public static TileState Initial { get; } = new() { Title = "…" };

    static readonly TileState NotResponding = new()
    {
        Title = "Not responding",
        Icon = new TileIcon("clock", TileColor.Warn),
    };

    readonly ITileConnector _connector;
    readonly IUiDispatcher _ui;
    readonly ITileLog _log;
    readonly TileContext _context;
    readonly TimeSpan _callTimeout;
    readonly TimeSpan _notRespondingFor;
    readonly CancellationTokenSource _lifetime = new();

    TileState? _pending;
    int _postQueued;
    TileState _lastGood = Initial;

    public TileRuntime(string tileId, string connectorId, double width, ITileConnector connector, JsonElement settings,
        IUiDispatcher ui, ITileLog log, TimeSpan? callTimeout = null, TimeSpan? notRespondingFor = null)
    {
        TileId = tileId;
        ConnectorId = connectorId;
        Width = width;
        _connector = connector;
        _ui = ui;
        _log = log;
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(10);
        _notRespondingFor = notRespondingFor ?? TimeSpan.FromSeconds(3);
        _context = new TileContext(this, settings, log);
    }

    public string TileId { get; }
    public string ConnectorId { get; }
    public double Width { get; }

    /// <summary>Read on the UI thread.</summary>
    public TileState State { get; private set; } = Initial;

    public Exception? LastError { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    public async Task StartAsync()
    {
        try
        {
            await Task.Run(() => _connector.StartAsync(_context, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task InvokeActionAsync(string actionId)
    {
        try
        {
            await RunWithTimeout(async ct => { await _connector.OnActionAsync(actionId, ct); return true; });
        }
        catch (TimeoutException) { ShowNotResponding(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task<FlyoutState?> GetFlyoutAsync()
    {
        if (LastError is { } error) return ErrorFlyout(error);
        try
        {
            return await RunWithTimeout(ct => _connector.GetFlyoutAsync(ct));
        }
        catch (TimeoutException) { ShowNotResponding(); return null; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
        catch (Exception ex) { Fail(ex); return ErrorFlyout(ex); }
    }

    public void RaiseResumed() => _context.RaiseResumed();

    /// <summary>Called by TileContext from any thread. Only the newest pending state is applied.</summary>
    internal void Enqueue(TileState state)
    {
        Volatile.Write(ref _pending, state);
        if (Interlocked.Exchange(ref _postQueued, 1) != 0) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _postQueued, 0);
            var latest = Interlocked.Exchange(ref _pending, null);
            if (latest is null) return;
            LastError = null;
            _lastGood = latest;
            SetState(latest);
        });
    }

    async Task<T> RunWithTimeout<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            return await Task.Run(() => call(cts.Token)).WaitAsync(_callTimeout);
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            throw;
        }
    }

    void Fail(Exception ex)
    {
        _log.Error($"Tile '{TileId}' ({ConnectorId}) failed", ex);
        _ui.Post(() =>
        {
            LastError = ex;
            SetState(new TileState
            {
                Title = "Error",
                Subtitle = ex.Message,
                Icon = new TileIcon("warning", TileColor.Warn),
                Tooltip = ex.Message,
            });
        });
    }

    void ShowNotResponding()
    {
        _log.Warn($"Tile '{TileId}' ({ConnectorId}) did not answer within {_callTimeout.TotalSeconds:0} s");
        _ui.Post(() => SetState(NotResponding));
        _ = Task.Delay(_notRespondingFor).ContinueWith(_ => _ui.Post(() =>
        {
            if (ReferenceEquals(State, NotResponding)) SetState(_lastGood);
        }), TaskScheduler.Default);
    }

    void SetState(TileState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    static FlyoutState ErrorFlyout(Exception ex) => new(
        "Tile error",
        ex.GetType().Name,
        ex.ToString().Split(Environment.NewLine).Take(8).Select(line => new FlyoutRow(line.Trim())).ToList(),
        []);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _connector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.Warn($"Tile '{TileId}' did not dispose cleanly: {ex.Message}");
        }
        _lifetime.Dispose();
    }
}
```

`src/TaskbarTiles.Host/Runtime/UnknownConnector.cs`:
```csharp
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Stands in for a connector id no loaded plugin provides, so the rest of the layout keeps working.</summary>
public sealed class UnknownConnector(string connectorId) : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.Update(new TileState
        {
            Title = "Unknown connector",
            Subtitle = connectorId,
            Icon = new TileIcon("warning", TileColor.Warn),
            Tooltip = $"No plugin provides the connector '{connectorId}'. Check the plugins folder and config.json.",
        });
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter TileRuntimeTests`
Expected: `Passed: 10`.

- [ ] **Step 6: Commit**

```bash
git add src/TaskbarTiles.Host/Runtime tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/TileRuntimeTests.cs
git commit -m "feat(runtime): add TileRuntime with coalescing, timeouts and error state"
```

---

### Task 8: Taskbar tracker

**Files:**
- Create: `src/TaskbarTiles.Host/Taskbar/ITaskbarSource.cs`, `src/TaskbarTiles.Host/Taskbar/NativeMethods.cs`, `src/TaskbarTiles.Host/Taskbar/TaskbarTracker.cs`
- Modify: `src/TaskbarTiles.Host/App.xaml.cs` (add `--dump-taskbars`)

**Interfaces:**
- Consumes: `PixelRect`, `TaskbarGeometry`, `TaskbarInfo`, `FreeZoneCalculator` (Task 5)
- Produces (namespace `TaskbarTiles.Host.Taskbar`):
  - `interface ITaskbarSource : IDisposable { IReadOnlyList<TaskbarInfo> Current { get; } event EventHandler? Changed; void Start(); }` — `Changed` is raised on the UI thread after every coalesced batch of taskbar events (also when nothing moved, so listeners can re-assert topmost).
  - `sealed class TaskbarTracker(System.Windows.Threading.Dispatcher dispatcher) : ITaskbarSource`
  - `static partial class NativeMethods` (internal P/Invoke used by Task 9's `TileBar` too: `SetWindowPos`, `GetWindowLongPtr`, `SetWindowLongPtr`, constants `HWND_TOPMOST`, `SWP_NOACTIVATE`, `SWP_SHOWWINDOW`, `GWL_EXSTYLE`, `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`)

This task talks to Explorer, so it has no unit tests (the geometry it feeds is covered by Task 5). Verification is the dump command in Step 4.

- [ ] **Step 1: Write the source abstraction and P/Invoke**

`src/TaskbarTiles.Host/Taskbar/ITaskbarSource.cs`:
```csharp
namespace TaskbarTiles.Host.Taskbar;

public interface ITaskbarSource : IDisposable
{
    /// <summary>One entry per taskbar (main and secondary). Read on the UI thread.</summary>
    IReadOnlyList<TaskbarInfo> Current { get; }

    /// <summary>Raised on the UI thread after each coalesced batch of taskbar events, even if nothing moved.</summary>
    event EventHandler? Changed;

    void Start();
}
```

`src/TaskbarTiles.Host/Taskbar/NativeMethods.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarTiles.Host.Taskbar;

static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_REORDER = 0x8004;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DPICHANGED = 0x02E0;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static PixelRect? Rect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) && r.Right > r.Left
            ? new PixelRect(r.Left, r.Top, r.Right, r.Bottom)
            : null;
}
```

- [ ] **Step 2: Write the tracker**

`src/TaskbarTiles.Host/Taskbar/TaskbarTracker.cs`:
```csharp
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.Taskbar;

/// <summary>
/// Tracks every taskbar window and publishes its free zone. Event-driven only:
///  - WinEvent hooks on Explorer (taskbar, Start, icon area, tray moved/resized/re-ordered) and on foreground changes
///  - ABN_FULLSCREENAPP from an appbar registration (fullscreen without a foreground change, e.g. F11)
///  - TaskbarCreated (Explorer restarted), WM_DISPLAYCHANGE, WM_DPICHANGED, WM_SETTINGCHANGE
/// </summary>
public sealed class TaskbarTracker(Dispatcher dispatcher) : ITaskbarSource
{
    const double MarginDip = 8;
    const double ClockReserveDip = 120;
    const int AppBarCallbackMessage = 0x8000 + 1; // WM_APP + 1

    readonly List<IntPtr> _hooks = [];
    readonly HashSet<IntPtr> _watched = [];
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    HwndSource? _messageWindow;
    bool _refreshQueued;
    uint _explorerPid;

    public IReadOnlyList<TaskbarInfo> Current { get; private set; } = [];
    public event EventHandler? Changed;

    public void Start()
    {
        // Hidden top-level window: receives broadcasts (WM_SETTINGCHANGE) and appbar notifications.
        _messageWindow = new HwndSource(new HwndSourceParameters("TaskbarTilesTracker")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        });
        _messageWindow.AddHook(WndProc);
        RegisterAppBar();
        Rehook();
        QueueRefresh();
    }

    // One cached delegate instance: native code holds a pointer to it, so it must stay alive.
    WinEventDelegate WinEventProc => _winEventProcCache ??= OnWinEvent;
    WinEventDelegate? _winEventProcCache;

    void Rehook()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();

        var main = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        GetWindowThreadProcessId(main, out _explorerPid);
        if (_explorerPid != 0)
            _hooks.Add(SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, WinEventProc,
                _explorerPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, WinEventProc,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_SYSTEM_FOREGROUND
            || evt == EVENT_OBJECT_REORDER
            || (idObject == OBJID_WINDOW && _watched.Contains(hwnd)))
            QueueRefresh();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated)
        {
            RegisterAppBar();
            Rehook();
            QueueRefresh();
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            // Secondary taskbars appear a moment after the display change; refresh again shortly after.
            QueueRefresh();
            RefreshAfter(TimeSpan.FromSeconds(1));
            RefreshAfter(TimeSpan.FromSeconds(3));
        }
        else if ((msg == AppBarCallbackMessage && wParam.ToInt32() == ABN_FULLSCREENAPP)
                 || msg is WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    void RefreshAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); QueueRefresh(); };
        timer.Start();
    }

    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    void Refresh()
    {
        _watched.Clear();
        var light = IsLightTaskbar();
        var list = new List<TaskbarInfo>();

        Collect("Shell_TrayWnd", iconAreaClass: "ReBarWindow32", hasTray: true, light, list);
        Collect("Shell_SecondaryTrayWnd", iconAreaClass: "WorkerW", hasTray: false, light, list);

        Current = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Collect(string taskbarClass, string iconAreaClass, bool hasTray, bool light, List<TaskbarInfo> into)
    {
        var bar = IntPtr.Zero;
        while ((bar = FindWindowEx(IntPtr.Zero, bar, taskbarClass, null)) != IntPtr.Zero)
        {
            if (Rect(bar) is not { } barRect) continue;

            var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
            var icons = FindWindowEx(bar, IntPtr.Zero, iconAreaClass, null);
            var tray = hasTray ? FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
            _watched.UnionWith(new[] { bar, start, icons, tray }.Where(h => h != IntPtr.Zero));

            var monitor = MonitorFromWindow(bar, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

            var geometry = new TaskbarGeometry(barRect, Rect(start), Rect(icons), Rect(tray));
            var zone = FreeZoneCalculator.Compute(geometry, (int)Math.Round(MarginDip * scale), (int)Math.Round(ClockReserveDip * scale));

            into.Add(new TaskbarInfo(
                DisplayId: DeviceId(info.szDevice),
                IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Taskbar: barRect,
                FreeZone: zone,
                Hidden: IsFullscreenOn(monitor, info.rcMonitor),
                IsLightTheme: light,
                DpiScale: scale));
        }
    }

    /// <summary>Stable monitor id (device interface path); falls back to \\.\DISPLAYn.</summary>
    static string DeviceId(string gdiDevice)
    {
        var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(gdiDevice, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME) && !string.IsNullOrEmpty(dd.DeviceID)
            ? dd.DeviceID
            : gdiDevice;
    }

    static bool IsFullscreenOn(IntPtr monitor, RECT monitorRect)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        if (ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        return r.Left <= monitorRect.Left && r.Top <= monitorRect.Top && r.Right >= monitorRect.Right && r.Bottom >= monitorRect.Bottom;
    }

    static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // Registering as an appbar (without reserving space) is the documented way to receive ABN_FULLSCREENAPP.
    void RegisterAppBar()
    {
        if (_messageWindow is null) return;
        var data = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = _messageWindow.Handle,
            uCallbackMessage = AppBarCallbackMessage,
        };
        SHAppBarMessage(ABM_REMOVE, ref data);
        SHAppBarMessage(ABM_NEW, ref data);
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        if (_messageWindow is not null)
        {
            var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = _messageWindow.Handle };
            SHAppBarMessage(ABM_REMOVE, ref data);
            _messageWindow.Dispose();
        }
    }
}
```

- [ ] **Step 3: Add the dump command**

Replace `src/TaskbarTiles.Host/App.xaml.cs`:
```csharp
using System.IO;
using System.Text.Json;
using System.Windows;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump-taskbars"))
        {
            DumpTaskbars();
            return;
        }
        Shutdown();
    }

    /// <summary>Diagnostics: writes what the tracker sees to %TEMP%\taskbartiles-dump.json and exits.</summary>
    void DumpTaskbars()
    {
        var tracker = new TaskbarTracker(Dispatcher);
        tracker.Changed += (_, _) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "taskbartiles-dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(tracker.Current, new JsonSerializerOptions { WriteIndented = true }));
            tracker.Dispose();
            Shutdown();
        };
        tracker.Start();
    }
}
```

- [ ] **Step 4: Verify against the real taskbars**

Run:
```bash
dotnet build src/TaskbarTiles.Host
src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/TaskbarTiles.exe --dump-taskbars
```
Then open `%TEMP%\taskbartiles-dump.json`.
Expected: one entry per connected display. The primary entry has `"IsPrimary": true`, `Taskbar` matching the bottom 48 DIP strip (on the user's main 4K display at 150 %: `0,2088,3840,2160`), `DisplayId` starting with `\\?\DISPLAY#`, `Hidden: false`. With centered icons, `FreeZone.IconsCentered` is `true` and `FreeZone.Zone.Right` ends 12 px before the Start button. Switch taskbar alignment to Left in Settings, rerun, and check `IconsCentered: false` with the zone ending 12 px before the tray.

- [ ] **Step 5: Run all tests and commit**

Run: `dotnet test TaskbarTiles.sln`
Expected: all pass.

```bash
git add src/TaskbarTiles.Host/Taskbar src/TaskbarTiles.Host/App.xaml.cs
git commit -m "feat(taskbar): track all taskbars via WinEvent hooks and appbar notifications"
```

---

### Task 9: Tile rendering (TileView, FlyoutWindow, TileBar)

**Files:**
- Create: `src/TaskbarTiles.Host/UI/Theme.cs`, `src/TaskbarTiles.Host/UI/Styles.xaml`, `src/TaskbarTiles.Host/UI/TileView.xaml`, `src/TaskbarTiles.Host/UI/TileView.xaml.cs`, `src/TaskbarTiles.Host/UI/FlyoutWindow.cs`, `src/TaskbarTiles.Host/UI/TileBar.cs`
- Modify: `src/TaskbarTiles.Host/App.xaml` (merge `UI/Styles.xaml`)
- Test: `tests/TaskbarTiles.Tests/ThemeTests.cs`

**Interfaces:**
- Consumes: `TileRuntime` (Task 7), `TaskbarInfo`, `NativeMethods` (Tasks 5, 8), `FitTiles` (Task 5), contract types (Task 2)
- Produces (namespace `TaskbarTiles.Host.UI`):
  - `static class Theme { void ApplyTo(ResourceDictionary resources, bool light); Brush ColorFor(TileColor color); string Glyph(string name); ImageSource? Decode(byte[]? bytes, int decodeWidth = 96); }`
  - `partial class TileView : UserControl` with ctor `(TileRuntime runtime, Func<bool> isLightTheme)`, `TileRuntime Runtime`, `void Detach()`
  - `static class FlyoutWindow { void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light); void CloseOpen(); }`
  - `sealed class TileBar : Window` with ctor `(string displayId)`, `string DisplayId`, `void SetTiles(IReadOnlyList<TileRuntime> runtimes)`, `void Apply(TaskbarInfo info)`, events `OpenConfigRequested`, `ExitRequested`

UI code is verified visually in Task 10. Only the pure helpers in `Theme` get unit tests.

- [ ] **Step 1: Write the failing test**

`tests/TaskbarTiles.Tests/ThemeTests.cs`:
```csharp
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData("play", "\uE768")]
    [InlineData("pause", "\uE769")]
    [InlineData("previous", "\uE892")]
    [InlineData("next", "\uE893")]
    [InlineData("warning", "\uE7BA")]
    public void Known_glyphs_map_to_segoe_fluent_codepoints(string name, string expected) =>
        Assert.Equal(expected, Theme.Glyph(name));

    [Fact]
    public void Unknown_glyph_falls_back_to_info() => Assert.Equal(Theme.Glyph("info"), Theme.Glyph("no-such-glyph"));

    [Fact]
    public void Decode_returns_null_for_garbage() => Assert.Null(Theme.Decode([1, 2, 3]));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test TaskbarTiles.sln --filter ThemeTests`
Expected: build FAILS with `The name 'Theme' does not exist in the current context`.

- [ ] **Step 3: Write Theme and styles**

`src/TaskbarTiles.Host/UI/Theme.cs`:
```csharp
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.UI;

/// <summary>Brushes for light/dark taskbars, semantic colors, glyph names and image decoding.</summary>
public static class Theme
{
    static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = "\uE768", ["pause"] = "\uE769", ["previous"] = "\uE892", ["next"] = "\uE893",
        ["check"] = "\uE73E", ["cancel"] = "\uE711", ["warning"] = "\uE7BA", ["refresh"] = "\uE72C",
        ["music"] = "\uE8D6", ["info"] = "\uE946", ["sync"] = "\uE895", ["cloud"] = "\uE753", ["clock"] = "\uE823",
    };

    public static string Glyph(string name) => Glyphs.TryGetValue(name, out var g) ? g : Glyphs["info"];

    public static Brush ColorFor(TileColor color) => color switch
    {
        TileColor.Ok => Frozen(0x3F, 0xB9, 0x7A),
        TileColor.Warn => Frozen(0xE6, 0xA2, 0x3C),
        TileColor.Bad => Frozen(0xE5, 0x53, 0x4B),
        TileColor.Accent => Frozen(0x4C, 0x7C, 0xE0),
        _ => Frozen(0x6B, 0x6F, 0x7A),
    };

    /// <summary>Sets the brush keys used by TileView, TileBar and FlyoutWindow.</summary>
    public static void ApplyTo(ResourceDictionary r, bool light)
    {
        r["TileForeground"] = light ? Frozen(0x1B, 0x1B, 0x1B) : Brushes.White;
        r["TileDim"] = light ? Frozen(0x99, 0, 0, 0) : Frozen(0xB3, 0xFF, 0xFF, 0xFF);
        r["TileFill"] = light ? Frozen(0x0A, 0, 0, 0) : Frozen(0x0F, 0xFF, 0xFF, 0xFF);
        r["TileHover"] = light ? Frozen(0x14, 0, 0, 0) : Frozen(0x1A, 0xFF, 0xFF, 0xFF);
        r["TileOutline"] = light ? Frozen(0x1F, 0, 0, 0) : Frozen(0x24, 0xFF, 0xFF, 0xFF);
        r["FlyoutBackground"] = light ? Frozen(0xF3, 0xF3, 0xF3) : Frozen(0x2B, 0x2A, 0x31);
        r["FlyoutBorder"] = light ? Frozen(0x1F, 0, 0, 0) : Frozen(0x17, 0xFF, 0xFF, 0xFF);
        r["ButtonHover"] = light ? Frozen(0x1A, 0, 0, 0) : Frozen(0x22, 0xFF, 0xFF, 0xFF);
    }

    public static ImageSource? Decode(byte[]? bytes, int decodeWidth = 96)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
```

`src/TaskbarTiles.Host/UI/Styles.xaml`:
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Inline tile buttons (same as the Spotify widget) -->
    <Style x:Key="IconButton" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource TileForeground}"/>
        <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets"/>
        <Setter Property="FontSize" Value="14"/>
        <Setter Property="Width" Value="30"/>
        <Setter Property="Height" Value="30"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="#01000000" CornerRadius="4">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ButtonHover}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.35"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Flyout action buttons -->
    <Style x:Key="FlyoutButton" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource TileForeground}"/>
        <Setter Property="FontSize" Value="12.5"/>
        <Setter Property="Padding" Value="12,5"/>
        <Setter Property="Margin" Value="0,0,8,0"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{DynamicResource TileFill}" BorderBrush="{DynamicResource FlyoutBorder}"
                            BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ButtonHover}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="FlyoutPrimaryButton" TargetType="Button" BasedOn="{StaticResource FlyoutButton}">
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="#4C7CE0" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="#5B89EA"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

Modify `src/TaskbarTiles.Host/App.xaml` — replace the empty `<Application.Resources>` element:
```xml
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="UI/Styles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test TaskbarTiles.sln --filter ThemeTests`
Expected: `Passed: 7`.

- [ ] **Step 5: Write TileView**

`src/TaskbarTiles.Host/UI/TileView.xaml`:
```xml
<UserControl x:Class="TaskbarTiles.Host.UI.TileView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Focusable="True">
    <Border x:Name="Root" CornerRadius="6" BorderThickness="1" Padding="6,0"
            Background="{DynamicResource TileFill}" BorderBrush="{DynamicResource TileOutline}">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <Grid x:Name="IconHost" Width="28" Height="28" Margin="0,0,8,0" VerticalAlignment="Center">
                <Border x:Name="IconBackground" CornerRadius="5"/>
                <TextBlock x:Name="IconGlyph" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="14"
                           Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                <Image x:Name="Picture" Stretch="UniformToFill">
                    <Image.Clip>
                        <RectangleGeometry Rect="0,0,28,28" RadiusX="4" RadiusY="4"/>
                    </Image.Clip>
                </Image>
            </Grid>

            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,4,0">
                <TextBlock x:Name="TitleText" FontSize="12.5" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"
                           Foreground="{DynamicResource TileForeground}"/>
                <TextBlock x:Name="SubtitleText" FontSize="11.5" TextTrimming="CharacterEllipsis"
                           Foreground="{DynamicResource TileDim}"/>
            </StackPanel>

            <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                <Canvas x:Name="SparkHost" Width="56" Height="26" Visibility="Collapsed">
                    <Polyline x:Name="Spark" StrokeThickness="1.5" Stroke="#7AA2F7"/>
                </Canvas>
                <StackPanel x:Name="ButtonPanel" Orientation="Horizontal"/>
            </StackPanel>

            <Grid x:Name="ProgressTrack" Grid.ColumnSpan="3" Height="2" VerticalAlignment="Bottom" Margin="0,0,0,3" Visibility="Collapsed">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition x:Name="ProgressDone" Width="0*"/>
                    <ColumnDefinition x:Name="ProgressLeft" Width="1*"/>
                </Grid.ColumnDefinitions>
                <Border Grid.ColumnSpan="2" CornerRadius="1" Background="{DynamicResource TileOutline}"/>
                <Border CornerRadius="1" Background="#4C7CE0"/>
            </Grid>
        </Grid>
    </Border>
</UserControl>
```

`src/TaskbarTiles.Host/UI/TileView.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Renders one tile's TileState and turns clicks into flyouts or actions.</summary>
public partial class TileView : UserControl
{
    readonly Func<bool> _isLightTheme;
    bool _urgent;

    public TileView(TileRuntime runtime, Func<bool> isLightTheme)
    {
        InitializeComponent();
        Runtime = runtime;
        _isLightTheme = isLightTheme;
        Width = runtime.Width;
        Runtime.StateChanged += OnStateChanged;
        MouseLeftButtonUp += OnClick;
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        KeyDown += (_, e) => { if (e.Key == Key.Enter) OnClick(this, null); };
        Render(runtime.State);
    }

    public TileRuntime Runtime { get; }

    public void Detach() => Runtime.StateChanged -= OnStateChanged;

    void OnStateChanged(object? sender, EventArgs e) => Render(Runtime.State);

    void Render(TileState s)
    {
        TitleText.Text = s.Title ?? "";
        SubtitleText.Text = s.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(s.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = string.IsNullOrEmpty(s.Tooltip) ? null : s.Tooltip;

        var picture = Theme.Decode(s.Image);
        Picture.Source = picture;
        IconGlyph.Text = picture is null && s.Icon is { } icon ? Theme.Glyph(icon.Glyph) : "";
        IconBackground.Background = picture is null && s.Icon is { } i2 ? Theme.ColorFor(i2.Color) : Brushes.Transparent;
        IconHost.Visibility = picture is null && s.Icon is null ? Visibility.Collapsed : Visibility.Visible;

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
            button.Click += async (_, _) => await Runtime.InvokeActionAsync(id);
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

        _urgent = s.Urgent;
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

    void UpdateBackground()
    {
        if (_urgent)
        {
            var bad = ((SolidColorBrush)Theme.ColorFor(TileColor.Bad)).Color;
            var pulse = new SolidColorBrush(Color.FromArgb(0x10, bad.R, bad.G, bad.B));
            Root.Background = pulse;
            Root.BorderBrush = Theme.ColorFor(TileColor.Bad);
            if (SystemParameters.ClientAreaAnimation)
                pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x30, bad.R, bad.G, bad.B),
                    TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            return;
        }
        Root.SetResourceReference(Border.BackgroundProperty, IsMouseOver ? "TileHover" : "TileFill");
        Root.SetResourceReference(Border.BorderBrushProperty, "TileOutline");
    }

    async void OnClick(object sender, MouseButtonEventArgs? e)
    {
        if (e?.Handled == true) return;
        var flyout = await Runtime.GetFlyoutAsync();
        if (flyout is null) await Runtime.InvokeActionAsync("click");
        else FlyoutWindow.ShowAbove(this, flyout, Runtime, _isLightTheme());
    }
}
```

- [ ] **Step 6: Write FlyoutWindow**

`src/TaskbarTiles.Host/UI/FlyoutWindow.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>The popup above a tile. Only one is open at a time; it closes when it loses focus.</summary>
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

    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light)
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
            SizeToContent = SizeToContent.Height,
            Width = WidthDip,
        };
        Theme.ApplyTo(window.Resources, light);
        window.Content = Build(state, runtime, window);
        window.Deactivated += (_, _) => { if (_open == window) CloseOpen(); };
        window.Loaded += (_, _) => Position(window, anchor);
        _open = window;
        window.Show();
        window.Activate();
    }

    static UIElement Build(FlyoutState state, TileRuntime runtime, Window window)
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
                button.Click += async (_, _) => { CloseOpen(); await runtime.InvokeActionAsync(id); };
                actions.Children.Add(button);
            }
            panel.Children.Add(actions);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return border;
    }

    static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>Centers the window above the anchor in physical pixels, 8 DIP above the taskbar.</summary>
    static void Position(Window window, FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null) return;
        var scale = source.CompositionTarget.TransformToDevice.M11;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var anchorWidthPx = anchor.ActualWidth * scale;

        var hwnd = new WindowInteropHelper(window).Handle;
        int w = (int)Math.Ceiling(window.ActualWidth * scale), h = (int)Math.Ceiling(window.ActualHeight * scale);
        int x = (int)(topLeft.X + anchorWidthPx / 2 - w / 2.0);
        int y = (int)(topLeft.Y - 4 * scale - 8 * scale - h); // anchor sits 4 DIP below the taskbar top
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, Math.Max(0, x), y, w, h, NativeMethods.SWP_SHOWWINDOW);
    }
}
```

- [ ] **Step 7: Write TileBar**

`src/TaskbarTiles.Host/UI/TileBar.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>One per display: a borderless topmost strip sized to its visible tiles, placed in the free zone.</summary>
public sealed class TileBar : Window
{
    const double GapDip = 6;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    readonly List<TileView> _views = [];
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId)
    {
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
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open config file" };
        open.Click += (_, _) => OpenConfigRequested?.Invoke(this, EventArgs.Empty);
        var exit = new MenuItem { Header = "Exit Taskbar Tiles" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        ContextMenu = menu;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            Reflow();
        };
    }

    public string DisplayId { get; }
    public event EventHandler? OpenConfigRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    public void SetTiles(IReadOnlyList<TileRuntime> runtimes)
    {
        if (_views.Select(v => v.Runtime).SequenceEqual(runtimes)) return;

        foreach (var v in _views) v.Detach();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var rt in runtimes)
        {
            var view = new TileView(rt, () => _light ?? false) { Margin = new Thickness(0, 0, GapDip, 0) };
            _views.Add(view);
            _panel.Children.Add(view);
        }
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

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var widths = _views.Select(v => v.Width).ToList();
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

- [ ] **Step 8: Build, run all tests, commit**

Run: `dotnet test TaskbarTiles.sln`
Expected: build succeeds, all tests pass.

```bash
git add src/TaskbarTiles.Host/UI src/TaskbarTiles.Host/App.xaml tests/TaskbarTiles.Tests/ThemeTests.cs
git commit -m "feat(ui): render tiles, flyouts and per-display tile bars"
```

---

### Task 10: Host composition

**Files:**
- Create: `src/TaskbarTiles.Host/AppPaths.cs`, `src/TaskbarTiles.Host/HostController.cs`
- Modify: `src/TaskbarTiles.Host/App.xaml.cs` (full host startup, keeps `--dump-taskbars`)
- Test: `tests/TaskbarTiles.Tests/HostControllerTests.cs`

**Interfaces:**
- Consumes: `ConnectorCatalog`, `PluginLoader` (Task 6); `ConfigStore`, `DisplayResolver`, `HostConfig`, `TileConfig` (Tasks 3–4); `ITaskbarSource`, `TaskbarTracker`, `TaskbarInfo` (Tasks 5, 8); `TileRuntime`, `UnknownConnector`, `IUiDispatcher`, `WpfUiDispatcher`, `FileLog` (Tasks 4, 7); `TileBar` (Task 9)
- Produces:
  - `static class AppPaths { string DataDir; string ConfigFile; string LogsDir; IReadOnlyList<string> PluginRoots; }`
  - `sealed class HostController : IAsyncDisposable` with ctor `(ConnectorCatalog catalog, Func<HostConfig> config, ITaskbarSource taskbars, IUiDispatcher ui, Func<string, ITileLog> logFor, Func<string, ITileBarView> createBar)`; `void Start()`; `void ConfigChanged()`; `void NotifyResumed()`; `IReadOnlyDictionary<string, TileRuntime> Runtimes` (for tests)
  - `interface ITileBarView { string DisplayId { get; } void SetTiles(IReadOnlyList<TileRuntime> runtimes); void Apply(TaskbarInfo info); void Close(); }` — `TileBar` implements it

The controller's sync logic is tested with a fake taskbar source and fake bars; the WPF wiring is checked by running the app.

- [ ] **Step 1: Write the failing tests**

Append to `tests/TaskbarTiles.Tests/TestDoubles.cs`:
```csharp
public sealed class FakeTaskbarSource : TaskbarTiles.Host.Taskbar.ITaskbarSource
{
    public IReadOnlyList<TaskbarTiles.Host.Taskbar.TaskbarInfo> Current { get; set; } = [];
    public event EventHandler? Changed;
    public void Start() { }
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }

    public static TaskbarTiles.Host.Taskbar.TaskbarInfo Display(string id, bool primary) => new(
        id, primary, new(0, 0, 1000, 48), new(new(8, 0, 500, 48), true), Hidden: false, IsLightTheme: false, DpiScale: 1);
}

public sealed class FakeBar(string displayId) : TaskbarTiles.Host.ITileBarView
{
    public string DisplayId { get; } = displayId;
    public IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> Tiles { get; private set; } = [];
    public TaskbarTiles.Host.Taskbar.TaskbarInfo? Info { get; private set; }
    public bool Closed { get; private set; }
    public void SetTiles(IReadOnlyList<TaskbarTiles.Host.Runtime.TileRuntime> runtimes) => Tiles = runtimes;
    public void Apply(TaskbarTiles.Host.Taskbar.TaskbarInfo info) => Info = info;
    public void Close() => Closed = true;
}
```

`tests/TaskbarTiles.Tests/HostControllerTests.cs`:
```csharp
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class HostControllerTests
{
    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    const string TwoTiles = """
    { "tiles": { "a": { "connector": "ghost" }, "b": { "connector": "ghost" } },
      "displays": { "primary": { "slots": ["a", "b"] }, "*": { "slots": ["b"] } } }
    """;

    (HostController Host, FakeTaskbarSource Source, Dictionary<string, FakeBar> Bars, Func<HostConfig> Cfg, Action<HostConfig> SetCfg) Create(HostConfig cfg)
    {
        var current = cfg;
        var source = new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] };
        var bars = new Dictionary<string, FakeBar>();
        var host = new HostController(ConnectorCatalog.Empty, () => current, source, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars[id] = new FakeBar(id));
        return (host, source, bars, () => current, c => current = c);
    }

    [Fact]
    public void Start_creates_a_bar_per_display_with_its_tiles()
    {
        var (host, _, bars, _, _) = Create(Parse(TwoTiles));

        host.Start();

        Assert.Equal(new[] { "a", "b" }, bars["MAIN"].Tiles.Select(t => t.TileId));
        Assert.Equal("MAIN", bars["MAIN"].Info!.DisplayId);
    }

    [Fact]
    public void Tile_on_two_displays_shares_one_runtime()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        host.Start();

        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("SECOND", false)];
        source.Raise();

        Assert.Same(bars["MAIN"].Tiles[1], bars["SECOND"].Tiles[0]);
        Assert.Equal(2, host.Runtimes.Count);
    }

    [Fact]
    public void Removed_display_closes_its_bar()
    {
        var (host, source, bars, _, _) = Create(Parse(TwoTiles));
        source.Current = [FakeTaskbarSource.Display("MAIN", true), FakeTaskbarSource.Display("SECOND", false)];
        host.Start();

        source.Current = [FakeTaskbarSource.Display("MAIN", true)];
        source.Raise();

        Assert.True(bars["SECOND"].Closed);
    }

    [Fact]
    public void Config_change_keeps_unchanged_runtimes_and_drops_removed_ones()
    {
        var (host, _, bars, _, setCfg) = Create(Parse(TwoTiles));
        host.Start();
        var a = host.Runtimes["a"];

        setCfg(Parse("""{ "tiles": { "a": { "connector": "ghost" } }, "displays": { "primary": { "slots": ["a"] } } }"""));
        host.ConfigChanged();

        Assert.Same(a, host.Runtimes["a"]);
        Assert.False(host.Runtimes.ContainsKey("b"));
        Assert.Equal(new[] { "a" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Changed_settings_recreate_the_runtime()
    {
        var (host, _, _, _, setCfg) = Create(Parse(TwoTiles));
        host.Start();
        var a = host.Runtimes["a"];

        setCfg(Parse("""{ "tiles": { "a": { "connector": "ghost", "settings": { "x": 1 } }, "b": { "connector": "ghost" } }, "displays": { "primary": { "slots": ["a", "b"] } } }"""));
        host.ConfigChanged();

        Assert.NotSame(a, host.Runtimes["a"]);
    }

    [Fact]
    public void Unknown_connector_gets_placeholder_runtime()
    {
        var (host, _, _, _, _) = Create(Parse(TwoTiles));
        host.Start();
        Assert.Equal("ghost", host.Runtimes["a"].ConnectorId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test TaskbarTiles.sln --filter HostControllerTests`
Expected: build FAILS with `The type or namespace name 'ITileBarView' does not exist in the namespace 'TaskbarTiles.Host'`.

- [ ] **Step 3: Write AppPaths, ITileBarView and HostController**

`src/TaskbarTiles.Host/AppPaths.cs`:
```csharp
using System.IO;

namespace TaskbarTiles.Host;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarTiles");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Built-in plugins next to the exe first, then the user's plugins.</summary>
    public static IReadOnlyList<string> PluginRoots =>
        [Path.Combine(AppContext.BaseDirectory, "plugins"), Path.Combine(DataDir, "plugins")];
}
```

`src/TaskbarTiles.Host/HostController.cs`:
```csharp
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

/// <summary>What HostController needs from a per-display bar. TileBar implements it; tests use a fake.</summary>
public interface ITileBarView
{
    string DisplayId { get; }
    void SetTiles(IReadOnlyList<TileRuntime> runtimes);
    void Apply(TaskbarInfo info);
    void Close();
}

/// <summary>
/// Keeps runtimes and bars in sync with config and connected displays.
/// One runtime per tile id placed on any display; one bar per display. Runs on the UI thread.
/// </summary>
public sealed class HostController(
    ConnectorCatalog catalog,
    Func<HostConfig> config,
    ITaskbarSource taskbars,
    IUiDispatcher ui,
    Func<string, ITileLog> logFor,
    Func<string, ITileBarView> createBar) : IAsyncDisposable
{
    const double DefaultWidth = 132;

    readonly Dictionary<string, (TileRuntime Runtime, TileConfig Config)> _runtimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITileBarView> _bars = new(StringComparer.Ordinal);

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
        foreach (var (runtime, _) in _runtimes.Values) runtime.RaiseResumed();
    }

    void Sync()
    {
        var cfg = config();
        var displays = taskbars.Current;
        var layouts = displays.ToDictionary(d => d.DisplayId,
            d => DisplayResolver.VisibleTileIds(DisplayResolver.Resolve(cfg, d.DisplayId, d.IsPrimary)));

        SyncRuntimes(cfg, layouts.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal));

        foreach (var gone in _bars.Keys.Except(layouts.Keys).ToList())
        {
            _bars[gone].Close();
            _bars.Remove(gone);
        }

        foreach (var info in displays)
        {
            if (!_bars.TryGetValue(info.DisplayId, out var bar))
                _bars[info.DisplayId] = bar = createBar(info.DisplayId);
            bar.SetTiles(layouts[info.DisplayId].Select(id => _runtimes[id].Runtime).ToList());
            bar.Apply(info);
        }
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

            var connector = catalog.Create(tileCfg.Connector) ?? new UnknownConnector(tileCfg.Connector);
            var width = catalog.Connectors.TryGetValue(tileCfg.Connector, out var d) ? d.Width : DefaultWidth;
            var runtime = new TileRuntime(id, tileCfg.Connector, width, connector, tileCfg.Settings, ui, logFor(id));
            _runtimes[id] = (runtime, tileCfg);
            _ = runtime.StartAsync();
        }
    }

    static bool SameConfig(TileConfig a, TileConfig b) =>
        a.Connector == b.Connector && a.Settings.GetRawText() == b.Settings.GetRawText();

    public async ValueTask DisposeAsync()
    {
        foreach (var bar in _bars.Values) bar.Close();
        _bars.Clear();
        var disposals = _runtimes.Values.Select(e => e.Runtime.DisposeAsync().AsTask()).ToList();
        _runtimes.Clear();
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }
}
```

Make `TileBar` implement the interface — in `src/TaskbarTiles.Host/UI/TileBar.cs` change the class declaration line to:
```csharp
public sealed class TileBar : Window, ITileBarView
```
and add `using TaskbarTiles.Host;` is not needed (same root namespace); `Close()` is already provided by `Window`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test TaskbarTiles.sln --filter HostControllerTests`
Expected: `Passed: 6`.

- [ ] **Step 5: Wire up App startup**

Replace `src/TaskbarTiles.Host/App.xaml.cs`:
```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    Mutex? _singleInstance;
    ConfigStore? _config;
    TaskbarTracker? _tracker;
    HostController? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump-taskbars"))
        {
            DumpTaskbars();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\TaskbarTiles.Host", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        var log = FileLog.For(AppPaths.LogsDir, "host");
        DispatcherUnhandledException += (_, a) => { log.Error("Unhandled UI exception", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
        _config = new ConfigStore(AppPaths.ConfigFile, log);
        _config.Load();

        var ui = new WpfUiDispatcher(Dispatcher);
        _tracker = new TaskbarTracker(Dispatcher);
        _host = new HostController(catalog, () => _config.Current, _tracker, ui,
            name => FileLog.For(AppPaths.LogsDir, name), CreateBar);

        _config.Changed += (_, _) => ui.Post(() => _host.ConfigChanged());
        _config.StartWatching();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _host.Start();
        log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
    }

    ITileBarView CreateBar(string displayId)
    {
        var bar = new TileBar(displayId);
        bar.OpenConfigRequested += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.ConfigFile) { UseShellExecute = true });
        bar.ExitRequested += (_, _) => Shutdown();
        bar.Show();
        return bar;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _host?.NotifyResumed());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        FlyoutWindow.CloseOpen();
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tracker?.Dispose();
        _config?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Diagnostics: writes what the tracker sees to %TEMP%\taskbartiles-dump.json and exits.</summary>
    void DumpTaskbars()
    {
        var tracker = new TaskbarTracker(Dispatcher);
        tracker.Changed += (_, _) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "taskbartiles-dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(tracker.Current, new JsonSerializerOptions { WriteIndented = true }));
            tracker.Dispose();
            Shutdown();
        };
        tracker.Start();
    }
}
```

- [ ] **Step 6: Smoke-test without plugins**

Run:
```bash
dotnet build src/TaskbarTiles.Host
src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/TaskbarTiles.exe
```
Expected: `%LOCALAPPDATA%\TaskbarTiles\config.json` exists (default content). On the main taskbar, at the left end with centered icons, one tile appears: warning icon, "Unknown connector", subtitle "media" (no plugins yet). Hover shows the tooltip. Right-click shows "Open config file" and "Exit Taskbar Tiles"; Exit closes it. `%LOCALAPPDATA%\TaskbarTiles\logs\host.log` contains `Started with 0 connector(s)`.

Note: if an older `config.json` exists from earlier experiments, rename it first so the default is created.

- [ ] **Step 7: Run all tests and commit**

Run: `dotnet test TaskbarTiles.sln`
Expected: all pass.

```bash
git add src/TaskbarTiles.Host tests/TaskbarTiles.Tests/TestDoubles.cs tests/TaskbarTiles.Tests/HostControllerTests.cs
git commit -m "feat(host): compose config, plugins, taskbar tracking and tile bars"
```

---

### Task 11: Media plugin

**Files:**
- Create: `src/TaskbarTiles.Plugins.Media/TaskbarTiles.Plugins.Media.csproj`, `src/TaskbarTiles.Plugins.Media/MediaConnector.cs`
- Modify: `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj` (ship media plugin under `plugins\`)

**Interfaces:**
- Consumes: contract types (Task 2); `CopyPlugins.targets` (Task 6)
- Produces: connector id `media`, width 268. Settings: `app` (session filter, default `"Spotify"`), `launch` (URI or exe to open the app, default `"<app lowercased>:"`, e.g. `spotify:`). Action ids: `prev`, `toggle`, `next`, `open`, `click`.

Note: Windows media sessions do not expose the play queue, so the popup shows album and position instead of "up next" (deviation from the spec's media bullet).

- [ ] **Step 1: Create the project**

```bash
dotnet new classlib -n TaskbarTiles.Plugins.Media -o src/TaskbarTiles.Plugins.Media -f net9.0
dotnet sln add src/TaskbarTiles.Plugins.Media
```
Delete `src/TaskbarTiles.Plugins.Media/Class1.cs`.

`src/TaskbarTiles.Plugins.Media/TaskbarTiles.Plugins.Media.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides Contracts at runtime; don't ship a copy with the plugin. -->
    <ProjectReference Include="..\TaskbarTiles.Contracts\TaskbarTiles.Contracts.csproj" Private="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the connector**

`src/TaskbarTiles.Plugins.Media/MediaConnector.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;
using TaskbarTiles.Contracts;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarTiles.Plugins.Media;

/// <summary>
/// Now-playing tile for one app's Windows media session (Spotify by default).
/// Ported from SpotifyTaskbar: no Spotify API or login, just the system media controls.
/// </summary>
[TileConnector("media", Width = 268)]
public sealed class MediaConnector : ITileConnector
{
    readonly SemaphoreSlim _gate = new(1, 1);
    ITileContext? _ctx;
    string _app = "Spotify";
    string _launch = "spotify:";
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _artKey;
    byte[]? _art;

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        if (ctx.Settings.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String) _app = app.GetString()!;
        _launch = ctx.Settings.TryGetProperty("launch", out var launch) && launch.ValueKind == JsonValueKind.String
            ? launch.GetString()!
            : _app.ToLowerInvariant() + ":";

        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        ctx.Resumed += OnResumed;
        PickSession();
        await PublishAsync();
    }

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        var s = _session;
        switch (actionId)
        {
            case "prev" when s is not null: await s.TrySkipPreviousAsync(); break;
            case "toggle" when s is not null: await s.TryTogglePlayPauseAsync(); break;
            case "next" when s is not null: await s.TrySkipNextAsync(); break;
            case "open":
            case "click":
                Launch();
                break;
        }
    }

    public async Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null; // a click then launches the app

        var props = await s.TryGetMediaPropertiesAsync();
        var timeline = s.GetTimelineProperties();
        var rows = new List<FlyoutRow>();
        if (!string.IsNullOrEmpty(props.AlbumTitle)) rows.Add(new FlyoutRow("Album", props.AlbumTitle));
        if (timeline.EndTime > TimeSpan.Zero) rows.Add(new FlyoutRow("Position", $"{Format(timeline.Position)} / {Format(timeline.EndTime)}"));
        rows.Add(new FlyoutRow("Source", s.SourceAppUserModelId));

        return new FlyoutState(
            string.IsNullOrEmpty(props.Title) ? _app : props.Title,
            props.Artist,
            rows,
            [new FlyoutAction("open", $"Open {_app}", Primary: true)],
            _art);
    }

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        PickSession();
        _ = PublishAsync();
    }

    void OnResumed(object? sender, EventArgs e) => _ = PublishAsync();

    void PickSession()
    {
        var match = _manager?.GetSessions()
            .FirstOrDefault(x => x.SourceAppUserModelId.Contains(_app, StringComparison.OrdinalIgnoreCase));
        if (match == _session) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = match;
        _artKey = null;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = PublishAsync();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = PublishAsync();

    async Task PublishAsync()
    {
        if (_ctx is null) return;
        await _gate.WaitAsync();
        try
        {
            var s = _session;
            if (s is null)
            {
                _ctx.Update(new TileState
                {
                    Title = _app,
                    Subtitle = "Not running",
                    Icon = new TileIcon("music"),
                    Tooltip = $"Click to open {_app}",
                });
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var key = $"{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artKey)
            {
                _artKey = key;
                _art = props.Thumbnail is null ? null : await ReadAllAsync(props.Thumbnail);
            }

            _ctx.Update(new TileState
            {
                Title = string.IsNullOrEmpty(props.Title) ? _app : props.Title,
                Subtitle = props.Artist,
                Image = _art,
                Icon = new TileIcon("music"),
                Tooltip = $"{props.Title}\n{props.Artist}\n{props.AlbumTitle}",
                Buttons =
                [
                    new TileButton("prev", "previous", "Previous", playback?.Controls.IsPreviousEnabled ?? false),
                    new TileButton("toggle", playing ? "pause" : "play", "Play/Pause", true),
                    new TileButton("next", "next", "Next", playback?.Controls.IsNextEnabled ?? false),
                ],
            });
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"Media update failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<byte[]> ReadAllAsync(IRandomAccessStreamReference reference)
    {
        using var winStream = await reference.OpenReadAsync();
        using var stream = winStream.AsStreamForRead();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    void Launch()
    {
        try { Process.Start(new ProcessStartInfo(_launch) { UseShellExecute = true }); }
        catch (Exception ex) { _ctx?.Log.Warn($"Could not open {_launch}: {ex.Message}"); }
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public ValueTask DisposeAsync()
    {
        if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
        if (_ctx is not null) _ctx.Resumed -= OnResumed;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Ship it with the host**

Modify `src/TaskbarTiles.Host/TaskbarTiles.Host.csproj` — add before `</Project>`:
```xml
  <ItemGroup>
    <!-- Build order only; copied to plugins\TaskbarTiles.Plugins.Media\ by CopyPlugins.targets. -->
    <ProjectReference Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" ReferenceOutputAssembly="false" />
    <PluginProject Include="..\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.csproj" />
  </ItemGroup>
  <Import Project="..\..\build\CopyPlugins.targets" />
```

- [ ] **Step 4: Build and check the plugin lands in place**

Run: `dotnet build TaskbarTiles.sln`
Expected: build succeeds and `src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/plugins/TaskbarTiles.Plugins.Media/TaskbarTiles.Plugins.Media.dll` exists.

- [ ] **Step 5: Verify parity with SpotifyTaskbar**

Close SpotifyTaskbar first (right-click it → Exit) so the two don't overlap. Run `src/TaskbarTiles.Host/bin/Debug/net9.0-windows10.0.19041.0/TaskbarTiles.exe` with Spotify playing.

Expected, all of:
1. Tile shows album art, title, artist and previous / pause / next buttons; buttons work and play/pause toggles its icon.
2. Changing the track updates art and text within about a second.
3. Clicking the tile body opens a popup above it with art, title, artist, album, position and an "Open Spotify" button.
4. With Spotify closed, the tile shows "Spotify / Not running" with a music icon; clicking it opens Spotify.
5. Fullscreen video on the main display hides the tile; fullscreen on the other display does not.
6. Switching taskbar alignment Center ⇄ Left moves the tile between the left end and just left of the tray.
7. Restarting Explorer (`taskkill /f /im explorer.exe` then `start explorer`) leaves the tile in place.
8. `logs\host.log` lists `Loaded connector 'media'`.

- [ ] **Step 6: Run all tests and commit**

Run: `dotnet test TaskbarTiles.sln`
Expected: all pass.

```bash
git add TaskbarTiles.sln src/TaskbarTiles.Plugins.Media src/TaskbarTiles.Host/TaskbarTiles.Host.csproj
git commit -m "feat(media): port Spotify widget to the media plugin"
```

---

### Task 12: Install and retire SpotifyTaskbar

**Files:**
- Delete: `SpotifyTaskbar/` (repo copy, after parity is confirmed)
- Outside the repo: `%LOCALAPPDATA%\TaskbarTiles\app\` (install), Startup shortcuts

**Interfaces:**
- Consumes: the publishable host from Task 11
- Produces: TaskbarTiles starts at sign-in; SpotifyTaskbar no longer runs

Ask the user before Step 4: it deletes the old install folder.

- [ ] **Step 1: Publish**

```bash
dotnet publish src/TaskbarTiles.Host -c Release -o "$LOCALAPPDATA/TaskbarTiles/app"
```
Expected: `%LOCALAPPDATA%\TaskbarTiles\app\TaskbarTiles.exe` and `%LOCALAPPDATA%\TaskbarTiles\app\plugins\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.dll` exist.

- [ ] **Step 2: Swap the Startup shortcuts (PowerShell)**

```powershell
Get-Process SpotifyTaskbar, TaskbarTiles -ErrorAction SilentlyContinue | Stop-Process -Force
$startup = [Environment]::GetFolderPath('Startup')
Remove-Item (Join-Path $startup 'SpotifyTaskbar.lnk') -ErrorAction SilentlyContinue
$exe = "$env:LOCALAPPDATA\TaskbarTiles\app\TaskbarTiles.exe"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startup 'TaskbarTiles.lnk'))
$s.TargetPath = $exe; $s.WorkingDirectory = Split-Path $exe; $s.Description = 'Taskbar Tiles'; $s.Save()
Start-Process $exe
```
Expected: `TaskbarTiles.lnk` in the Startup folder, no `SpotifyTaskbar.lnk`, the media tile visible. Task Manager → Startup apps lists "TaskbarTiles".

- [ ] **Step 3: Confirm with the user**

Ask the user to check the tile and the Task Manager entry. Continue only after they confirm.

- [ ] **Step 4: Remove the old install and the repo copy (after confirmation)**

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\SpotifyTaskbar"
```
```bash
git rm -r -q SpotifyTaskbar
git commit -m "chore: retire SpotifyTaskbar, replaced by the media plugin"
```

---

## Self-review notes

- Spec coverage for milestone 1: contracts (Task 2), config model/parse/resolve/watch (Tasks 3–4), multi-display tracker with geometry-based free zone, fullscreen per monitor, Explorer restart, display changes (Tasks 5, 8), plugin loading with shared host assemblies (Task 6), runtime with coalescing, 10 s timeouts, "Not responding", error state and error popup (Task 7), declarative rendering incl. image, icon, buttons, progress, sparkline, urgent pulse, tooltip, popups, light/dark theme (Task 9), shared runtimes across displays and config hot reload (Task 10), media plugin (Task 11), Startup-folder autostart and retirement (Task 12).
- Deferred by design to later milestone plans: backoff restarts, script plugin and full context menu (M2); stacks, compact/overflow, edit mode, per-display editing (M3); hot reload of plugin DLLs, automatic restart on leaked unload, watchdog, state cache (M4).
- Deviation: media popup shows album and position instead of "up next" (Windows media sessions expose no queue).
