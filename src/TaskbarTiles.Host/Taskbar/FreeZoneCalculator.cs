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
