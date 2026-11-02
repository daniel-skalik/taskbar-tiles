using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Little self-running pixel-art animations across the whole tile. Setting "scene": Aquarium, Snake, Racers, Cozy farm
/// or Shuffle (another scene every 10 minutes); default Cozy farm. A click pokes the scene: feeds the fish, restarts the
/// snake, starts a new race, or makes the farmer wave.
/// </summary>
[TileConnector("pixel-scene", Width = 200)]
public sealed class SceneConnector : ITileConnector
{
    public const string SceneKey = "scene";
    public const string Aquarium = "Aquarium", Snake = "Snake", Racers = "Racers", CozyFarm = "Cozy farm", Shuffle = "Shuffle";
    public static readonly IReadOnlyList<string> SceneNames = [Aquarium, Snake, Racers, CozyFarm, Shuffle];

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

    public SceneConnector() : this(new Random(), () => DateTime.Now) { }

    public SceneConnector(Random rng, Func<DateTime> clock)
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
        // The host restarts the connector when settings change, so the scene is read once here.
        lock (_gate)
        {
            _scene = Create(SceneName(ctx.Settings, ctx.Log), _rng);
            Frame();
        }
        if (!_disposed) _loop = LoopAsync(_stopping);
        return Task.CompletedTask;
    }

    /// <summary>"scene" from settings, matched ignoring case; missing or unknown is logged and Cozy farm used.</summary>
    public static string SceneName(JsonElement settings, ITileLog log)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(SceneKey, out var value)) return CozyFarm;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            foreach (var name in SceneNames)
                if (string.Equals(name, text?.Trim(), StringComparison.OrdinalIgnoreCase)) return name;
        }
        log.Warn($"\"scene\" must be one of {string.Join(", ", SceneNames)}; showing {CozyFarm}.");
        return CozyFarm;
    }

    public static IScene Create(string name, Random rng) => name switch
    {
        Aquarium => new AquariumScene(rng),
        Snake => new SnakeScene(rng),
        Racers => new RacersScene(rng),
        Shuffle => new ShuffleScene([() => new AquariumScene(rng), () => new SnakeScene(rng), () => new RacersScene(rng), () => new FarmScene(rng)]),
        _ => new FarmScene(rng),
    };

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
