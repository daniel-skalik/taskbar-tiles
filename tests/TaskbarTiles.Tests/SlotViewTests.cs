using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class SlotViewTests
{
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement.Clone();

    static TileRuntime Runtime(string id, double width = 132) =>
        new(id, "fake", width, () => new FakeConnector(), NoSettings, new ImmediateDispatcher(), new CollectingLog());

    static TileCommands Commands() => new(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog());

    [Fact]
    public void A_stack_shows_one_member_with_dots_and_takes_its_widest_width() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a", 132), Runtime("b", 268)], false, TimeSpan.FromSeconds(5));

        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        Assert.Equal(268, view.Width);
        Assert.Equal(0, view.CurrentMember);
        Assert.Same(slot.Members[0], view.Current.Runtime);
        Assert.Equal(Visibility.Visible, view.Current.Visibility);
        view.Detach();
    });

    [Fact]
    public void Wheel_down_shows_the_next_member() => Sta.Run(() =>
    {
        var slot = new BarSlot(0, [Runtime("a"), Runtime("b")], false, TimeSpan.FromSeconds(5));
        var view = new SlotView(slot, () => false, Commands(), new CollectingLog());

        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.MouseWheelEvent });

        Assert.Equal(1, view.CurrentMember);
        Assert.Same(slot.Members[1], view.Current.Runtime);
        view.Detach();
    });

    [Fact]
    public void Compact_slots_are_40_dip_wide() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a", 268)], true, null), () => false, Commands(), new CollectingLog());

        view.Compact = true;

        Assert.Equal(LayoutEngine.CompactWidth, view.Width);
        Assert.True(view.Current.Compact);
        Assert.Null(view.Rotation);
        view.Detach();
    });

    [Fact]
    public void Edit_mode_shows_the_remove_button_and_suspends_rotation() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a"), Runtime("b")], false, TimeSpan.FromSeconds(5)), () => false, Commands(), new CollectingLog());
        var removed = 0;
        view.RemoveRequested += (_, _) => removed++;

        Assert.Equal(Visibility.Collapsed, view.RemoveButton.Visibility);
        view.Editing = true;

        Assert.Equal(Visibility.Visible, view.RemoveButton.Visibility);
        Assert.True(view.Rotation!.Suspended);
        view.RemoveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, removed);

        view.Editing = false;
        Assert.False(view.Rotation.Suspended);
        Assert.Equal(Visibility.Collapsed, view.RemoveButton.Visibility);
        view.Detach();
    });

    [Fact]
    public void Drop_highlight_only_applies_in_edit_mode() => Sta.Run(() =>
    {
        var view = new SlotView(new BarSlot(0, [Runtime("a")], false, null), () => false, Commands(), new CollectingLog());

        view.DropHighlight = true;
        Assert.False(view.DropHighlight);

        view.Editing = true;
        view.DropHighlight = true;
        Assert.True(view.DropHighlight);

        view.Editing = false;
        Assert.False(view.DropHighlight);
        view.Detach();
    });

    static async Task<(TileRuntime Runtime, FakeConnector Connector)> StartedRuntime(string id)
    {
        var connector = new FakeConnector();
        var runtime = new TileRuntime(id, "fake", 132, () => connector, NoSettings, new ImmediateDispatcher(), new CollectingLog());
        await runtime.StartAsync();
        return (runtime, connector);
    }

    static SlotView StackOf(params TileRuntime[] members) =>
        new(new BarSlot(0, members, false, TimeSpan.FromHours(1)), () => false, Commands(), new CollectingLog());

    [Fact]
    public void An_urgent_member_takes_over_the_stack() => Sta.Run(() =>
    {
        var (a, _) = StartedRuntime("a").GetAwaiter().GetResult();
        var (b, bc) = StartedRuntime("b").GetAwaiter().GetResult();
        var view = StackOf(a, b);
        Assert.Equal(0, view.CurrentMember);

        bc.Context!.Update(new TileState { Title = "!", Urgent = true });

        Assert.Equal(1, view.CurrentMember);
        Assert.Same(b, view.Current.Runtime);
        view.Detach();
    });

    [Fact]
    public void After_Detach_state_changes_do_not_move_the_stack() => Sta.Run(() =>
    {
        var (a, _) = StartedRuntime("a").GetAwaiter().GetResult();
        var (b, bc) = StartedRuntime("b").GetAwaiter().GetResult();
        var view = StackOf(a, b);
        view.Detach();

        bc.Context!.Update(new TileState { Title = "!", Urgent = true });

        Assert.Equal(0, view.CurrentMember);
        Assert.Same(a, view.Current.Runtime);
    });

    [Fact]
    public void Only_the_shown_member_is_visible() => Sta.Run(() =>
    {
        var view = StackOf(Runtime("a"), Runtime("b"), Runtime("c"));

        var visibility = view.Children.OfType<TileView>().Select(t => t.Visibility).ToArray();

        Assert.Equal([Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed], visibility);
        view.Detach();
    });

    [Fact]
    public void Member_popups_anchor_to_the_slot_unless_an_anchor_is_set() => Sta.Run(() =>
    {
        var view = StackOf(Runtime("a"), Runtime("b"));
        Assert.All(view.Children.OfType<TileView>(), t => Assert.Same(view, t.FlyoutAnchor));

        var other = new System.Windows.Controls.Grid();
        view.FlyoutAnchor = other;

        Assert.All(view.Children.OfType<TileView>(), t => Assert.Same(other, t.FlyoutAnchor));
        view.Detach();
    });

    [Fact]
    public void A_constructor_that_throws_leaves_no_subscription_behind() => Sta.Run(() =>
    {
        var good = Runtime("a");
        var bad = Runtime("b", width: -1); // a negative Width makes the second TileView throw

        Assert.ThrowsAny<Exception>(() => StackOf(good, bad));

        Assert.False(HasSubscribers(good));
        Assert.False(HasSubscribers(bad));
    });

    static bool HasSubscribers(TileRuntime runtime) =>
        typeof(TileRuntime).GetField(nameof(TileRuntime.StateChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(runtime) is Delegate;
}

/// <summary>Runs WPF code on a fresh STA thread and rethrows what it threw.</summary>
public static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
