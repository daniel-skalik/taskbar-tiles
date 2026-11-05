namespace TaskbarTiles.Contracts;

/// <summary>Drives one tile. The host creates one instance per tile in config.json, and a fresh one when it restarts the tile.</summary>
public interface ITileConnector : IAsyncDisposable
{
    /// <summary>Called once per instance. Keep the context and call ctx.Update whenever the tile should change.</summary>
    Task StartAsync(ITileContext ctx, CancellationToken ct);

    /// <summary>A button or popup action id, or one of <see cref="TileActions"/> ("click", "refresh").</summary>
    Task OnActionAsync(string actionId, CancellationToken ct);

    /// <summary>
    /// Text typed into the popup's input box (<see cref="FlyoutState.Input"/>), with that box's action id. The default
    /// drops the text and runs the action as a plain action, so connectors without input boxes need nothing.
    /// </summary>
    Task OnInputAsync(string actionId, string text, CancellationToken ct) => OnActionAsync(actionId, ct);

    /// <summary>Content of the popup shown when the tile is clicked; null means no popup.</summary>
    Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct);
}
