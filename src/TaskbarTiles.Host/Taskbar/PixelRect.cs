namespace TaskbarTiles.Host.Taskbar;

/// <summary>Physical-pixel rectangle (Win32 coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
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
    double DpiScale);
