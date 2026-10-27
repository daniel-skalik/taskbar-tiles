using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>
/// Stands in for a tile whose kind is missing or broken: shows the error look with what is wrong. A reported error (not a
/// throw), so it is not restarted with backoff; a fixed tile.json changes the kind's stamp and the host restarts the tile.
/// </summary>
public sealed class BrokenKindConnector(string message) : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.ReportError(message);
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
