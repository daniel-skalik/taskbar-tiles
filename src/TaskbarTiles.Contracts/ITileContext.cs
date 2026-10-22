using System.Text.Json;

namespace TaskbarTiles.Contracts;

public interface ITileContext
{
    string TileId { get; }

    /// <summary>The tile's "settings" object from config.json (an empty object if absent).</summary>
    JsonElement Settings { get; }

    /// <summary>Thread-safe. Only the latest state is rendered if several arrive quickly.</summary>
    void Update(TileState state);

    /// <summary>Raised after the system resumes from sleep.</summary>
    event EventHandler? Resumed;

    ITileLog Log { get; }
}

public interface ITileLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
