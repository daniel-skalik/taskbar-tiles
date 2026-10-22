using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>What one connector instance sees. Bound to that instance's generation, so a replaced connector's calls are dropped.</summary>
sealed class TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public string ConfigDirectory => configDirectory;
    public ITileLog Log => log;
    public event EventHandler? Resumed;

    public void Update(TileState state) => runtime.Enqueue(generation, state);

    public void ReportError(string message, string? details = null) => runtime.Report(generation, message, details);

    /// <summary>Each handler runs in its own try/catch so one throwing plugin handler cannot skip the others.</summary>
    internal void RaiseResumed(Action<Exception> onError)
    {
        if (Resumed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { onError(ex); }
        }
    }
}
