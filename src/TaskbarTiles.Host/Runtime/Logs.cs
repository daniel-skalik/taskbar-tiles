using System.Collections.Concurrent;
using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Appends timestamped lines to logs\{name}.log. Thread-safe.</summary>
public sealed class FileLog : ITileLog
{
    readonly string _path;
    readonly object _gate = new();

    static readonly ConcurrentDictionary<string, FileLog> Cache = new(StringComparer.OrdinalIgnoreCase);

    FileLog(string path) => _path = path;

    /// <summary>One instance per file, so concurrent writers share a lock. Names are sanitized to a plain file name.</summary>
    public static FileLog For(string logsDir, string name)
    {
        var path = Path.GetFullPath(Path.Combine(logsDir, Sanitize(name) + ".log"));
        return Cache.GetOrAdd(path, p =>
        {
            Directory.CreateDirectory(logsDir);
            return new FileLog(p);
        });
    }

    internal static string Sanitize(string name)
    {
        var s = name.Replace("..", "_").Replace('/', '_').Replace('\\', '_');
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "_" : s;
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
