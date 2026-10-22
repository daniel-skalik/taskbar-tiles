using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

sealed class TileContext(TileRuntime runtime, JsonElement settings, ITileLog log) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public ITileLog Log => log;
    public event EventHandler? Resumed;

    public void Update(TileState state) => runtime.Enqueue(state);

    internal void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}
