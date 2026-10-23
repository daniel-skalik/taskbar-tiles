using System.Windows.Controls;
using TaskbarTiles.Host.UI;
using Xunit;

namespace TaskbarTiles.Tests;

public class FlyoutWindowTests
{
    [Fact]
    public void An_anchor_in_no_window_has_no_source() => Sta.Run(() =>
        Assert.Null(FlyoutWindow.SourceOf(new Button())));
}
