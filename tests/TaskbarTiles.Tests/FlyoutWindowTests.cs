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

    [Fact]
    public void An_indented_row_moves_right_by_one_checkbox_per_level() => Sta.Run(() =>
    {
        Assert.Equal(0, FlyoutWindow.Row(new FlyoutRow("a"), first: true, _ => { }).Margin.Left);
        Assert.Equal(48, FlyoutWindow.Row(new FlyoutRow("a", Action: "x", Check: false, Indent: 2), first: false, _ => { }).Margin.Left);
    });
}
