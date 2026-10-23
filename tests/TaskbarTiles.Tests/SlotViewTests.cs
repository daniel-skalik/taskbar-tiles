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
