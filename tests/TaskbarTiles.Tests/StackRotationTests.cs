using TaskbarTiles.Host.Layout;

namespace TaskbarTiles.Tests;

public class StackRotationTests
{
    [Fact]
    public void Ticks_advance_and_wrap()
    {
        var r = new StackRotation(3);

        Assert.True(r.Tick());
        Assert.Equal(1, r.Index);
        r.Tick();
        Assert.True(r.Tick());
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void A_single_member_never_moves()
    {
        var r = new StackRotation(1);
        Assert.False(r.Tick());
        Assert.False(r.Step(1));
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Zero_members_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackRotation(0));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Paused_hovered_or_suspended_stacks_do_not_rotate(bool paused, bool hovered, bool suspended)
    {
        var r = new StackRotation(3) { Paused = paused, Hovered = hovered, Suspended = suspended };

        Assert.False(r.Tick());
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Wheel_cycles_both_ways_even_when_paused()
    {
        var r = new StackRotation(3) { Paused = true };

        Assert.True(r.Step(-1));
        Assert.Equal(2, r.Index);
        r.Step(+1);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void An_urgent_member_takes_over_and_holds_the_stack()
    {
        var r = new StackRotation(3);

        Assert.True(r.SetUrgent(2, true));
        Assert.Equal(2, r.Index);
        Assert.Equal(2, r.TakenOverBy);
        Assert.False(r.Tick());
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void After_the_wheel_moved_away_a_tick_returns_to_the_urgent_member()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.Step(1);
        Assert.Equal(2, r.Index);
        Assert.True(r.Tick());
        Assert.Equal(1, r.Index);
    }

    [Fact]
    public void Clicking_the_urgent_member_releases_the_stack()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.Acknowledge();

        Assert.Null(r.TakenOverBy);
        Assert.True(r.Tick());
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void Clicking_while_another_member_is_shown_does_not_release()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);
        r.Step(1);

        Assert.False(r.Acknowledge());
        Assert.Equal(1, r.TakenOverBy);
    }

    [Fact]
    public void A_member_that_stays_urgent_does_not_take_over_again_after_the_click()
    {
        var r = new StackRotation(2);
        r.SetUrgent(1, true);
        r.Acknowledge();
        r.Tick();

        Assert.False(r.SetUrgent(1, true)); // same state reported again
        Assert.Null(r.TakenOverBy);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Becoming_urgent_anew_takes_over_again()
    {
        var r = new StackRotation(2);
        r.SetUrgent(1, true);
        r.Acknowledge();
        r.Tick();

        r.SetUrgent(1, false);
        Assert.True(r.SetUrgent(1, true));
        Assert.Equal(1, r.TakenOverBy);
    }

    [Fact]
    public void A_second_urgent_member_waits_its_turn()
    {
        var r = new StackRotation(3);
        r.SetUrgent(0, true);
        r.SetUrgent(2, true);
        Assert.Equal(0, r.TakenOverBy);

        Assert.True(r.Acknowledge());

        Assert.Equal(2, r.TakenOverBy);
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void An_urgent_member_that_calms_down_releases_the_stack_by_itself()
    {
        var r = new StackRotation(3);
        r.SetUrgent(1, true);

        r.SetUrgent(1, false);

        Assert.Null(r.TakenOverBy);
        Assert.True(r.Tick());
    }

    [Fact]
    public void Takeover_waits_while_suspended_but_is_remembered()
    {
        var r = new StackRotation(3) { Suspended = true };
        r.SetUrgent(2, true);
        r.Step(-2); // to 0 in edit mode

        Assert.False(r.Tick());
        r.Suspended = false;
        Assert.True(r.Tick());
        Assert.Equal(2, r.Index);
    }
}
