namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// One little animated world. The connector steps it about ten times a second and renders each step into a fresh
/// <see cref="PixelCanvas.Width"/> × <see cref="PixelCanvas.Height"/> frame. Not thread-safe: the connector serializes calls.
/// </summary>
public interface IScene
{
    /// <summary>Shown first in the tooltip, e.g. "Snake".</summary>
    string Name { get; }

    /// <summary>A short, fun line for the tooltip, e.g. "score 14 · best 31". Scenes rebuild it only when it changes.</summary>
    string Status { get; }

    /// <summary>Advances one frame. <paramref name="now"/> is local time, which scenes that follow the clock use.</summary>
    void Step(DateTime now, Random rng);

    /// <summary>Draws the current frame over every pixel of <paramref name="pixels"/>.</summary>
    void Render(int[] pixels);

    /// <summary>The tile was clicked.</summary>
    void OnClick(DateTime now, Random rng) { }
}
