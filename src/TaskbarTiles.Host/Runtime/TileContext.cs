using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>What one connector instance sees. Bound to that instance's generation, so a replaced connector's calls are dropped.</summary>
sealed class TileContext(TileRuntime runtime, int generation, JsonElement settings, ITileLog log, string configDirectory, string tileDirectory) : ITileContext
{
    public string TileId => runtime.TileId;
    public JsonElement Settings => settings;
    public string ConfigDirectory => configDirectory;
    public string TileDirectory => tileDirectory;
    public ITileLog Log => log;
    public event EventHandler? Resumed;
    public event EventHandler? Resized;
    public double Width => runtime.Width;

    public void Update(TileState state) => runtime.Enqueue(generation, state);

    public void ReportError(string message, string? details = null) => runtime.Report(generation, message, details);

    internal void RaiseResumed(Action<Exception> onError) => Raise(Resumed, onError);

    internal void RaiseResized(Action<Exception> onError) => Raise(Resized, onError);

    /// <summary>Each handler runs in its own try/catch so one throwing plugin handler cannot skip the others.</summary>
    void Raise(EventHandler? handlers, Action<Exception> onError)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { onError(ex); }
        }
    }
}
