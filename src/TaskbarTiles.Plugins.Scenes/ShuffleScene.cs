namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Shows each of the other scenes in turn, switching every 10 minutes to a random one that is not the current one.
/// Scenes are kept once made, so the snake's best score survives a trip to the aquarium.
/// </summary>
public sealed class ShuffleScene : IScene
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(10);

    readonly Func<IScene>[] _factories;
    readonly IScene?[] _made;
    readonly TimeSpan _period;
    int _index = -1;
    DateTime _since;

    public ShuffleScene(IReadOnlyList<Func<IScene>> factories, TimeSpan? period = null)
    {
        if (factories.Count < 2) throw new ArgumentException("Shuffle needs at least two scenes.", nameof(factories));
        _factories = factories.ToArray();
        _made = new IScene?[_factories.Length];
        _period = period ?? DefaultPeriod;
    }

    /// <summary>The scene showing now; null before the first step.</summary>
    public IScene? Current => _index < 0 ? null : _made[_index];

    public string Name => Current?.Name ?? "Shuffle";
    public string Status => Current?.Status ?? "";

    public void Step(DateTime now, Random rng)
    {
        // A clock that jumps back (time zone change) restarts the period rather than freezing the scene.
        if (_index < 0 || now - _since >= _period || now < _since)
        {
            int next = rng.Next(_index < 0 ? _factories.Length : _factories.Length - 1);
            if (_index >= 0 && next >= _index) next++; // skip the current one, so a scene never follows itself
            _index = next;
            _made[next] ??= _factories[next]();
            _since = now;
        }
        _made[_index]!.Step(now, rng);
    }

    public void Render(int[] pixels)
    {
        if (Current is { } scene) scene.Render(pixels);
        else new PixelCanvas(pixels).Clear(PixelCanvas.Rgb(0x101820));
    }

    public void OnClick(DateTime now, Random rng) => Current?.OnClick(now, rng);
}
