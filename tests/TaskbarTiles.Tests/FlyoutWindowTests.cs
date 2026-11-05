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

    [Theory]
    [InlineData("ring")]
    [InlineData("half-ring")]
    [InlineData("bars-1")]
    [InlineData("bars-2")]
    [InlineData("bars-3")]
    [InlineData("dashes")]
    [InlineData("alert")]
    public void Row_shapes_have_drawings(string name) => Sta.Run(() =>
        Assert.NotNull(Theme.RowShape(name, TileColor.Warn)));

    [Fact]
    public void Other_glyph_names_are_no_row_shape() => Sta.Run(() =>
        Assert.Null(Theme.RowShape("check", TileColor.Ok)));

    [Fact]
    public void A_row_draws_its_start_and_end_icons() => Sta.Run(() =>
    {
        var row = new FlyoutRow("ENG-3 Fix login", Icon: new TileIcon("half-ring", TileColor.Warn), Action: "open:x",
            EndIcon: new TileIcon("bars-3", TileColor.Neutral));

        var grid = (System.Windows.Controls.Grid)((System.Windows.Controls.Border)FlyoutWindow.Row(row, first: true, _ => { }).Child).Child;

        Assert.Equal(3, grid.Children.Count); // start shape, text, end shape
        Assert.Contains(grid.Children.Cast<System.Windows.UIElement>(), c => System.Windows.Controls.Grid.GetColumn(c) == 3);
    });
}
