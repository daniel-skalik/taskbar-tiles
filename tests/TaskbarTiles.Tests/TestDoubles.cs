using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Tests;

public sealed class CollectingLog : ITileLog
{
    public List<string> Messages { get; } = [];
    public void Info(string message) { lock (Messages) Messages.Add("INFO " + message); }
    public void Warn(string message) { lock (Messages) Messages.Add("WARN " + message); }
    public void Error(string message, Exception? ex = null) { lock (Messages) Messages.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message)); }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
