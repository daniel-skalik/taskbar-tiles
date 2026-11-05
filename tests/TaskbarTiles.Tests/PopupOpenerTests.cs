using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;
using Xunit;

namespace TaskbarTiles.Tests;

public class PopupOpenerTests
{
    static readonly FlyoutState Last = new("Last", null, [], []);
    static readonly FlyoutState Fresh = new("Fresh", null, [], []);

    sealed class Recorder
    {
        public readonly List<string> Events = [];
        public Action<FlyoutState> Show(FlyoutState s)
        {
            Events.Add($"show {s.Title}");
            return f => Events.Add($"refresh {f.Title}");
        }
        public Task NoPopup() { Events.Add("click"); return Task.CompletedTask; }
        public void Close() => Events.Add("close");
    }

    static Task Open(Recorder r, Task<FlyoutResult> fresh, FlyoutState? last) =>
        PopupOpener.OpenAsync(fresh, last, TimeSpan.FromMilliseconds(30), r.Show, r.NoPopup, r.Close);

    static async Task<FlyoutResult> Slow(FlyoutResult result)
    {
        await Task.Delay(300);
        return result;
    }

    [Fact]
    public async Task A_fast_popup_shows_fresh_even_with_a_last_one()
    {
        var r = new Recorder();
        await Open(r, Task.FromResult(FlyoutResult.Of(Fresh)), Last);
        Assert.Equal(["show Fresh"], r.Events);
    }

    [Fact]
    public async Task A_slow_popup_shows_the_last_one_then_refreshes_it()
    {
        var r = new Recorder();
        await Open(r, Slow(FlyoutResult.Of(Fresh)), Last);
        Assert.Equal(["show Last", "refresh Fresh"], r.Events);
    }

    [Fact]
    public async Task A_slow_popup_without_a_last_one_waits()
    {
        var r = new Recorder();
        await Open(r, Slow(FlyoutResult.Of(Fresh)), null);
        Assert.Equal(["show Fresh"], r.Events);
    }

    [Fact]
    public async Task A_slow_answer_of_no_popup_closes_the_last_one()
    {
        var r = new Recorder();
        await Open(r, Slow(FlyoutResult.NoPopup), Last);
        Assert.Equal(["show Last", "close"], r.Events);
    }

    [Fact]
    public async Task No_popup_runs_the_click()
    {
        var r = new Recorder();
        await Open(r, Task.FromResult(FlyoutResult.NoPopup), null);
        Assert.Equal(["click"], r.Events);
    }
}
