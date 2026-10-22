using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Appends timestamped lines to logs\{name}.log. Thread-safe.</summary>
public sealed class FileLog : ITileLog
{
    readonly string _path;
    readonly object _gate = new();

    FileLog(string path) => _path = path;

    public static FileLog For(string logsDir, string name)
    {
        Directory.CreateDirectory(logsDir);
        return new FileLog(Path.Combine(logsDir, name + ".log"));
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never take the host down */ }
        }
    }
}

public sealed class NullTileLog : ITileLog
{
    public static NullTileLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
