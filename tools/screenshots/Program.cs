// Renders every bundled tile with made-up sample data, the way the taskbar draws it, to <out>\tile-<kind>.png
// (default docs\images). Nothing reads this machine: no media session, clipboard, devices or Claude account.
//   dotnet run --project tools/screenshots [-- <out folder>]
//
// --preview runs a script kind folder for real, as the app would, and renders what it prints: for checking a new tile
// without installing it. It reports tile.json and script problems, the popup, and where the picture went.
//   dotnet run --project tools/screenshots -- --preview <kind folder> [<out.png>]
//
// --sizes draws each tile at a range of widths, as edit mode can resize it, to <out>\sizes-<kind>.png.
//   dotnet run --project tools/screenshots -- --sizes <out folder>
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;
using TaskbarTiles.Plugins.Battery;
using TaskbarTiles.Plugins.Clipboard;
using TaskbarTiles.Plugins.Scenes;
using TaskbarTiles.Plugins.Script;
using TaskbarTiles.Plugins.SystemMonitor;

const double Scale = 2;        // output pixels per DIP
const double TaskbarGap = 4;   // taskbar around the tile, in DIP
if (args is ["--preview", var kindFolder, ..])
    return await Preview(Path.GetFullPath(kindFolder), args.Length > 2 ? Path.GetFullPath(args[2]) : null);

var repo = FindRepo();
var sizes = args is ["--sizes", ..];
if (sizes) args = args[1..];
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repo, "docs", "images"));
Directory.CreateDirectory(outDir);
var claudeLogo = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(repo, "tiles", "claude-usage", "claude.png")));
var dockerLogo = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(repo, "tiles", "docker", "docker.png")));

// The scenes animate: run each connector for a moment, at each width drawn, and keep its last frame.
var noon = new DateTime(2026, 6, 21, 17, 30, 0);
double[] widths = sizes ? SizeWidths() : [200];
// Seconds each runs for: long enough for a grown snake and a race under way.
var scenes = new (string Kind, Func<ITileConnector> Create, int Seconds)[]
{
    ("aquarium", () => new AquariumConnector(new Random(7), () => noon), 4),
    ("snake", () => new SnakeConnector(new Random(7), () => noon), 20),
    ("racers", () => new RacersConnector(new Random(7), () => noon), 14),
    ("cozy-farm", () => new FarmConnector(new Random(7), () => noon, () => (50.08, 14.42)), 4),
};
var frames = new Dictionary<(string, double), TileState>();
await Task.WhenAll(scenes.SelectMany(scene => widths.Select(async width =>
{
    var ctx = new Capture(scene.Kind) { Width = width };
    var connector = scene.Create();
    await connector.StartAsync(ctx, CancellationToken.None);
    await Task.Delay(TimeSpan.FromSeconds(scene.Seconds));
    await connector.DisposeAsync();
    lock (frames) frames[(scene.Kind, width)] = ctx.Last ?? throw new InvalidOperationException($"{scene.Kind} drew nothing");
})));
Func<double, TileState> Scene(string kind) => width => frames[(kind, sizes ? width : 200)];

RunSta(() =>
{
    static Func<double, TileState> Fixed(TileState state) => _ => state;
    var tiles = new List<(string Kind, double Width, Func<double, TileState> State)>
    {
        ("media", 268, Fixed(Media())),
        ("claude-usage", 220, Fixed(Script($$"""{ "title": "5h 34%/52% · resets 16:40", "subtitle": "Week 21%/43% · resets Fri", "progress": 0.34, "progressMark": 0.52, "icon": { "glyph": "clock", "color": "ok" }, "image": "{{claudeLogo}}" }"""))),
        ("system", 200, Fixed(SystemTile())),
        ("battery", 132, Fixed(BatteryView.Tile(BatteryView.Sorted([
            new DeviceBattery("m", "MX Master 3S", DeviceType.Mouse, 65),
            new DeviceBattery("k", "MX Keys", DeviceType.Keyboard, 90),
            new DeviceBattery("h", "WH-1000XM5", DeviceType.Headphones, 40, Charging: true)])))),
        ("clipboard", 200, Fixed(ClipboardView.Tile([
            new ClipEntry(2, new ClipItem(ClipKind.Text, "git switch -c feat/new-tile"), noon.AddMinutes(-2)),
            new ClipEntry(1, new ClipItem(ClipKind.Image, "Image 1280×720", "Image 1280×720"), noon.AddMinutes(-9))]))),
        ("docker", 200, Fixed(Script($$"""{ "title": "Docker", "subtitle": "4 running · 1 stopped", "icon": { "glyph": "package", "color": "ok" }, "image": "{{dockerLogo}}" }"""))),
        ("git", 220, Fixed(Script("""{ "title": "my-app · main", "subtitle": "3 changed, 1 new · ↑2", "icon": { "glyph": "branch", "color": "accent" } }"""))),
        ("uptime", 132, Fixed(Script("""{ "title": "17:30", "subtitle": "up 3d 4h", "icon": { "glyph": "clock", "color": "accent" } }"""))),
        ("aquarium", 200, Scene("aquarium")),
        ("cozy-farm", 200, Scene("cozy-farm")),
        ("snake", 200, Scene("snake")),
        ("racers", 200, Scene("racers")),
    };
    foreach (var (kind, width, state) in tiles)
        if (sizes) Save(RenderSizes(state), Path.Combine(outDir, $"sizes-{kind}.png"));
        else Save(Render(width, state(width)), Path.Combine(outDir, $"tile-{kind}.png"));
});
Console.WriteLine($"Wrote {(sizes ? "the sizes of " : "")}12 tiles to {outDir}");
return 0;

// Exit code 0 when the tile drew without errors, 1 otherwise.
static async Task<int> Preview(string folder, string? output)
{
    var kind = Path.GetFileName(folder);
    var jsonPath = Path.Combine(folder, "tile.json");
    if (!File.Exists(jsonPath)) { Console.WriteLine($"No tile.json in {folder}."); return 1; }
    var json = TaskbarTiles.Host.Plugins.TileJson.Parse(File.ReadAllText(jsonPath), $@"tiles\{kind}\tile.json");
    if (json.Error is { } error) { Console.WriteLine($"tile.json: {error}"); return 1; }
    Console.WriteLine($"tile.json: \"{json.Name}\" ({json.Connector}), width {json.Width?.ToString() ?? "default"}, group \"{json.Group}\", {json.Fields?.Count ?? 0} settings fields");
    if (json.Connector != "script")
    {
        Console.WriteLine("Only script kinds run here; a DLL kind is checked by its unit tests and in the app.");
        return 0;
    }

    var ctx = new Capture(kind, json.Settings, folder, echo: true);
    var connector = new ScriptConnector();
    await connector.StartAsync(ctx, CancellationToken.None);
    var waited = TimeSpan.Zero;
    while (ctx.Last is null && ctx.Error is null && waited < TimeSpan.FromSeconds(30))
    {
        await Task.Delay(100);
        waited += TimeSpan.FromMilliseconds(100);
    }
    var flyout = ctx.Error is null ? await connector.GetFlyoutAsync(CancellationToken.None) : null;
    await connector.DisposeAsync();

    if (ctx.Error is { } failed) { Console.WriteLine($"Error look: {failed}"); return 1; }
    if (ctx.Last is not { } state) { Console.WriteLine("Nothing printed within 30 s (a tile with only click, flyout or action hooks shows its id)."); return 1; }
    Console.WriteLine($"Tile: title \"{state.Title}\", subtitle \"{state.Subtitle}\"");
    if (flyout is not null)
    {
        Console.WriteLine($"Popup: \"{flyout.Title}\" / \"{flyout.Subtitle}\"");
        foreach (var row in flyout.Rows) Console.WriteLine($"  {row.Text}: {row.Value}");
        foreach (var action in flyout.Actions) Console.WriteLine($"  [{action.Label}] runs action:{action.Id}");
    }
    output ??= Path.Combine(Path.GetTempPath(), $"tile-preview-{kind}.png");
    RunSta(() => Save(Render(json.Width ?? 132, state), output));
    Console.WriteLine($"Picture: {output}");
    return ctx.Warnings > 0 ? 1 : 0;
}

static void RunSta(Action draw)
{
    var thread = new Thread(() =>
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TaskbarTiles;component/UI/Styles.xaml") });
        draw();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
}

static TileState Script(string json) =>
    ScriptOutput.ParseTile(json).Value ?? throw new InvalidOperationException("bad sample: " + json);

static TileState SystemTile()
{
    const long Gib = 1L << 30;
    var rng = new Random(3);
    SystemReading Reading(double cpu) => new(cpu, new MemoryReading(44 * Gib, 64 * Gib), 13, 294 * 1024, 23 * 1024,
        [new DiskReading("C:", 885 * Gib, 493 * Gib), new DiskReading("D:", 977 * Gib, 220 * Gib)], 32);
    var history = new SystemHistory();
    for (var i = 0; i < 60; i++) history.Push(Reading(8 + rng.NextDouble() * 10 + 30 * Math.Max(0, Math.Sin(i / 6.0))));
    Metric[] all = [Metric.Cpu, Metric.Ram, Metric.Gpu, Metric.Network, Metric.Disk];
    return SystemView.Tile(Reading(23), history, all, rotating: true, Metric.Cpu);
}

// Album art drawn here, not a real cover.
static TileState Media()
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0x5B, 0x3C, 0xC4), 45), null, new Rect(0, 0, 96, 96));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD1, 0x66)), null, new Point(62, 38), 20, 20);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x30)), null, new Rect(0, 64, 96, 32));
    }
    var bmp = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    return new TileState
    {
        Title = "Golden Hour Drive",
        Subtitle = "Lumen & the Tides",
        Image = Png(bmp),
        Icon = new TileIcon("music"),
        Buttons = [new("prev", "previous", "Previous"), new("toggle", "pause", "Play/Pause"), new("next", "next", "Next")],
        Progress = 0.42,
    };
}

// The tile at widths from the smallest edit mode allows to the largest, one under the other.
static RenderTargetBitmap RenderSizes(Func<double, TileState> state)
{
    var column = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) };
    foreach (var width in SizeWidths())
        column.Children.Add(new Image { Source = Render(width, state(width)), Width = width + 2 * TaskbarGap, HorizontalAlignment = HorizontalAlignment.Left });
    column.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    column.Arrange(new Rect(column.DesiredSize));
    column.UpdateLayout();
    var bmp = new RenderTargetBitmap((int)(column.ActualWidth * Scale), (int)(column.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
    bmp.Render(column);
    return bmp;
}

// The tile as TileView draws it on a dark taskbar, with a little taskbar around it.
static RenderTargetBitmap Render(double width, TileState state)
{
    var runtime = new TileRuntime("shot", "shot", width, () => new Fixed(state), JsonDocument.Parse("{}").RootElement.Clone(), new Inline(), new NoLog());
    runtime.StartAsync().Wait();
    var log = new NoLog();
    var view = new TileView(runtime, () => false, new TileCommands(@"C:\none\config.json", @"C:\none", _ => { }, log), log) { Height = 40 };
    var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), Padding = new Thickness(TaskbarGap), Child = view };
    Theme.ApplyTo(frame.Resources, light: false);
    frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    frame.Arrange(new Rect(frame.DesiredSize));
    frame.UpdateLayout(); // the chart sizes itself to the laid-out tile
    var bmp = new RenderTargetBitmap((int)Math.Round(frame.ActualWidth * Scale), (int)Math.Round(frame.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
    bmp.Render(frame);
    view.Detach();
    return bmp;
}

static byte[] Png(BitmapSource bmp)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

static void Save(BitmapSource bmp, string path) => File.WriteAllBytes(path, Png(bmp));

static double[] SizeWidths() => [48, 80, 110, 160, 240, 360, 520];

static string FindRepo()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "TaskbarTiles.sln"))) return dir.FullName;
    throw new InvalidOperationException("Run this from inside the repository.");
}

sealed class Fixed(TileState state) : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct) { ctx.Update(state); return Task.CompletedTask; }
    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class Capture(string id, JsonElement? settings = null, string? folder = null, bool echo = false) : ITileContext
{
    public volatile TileState? Last;
    public double Width { get; init; }
    public volatile string? Error;
    public string TileId => id;
    public JsonElement Settings { get; } = settings ?? JsonDocument.Parse("{}").RootElement.Clone();
    public string ConfigDirectory => Path.GetTempPath();
    public string TileDirectory => folder ?? ConfigDirectory;
    public void Update(TileState state) => Last = state;
    public void ReportError(string message, string? details = null) => Error = details is null ? message : $"{message}\n{details}";
    public event EventHandler? Resumed { add { } remove { } }
    public ITileLog Log => _log ??= echo ? new ConsoleLog(this) : new NoLog();
    ITileLog? _log;
    public int Warnings;
}

sealed class ConsoleLog(Capture owner) : ITileLog
{
    public void Info(string m) { }
    public void Warn(string m) { Interlocked.Increment(ref owner.Warnings); Console.WriteLine($"Warning: {m}"); }
    public void Error(string m, Exception? e = null) { Interlocked.Increment(ref owner.Warnings); Console.WriteLine($"Error: {m} {e?.Message}"); }
}

sealed class Inline : IUiDispatcher { public void Post(Action action) => action(); }
sealed class NoLog : ITileLog { public void Info(string m) { } public void Warn(string m) { } public void Error(string m, Exception? e = null) { } }
