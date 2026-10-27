using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class DragGhostTests
{
    [Fact]
    public void Ghost_window_is_click_through_and_never_activates()
    {
        const long layered = 0x00080000;

        var style = DragGhost.ExStyle(layered);

        Assert.Equal(layered, style & layered);            // keeps what WPF set
        Assert.Equal(0x20L, style & 0x20L);                // WS_EX_TRANSPARENT: mouse input passes through
        Assert.Equal(0x08000000L, style & 0x08000000L);    // WS_EX_NOACTIVATE: never takes focus
        Assert.Equal(0x80L, style & 0x80L);                // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry
    }

    [Fact]
    public void Snapshot_renders_the_slot_at_the_display_scale() => Sta.Run(() =>
    {
        var runtime = new TileRuntime("a", "fake", 132, () => new FakeConnector(), JsonDocument.Parse("{}").RootElement.Clone(),
            new ImmediateDispatcher(), new CollectingLog());
        var commands = new TileCommands(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());
        var view = new SlotView(new BarSlot(0, [runtime], false, null), () => false, commands, new CollectingLog());
        view.Measure(new Size(132, 40));
        view.Arrange(new Rect(0, 0, 132, 40));

        var bitmap = DragGhost.Snapshot(view, 1.5);

        Assert.Equal(198, bitmap.PixelWidth);
        Assert.Equal(60, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 3] != 0); // not a blank render
        view.Detach();
    });

    [Fact]
    public void Snapshot_of_a_later_or_slid_slot_is_not_shifted() => Sta.Run(() =>
    {
        var commands = new TileCommands(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());
        SlotView Make(string id, int width) => new(new BarSlot(0, [new TileRuntime(id, "fake", width, () => new FakeConnector(),
            JsonDocument.Parse("{}").RootElement.Clone(), new ImmediateDispatcher(), new CollectingLog())], false, null),
            () => false, commands, new CollectingLog());
        var first = Make("a", 100);
        var second = Make("b", 300);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { first, second } };
        panel.Measure(new Size(400, 40));
        panel.Arrange(new Rect(0, 0, 400, 40));
        second.RenderTransform = new TranslateTransform(37, 0);

        var expected = FirstDrawnColumn(DragGhost.Snapshot(first, 1));
        var actual = FirstDrawnColumn(DragGhost.Snapshot(second, 1));

        Assert.True(expected >= 0);
        Assert.Equal(expected, actual); // same tile look, so the picture starts at the same column
        first.Detach();
        second.Detach();
    });

    static int FirstDrawnColumn(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        for (var x = 0; x < bitmap.PixelWidth; x++)
            for (var y = 0; y < bitmap.PixelHeight; y++)
                if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] != 0) return x;
        return -1;
    }
}
