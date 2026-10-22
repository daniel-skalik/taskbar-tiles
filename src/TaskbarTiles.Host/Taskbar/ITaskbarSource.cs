namespace TaskbarTiles.Host.Taskbar;

public interface ITaskbarSource : IDisposable
{
    /// <summary>One entry per taskbar (main and secondary). Read on the UI thread.</summary>
    IReadOnlyList<TaskbarInfo> Current { get; }

    /// <summary>Raised on the UI thread after each coalesced batch of taskbar events, even if nothing moved.</summary>
    event EventHandler? Changed;

    void Start();
}
