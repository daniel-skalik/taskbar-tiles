using System.Text.Json;
using System.Windows;
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
        view.Detach();
    });
}
