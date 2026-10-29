namespace TaskbarTiles.Host.Taskbar;

/// <summary>Physical-pixel rectangle (Win32 coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>True for a pixel inside: the left and top edges belong to the rectangle, the right and bottom ones do not.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
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
    double DpiScale,
    PixelRect? RightZone = null)
{
    /// <summary>The zone a bar on <paramref name="side"/> uses; the right zone defaults to the free zone.</summary>
    public PixelRect ZoneFor(Config.BarSide side) => side == Config.BarSide.Right ? RightZone ?? FreeZone.Zone : FreeZone.Zone;

    /// <summary>The bar hugs the zone's right end (the tray) on the right side, or when the icons are left-aligned.</summary>
    public bool AnchorsRight(Config.BarSide side) => side == Config.BarSide.Right || !FreeZone.IconsCentered;
}
