namespace TaskbarTiles.Host.Taskbar;

public interface ITaskbarSource : IDisposable
{
    /// <summary>One entry per taskbar (main and secondary). Read on the UI thread.</summary>
    IReadOnlyList<TaskbarInfo> Current { get; }

    /// <summary>Raised on the UI thread after each coalesced batch of taskbar events, even if nothing moved.</summary>
    event EventHandler? Changed;

    /// <summary>Read where the app icons end exactly (for displays whose tiles sit on the right). Off by default.</summary>
    bool TrackIconEdges { get; set; }

    void Start();
}
