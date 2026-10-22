namespace TaskbarTiles.Contracts;

/// <summary>Action ids the host itself sends to <see cref="ITileConnector.OnActionAsync"/>, besides button and popup action ids.</summary>
public static class TileActions
{
    /// <summary>A plain click on a tile whose GetFlyoutAsync returned null.</summary>
    public const string Click = "click";

    /// <summary>"Refresh now" from the tile's context menu: fetch fresh data now. Connectors may ignore it.</summary>
    public const string Refresh = "refresh";

    /// <summary>Host-provided popup action: restart the tile's connector. Ids starting with "host:" are reserved for the host.</summary>
    public const string Restart = "host:restart";

    /// <summary>Host-provided popup action: open the tile's log.</summary>
    public const string ShowLog = "host:show-log";
}
