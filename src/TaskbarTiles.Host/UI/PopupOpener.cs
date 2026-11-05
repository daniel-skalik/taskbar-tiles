using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// Opens a tile's popup without making the user wait for slow connectors. A popup that answers within
/// <see cref="FastEnough"/> shows as it is. A slower one (a script tile starts a process on every open) shows the tile's
/// last popup at once, then the fresh one in place when it arrives; with no last popup the click waits as before.
/// </summary>
public static class PopupOpener
{
    /// <summary>How long a click waits for the fresh popup before showing the last one instead.</summary>
    public static readonly TimeSpan FastEnough = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Runs the open: <paramref name="show"/> shows a popup and returns how to refresh it in place;
    /// <paramref name="noPopup"/> runs when the tile has no popup (its click hook); <paramref name="close"/> closes a popup
    /// that was shown from the last one when the fresh answer is no popup at all.
    /// </summary>
    public static async Task OpenAsync(Task<FlyoutResult> fresh, FlyoutState? last, TimeSpan fastEnough,
        Func<FlyoutState, Action<FlyoutState>> show, Func<Task> noPopup, Action close)
    {
        if (last is not null && await Task.WhenAny(fresh, Task.Delay(fastEnough)).ConfigureAwait(true) != fresh)
        {
            var refresh = show(last);
            var result = await fresh.ConfigureAwait(true);
            if (result.Outcome == FlyoutOutcome.Popup && result.Flyout is { } popup) refresh(popup);
            else if (result.Outcome == FlyoutOutcome.NoPopup) close();
            // Unavailable (not responding, stopped): keep the last popup; the tile shows its own state.
            return;
        }

        var answer = await fresh.ConfigureAwait(true);
        switch (answer.Outcome)
        {
            case FlyoutOutcome.NoPopup:
                await noPopup().ConfigureAwait(true);
                break;
            case FlyoutOutcome.Popup:
                show(answer.Flyout!);
                break;
        }
    }
}
