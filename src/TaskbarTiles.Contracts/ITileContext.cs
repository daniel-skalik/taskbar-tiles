using System.Text.Json;

namespace TaskbarTiles.Contracts;

public interface ITileContext
{
    string TileId { get; }

    /// <summary>The tile's "settings" object from config.json (an empty object if absent).</summary>
    JsonElement Settings { get; }

    /// <summary>
    /// The folder that holds config.json (normally %LOCALAPPDATA%\TaskbarTiles). Resolve relative paths from
    /// settings against it, so plugins never hard-code the data folder.
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>
    /// The tile's kind folder (normally %LOCALAPPDATA%\TaskbarTiles\tiles\&lt;kind&gt;), which holds its tile.json,
    /// scripts and DLLs. Resolve the tile's own files against it. Hosts that predate tile folders return ConfigDirectory.
    /// </summary>
    string TileDirectory => ConfigDirectory;

    /// <summary>Thread-safe. Only the latest state is rendered if several arrive quickly.</summary>
    void Update(TileState state);

    /// <summary>
    /// Thread-safe. Shows the error look (message as subtitle and tooltip) and puts the details, e.g. a script's
    /// stderr, in the tile's popup, without restarting the connector. The next Update clears it.
    /// Throw from a connector method only for fatal failures: the host then restarts the connector with backoff.
    /// </summary>
    void ReportError(string message, string? details = null);

    /// <summary>Raised after the system resumes from sleep, on a thread-pool thread.</summary>
    event EventHandler? Resumed;

    ITileLog Log { get; }
}

public interface ITileLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
