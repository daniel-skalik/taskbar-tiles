using System.Windows.Controls;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.UI;
using Xunit;

namespace TaskbarTiles.Tests;

public class FlyoutWindowTests
{
    [Fact]
    public void An_anchor_in_no_window_has_no_source() => Sta.Run(() =>
        Assert.Null(FlyoutWindow.SourceOf(new Button())));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_popup_row_builds_with_or_without_an_action(bool clickable, bool? check) => Sta.Run(() =>
    {
        var row = new FlyoutRow("Write spec", "2 min", Action: clickable ? "toggle:1:ab12cd34" : null, Check: check);

        Assert.NotNull(FlyoutWindow.Row(row, first: true, _ => { }));
    });
}
