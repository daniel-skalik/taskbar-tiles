using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Stands in for a connector id no loaded plugin provides, so the rest of the layout keeps working.</summary>
public sealed class UnknownConnector(string connectorId) : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.Update(new TileState
        {
            Title = "Unknown connector",
            Subtitle = connectorId,
            Icon = new TileIcon("warning", TileColor.Warn),
            Tooltip = $"No plugin provides the connector '{connectorId}'. Check the plugins folder and config.json.",
        });
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
