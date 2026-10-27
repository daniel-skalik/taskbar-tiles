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
