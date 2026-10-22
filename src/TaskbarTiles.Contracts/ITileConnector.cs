namespace TaskbarTiles.Contracts;

/// <summary>Drives one tile. The host creates one instance per tile in config.json, and a fresh one when it restarts the tile.</summary>
public interface ITileConnector : IAsyncDisposable
{
    /// <summary>Called once per instance. Keep the context and call ctx.Update whenever the tile should change.</summary>
    Task StartAsync(ITileContext ctx, CancellationToken ct);

    /// <summary>A button or popup action id, or one of <see cref="TileActions"/> ("click", "refresh").</summary>
    Task OnActionAsync(string actionId, CancellationToken ct);

    /// <summary>Content of the popup shown when the tile is clicked; null means no popup.</summary>
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);
}
