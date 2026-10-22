using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests.SamplePlugin;

[TileConnector("sample", Width = 150)]
public sealed class SampleConnector : ITileConnector
{
    public Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        ctx.Update(new TileState { Title = "hello from sample" });
        return Task.CompletedTask;
    }

    public Task OnActionAsync(string actionId, CancellationToken ct) => Task.CompletedTask;
    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Not a connector: no attribute. The loader must ignore it.
public sealed class NotAConnector { }
