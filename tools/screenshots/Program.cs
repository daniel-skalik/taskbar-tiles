// Renders every bundled tile with made-up sample data, the way the taskbar draws it, to <out>\tile-<kind>.png
// (default docs\images). Nothing reads this machine: no media session, clipboard, devices or Claude account.
//   dotnet run --project tools/screenshots [-- <out folder>]
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
var repo = FindRepo();
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repo, "docs", "images"));
Directory.CreateDirectory(outDir);
var claudeLogo = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(repo, "tiles", "claude-usage", "claude.png")));

// The scenes animate: run each connector for a moment and keep its last frame.
var noon = new DateTime(2026, 6, 21, 17, 30, 0);
// Seconds each runs for: long enough for a grown snake and a race under way.
var scenes = new (string Kind, ITileConnector Connector, int Seconds)[]
{
    ("aquarium", new AquariumConnector(new Random(7), () => noon), 4),
    ("snake", new SnakeConnector(new Random(7), () => noon), 20),
    ("racers", new RacersConnector(new Random(7), () => noon), 14),
    ("cozy-farm", new FarmConnector(new Random(7), () => noon, () => (50.08, 14.42)), 4),
};
var frames = new Dictionary<string, TileState>();
await Task.WhenAll(scenes.Select(async scene =>
{
    var ctx = new Capture(scene.Kind);
    await scene.Connector.StartAsync(ctx, CancellationToken.None);
    await Task.Delay(TimeSpan.FromSeconds(scene.Seconds));
    await scene.Connector.DisposeAsync();
    lock (frames) frames[scene.Kind] = ctx.Last ?? throw new InvalidOperationException($"{scene.Kind} drew nothing");
}));

var thread = new Thread(() =>
{
    var app = new Application();
    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TaskbarTiles;component/UI/Styles.xaml") });

    var tiles = new List<(string Kind, double Width, TileState State)>
    {
        ("media", 268, Media()),
        ("claude-usage", 220, Script($$"""{ "title": "5h 34% · resets 16:40", "subtitle": "Week 21% · resets Fri", "progress": 0.34, "icon": { "glyph": "clock", "color": "ok" }, "image": "{{claudeLogo}}" }""")),
        ("claude", 268, Script($$"""{ "title": "Claude", "subtitle": "1 blocked · 2 working · 1 idle", "icon": { "glyph": "warning", "color": "warn" }, "image": "{{claudeLogo}}" }""")),
        ("system", 200, SystemTile()),
        ("battery", 132, BatteryView.Tile(BatteryView.Sorted([
            new DeviceBattery("m", "MX Master 3S", DeviceType.Mouse, 65),
            new DeviceBattery("k", "MX Keys", DeviceType.Keyboard, 90),
            new DeviceBattery("h", "WH-1000XM5", DeviceType.Headphones, 40, Charging: true)]))),
        ("clipboard", 200, ClipboardView.Tile([
            new ClipEntry(2, new ClipItem(ClipKind.Text, "git switch -c feat/new-tile"), noon.AddMinutes(-2)),
            new ClipEntry(1, new ClipItem(ClipKind.Image, "Image 1280×720", "Image 1280×720"), noon.AddMinutes(-9))])),
        ("uptime", 132, Script("""{ "title": "17:30", "subtitle": "up 3d 4h", "icon": { "glyph": "clock", "color": "accent" } }""")),
        ("ping", 132, Script("""{ "title": "1.1.1.1", "subtitle": "12 ms", "icon": { "glyph": "check", "color": "ok" } }""")),
        ("aquarium", 200, frames["aquarium"]),
        ("cozy-farm", 200, frames["cozy-farm"]),
        ("snake", 200, frames["snake"]),
        ("racers", 200, frames["racers"]),
    };
    foreach (var (kind, width, state) in tiles) Save(Render(width, state), Path.Combine(outDir, $"tile-{kind}.png"));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"Wrote 12 tiles to {outDir}");

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

sealed class Capture(string id) : ITileContext
{
    public volatile TileState? Last;
    public string TileId => id;
    public JsonElement Settings { get; } = JsonDocument.Parse("{}").RootElement.Clone();
    public string ConfigDirectory => Path.GetTempPath();
    public void Update(TileState state) => Last = state;
    public void ReportError(string message, string? details = null) => throw new InvalidOperationException($"{id}: {message}");
    public event EventHandler? Resumed { add { } remove { } }
    public ITileLog Log { get; } = new NoLog();
}

sealed class Inline : IUiDispatcher { public void Post(Action action) => action(); }
sealed class NoLog : ITileLog { public void Info(string m) { } public void Warn(string m) { } public void Error(string m, Exception? e = null) { } }
