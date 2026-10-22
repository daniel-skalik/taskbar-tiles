using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public class ContractsTests
{
    [Fact]
    public void TileState_defaults_to_no_buttons_and_not_urgent()
    {
        var state = new TileState { Title = "x" };
        Assert.Empty(state.Buttons);
        Assert.False(state.Urgent);
        Assert.Null(state.Progress);
    }

    [Fact]
    public void Attribute_width_defaults_to_132_and_can_be_set()
    {
        Assert.Equal(132, new TileConnectorAttribute("a").Width);
        Assert.Equal(268, new TileConnectorAttribute("media") { Width = 268 }.Width);
    }

    [Fact]
    public void TileButton_is_enabled_by_default()
    {
        Assert.True(new TileButton("next", "next").Enabled);
    }

    [Fact]
    public void Host_action_ids_are_stable()
    {
        Assert.Equal("click", TileActions.Click);
        Assert.Equal("refresh", TileActions.Refresh);
    }
}
