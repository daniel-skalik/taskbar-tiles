using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Tests;

public class FreeZoneCalculatorTests
{
    const int Margin = 12, Clock = 180;
    static readonly PixelRect MainBar = new(0, 2088, 3840, 2160);

    [Fact]
    public void Centered_icons_free_zone_ends_before_start()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(1589, 2088, 1656, 2160), IconArea: new(1656, 2088, 2052, 2160), Tray: new(3408, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.True(z.IconsCentered);
        Assert.Equal(new PixelRect(12, 2088, 1577, 2160), z.Zone);
    }

    [Fact]
    public void Left_aligned_icons_free_zone_runs_from_icons_to_tray()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(0, 2088, 68, 2160), IconArea: new(68, 2088, 398, 2160), Tray: new(3456, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.False(z.IconsCentered);
        Assert.Equal(new PixelRect(410, 2088, 3444, 2160), z.Zone);
    }

    [Fact]
    public void Secondary_taskbar_without_tray_reserves_clock_space()
    {
        var bar = new PixelRect(3840, 2088, 7680, 2160);
        var g = new TaskbarGeometry(bar, Start: new(3840, 2088, 3907, 2160), IconArea: new(3907, 2088, 4039, 2160), Tray: null);

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.Equal(new PixelRect(4051, 2088, 7488, 2160), z.Zone);
    }

    [Fact]
    public void Missing_start_and_icons_is_treated_as_left_aligned_from_taskbar_edge()
    {
        var z = FreeZoneCalculator.Compute(new TaskbarGeometry(MainBar, null, null, null), Margin, Clock);

        Assert.False(z.IconsCentered);
        Assert.Equal(new PixelRect(12, 2088, 3648, 2160), z.Zone);
    }

    [Fact]
    public void Crowded_taskbar_collapses_to_zero_width()
    {
        var g = new TaskbarGeometry(MainBar, Start: new(0, 2088, 68, 2160), IconArea: new(68, 2088, 3450, 2160), Tray: new(3456, 2088, 3840, 2160));

        var z = FreeZoneCalculator.Compute(g, Margin, Clock);

        Assert.True(z.Zone.IsEmpty);
        Assert.Equal(z.Zone.Left, z.Zone.Right);
    }
}
