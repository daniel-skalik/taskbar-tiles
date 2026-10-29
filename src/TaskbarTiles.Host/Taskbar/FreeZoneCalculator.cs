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

    /// <summary>
    /// Where the app icons end for the right zone: the edge UI Automation read, or else the icon area window's edge plus
    /// <paramref name="safetyPx"/>, because that window can end an icon or two before the last icon.
    /// </summary>
    public static int? IconsRight(int? read, TaskbarGeometry g, int safetyPx) =>
        read ?? (g.IconArea is { } area ? area.Right + safetyPx : null);

    /// <summary>
    /// The space between the app icons and the tray (or the taskbar edge minus a clock reserve on secondary taskbars),
    /// for displays whose tiles sit on the right. <paramref name="iconsRight"/> is where the icons end when known
    /// better than the icon area window says (UI Automation).
    /// </summary>
    public static PixelRect ComputeRight(TaskbarGeometry g, int marginPx, int clockReservePx, int? iconsRight = null)
    {
        var tb = g.Taskbar;
        var left = (iconsRight ?? g.IconArea?.Right ?? g.Start?.Right ?? tb.Left) + marginPx;
        var right = (g.Tray?.Left ?? tb.Right - clockReservePx) - marginPx;
        if (right < left) right = left;
        return new PixelRect(left, tb.Top, right, tb.Bottom);
    }
}
