using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// A little self-running pixel-art animation across the whole tile; each scene is its own tile kind with its own
/// connector below. A click pokes the scene: feeds the fish, restarts the snake, starts a new race, or makes the farmer
/// wave. Subclasses only say which scene to run.
/// </summary>
public abstract class SceneConnector : ITileConnector
{
    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);

    readonly Random _rng;
    readonly Func<DateTime> _clock;
    readonly object _gate = new(); // OnClick arrives on another thread than the frame loop
    readonly CancellationTokenSource _stop = new();
    readonly CancellationToken _stopping;
    volatile bool _disposed;
    ITileContext? _ctx;
    IScene? _scene;
    Task? _loop;
    string? _lastName, _lastStatus, _tooltip;

    protected SceneConnector() : this(new Random(), () => DateTime.Now) { }

    protected SceneConnector(Random rng, Func<DateTime> clock)
    {
        _rng = rng;
        _clock = clock;
        _stopping = _stop.Token; // kept so a StartAsync that races DisposeAsync never touches the disposed source
    }

    /// <summary>The running scene, for tests.</summary>
    public IScene? Scene => _scene;

    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        lock (_gate)
        {
            _scene = CreateScene(ctx, _rng);
            Frame();
        }
        if (!_disposed) _loop = LoopAsync(_stopping);
        return Task.CompletedTask;
    }

    /// <summary>The scene this tile shows, made once per start (the host restarts the connector when settings change).</summary>
    protected abstract IScene CreateScene(ITileContext ctx, Random rng);

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FrameInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _scene!.Step(_clock(), _rng);
                    Frame();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A scene bug should not freeze the tile silently; the log says what happened.
            _ctx?.Log.Error("The scene stopped", ex);
            _ctx?.ReportError("The scene stopped", ex.Message);
        }
    }

    /// <summary>Renders into a fresh array each time, because the host may still be drawing the previous frame.</summary>
    void Frame()
    {
        var scene = _scene!;
        var pixels = PixelCanvas.NewFrame();
        scene.Render(pixels);
        var status = scene.Status;
        if (!ReferenceEquals(scene.Name, _lastName) || !ReferenceEquals(status, _lastStatus))
        {
            _lastName = scene.Name;
            _lastStatus = status;
            _tooltip = string.IsNullOrEmpty(status) ? scene.Name : $"{scene.Name} · {status}";
        }
        _ctx?.Update(new TileState { Canvas = new TileCanvas(PixelCanvas.Width, PixelCanvas.Height, pixels), Tooltip = _tooltip });
    }

    public Task OnActionAsync(string actionId, CancellationToken ct)
    {
        if (actionId != TileActions.Click) return Task.CompletedTask;
        lock (_gate)
        {
            if (_disposed || _scene is null) return Task.CompletedTask;
            _scene.OnClick(_clock(), _rng);
            Frame(); // show the reaction right away rather than on the next tick
        }
        return Task.CompletedTask;
    }

    /// <summary>No popup, so a click on the tile arrives as <see cref="TileActions.Click"/>.</summary>
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        _stop.Dispose();
    }
}

[TileConnector("aquarium", Width = 200)]
public sealed class AquariumConnector : SceneConnector
{
    public AquariumConnector() { }
    public AquariumConnector(Random rng, Func<DateTime> clock) : base(rng, clock) { }
    protected override IScene CreateScene(ITileContext ctx, Random rng) => new AquariumScene(rng);
}

[TileConnector("snake", Width = 200)]
public sealed class SnakeConnector : SceneConnector
{
    public SnakeConnector() { }
    public SnakeConnector(Random rng, Func<DateTime> clock) : base(rng, clock) { }
    protected override IScene CreateScene(ITileContext ctx, Random rng) => new SnakeScene(rng);
}

[TileConnector("racers", Width = 200)]
public sealed class RacersConnector : SceneConnector
{
    public RacersConnector() { }
    public RacersConnector(Random rng, Func<DateTime> clock) : base(rng, clock) { }
    protected override IScene CreateScene(ITileContext ctx, Random rng) => new RacersScene(rng);
}

/// <summary>Settings: "latitude", "longitude", "workStart", "workEnd"; see <see cref="FarmOptions"/>.</summary>
[TileConnector("cozy-farm", Width = 200)]
public sealed class FarmConnector : SceneConnector
{
    readonly Func<(double, double)?>? _region;

    public FarmConnector() { }
    public FarmConnector(Random rng, Func<DateTime> clock, Func<(double, double)?>? region = null) : base(rng, clock) => _region = region;
    protected override IScene CreateScene(ITileContext ctx, Random rng) => new FarmScene(rng, FarmOptions.FromSettings(ctx.Settings, ctx.Log, _region));
}
