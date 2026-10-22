namespace TaskbarTiles.Contracts;

/// <summary>Drives one tile. The host creates one instance per tile in config.json.</summary>
public interface ITileConnector : IAsyncDisposable
{
    /// <summary>Called once. Keep the context and call ctx.Update whenever the tile should change.</summary>
    Task StartAsync(ITileContext ctx, CancellationToken ct);

    /// <summary>"click" for a plain click when no flyout is offered, otherwise a button or flyout action id.</summary>
    Task OnActionAsync(string actionId, CancellationToken ct);

    /// <summary>Content of the popup shown when the tile is clicked; null means no popup.</summary>
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);
}
