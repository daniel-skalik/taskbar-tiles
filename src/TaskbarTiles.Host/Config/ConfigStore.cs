using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Config;

/// <summary>Owns config.json: creates it on first run, parses it, and reloads it when it changes on disk.</summary>
public sealed class ConfigStore(string path, ITileLog log) : IDisposable
{
    public const string DefaultJson = """
    {
      // Each tile is driven by a connector (a plugin). Place tiles on displays under "displays".
      "tiles": {
        "spotify": { "connector": "media", "settings": { "app": "Spotify" } }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true } ] },
        "*":       { "slots": [] }
      }
    }
    """;

    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    readonly object _reloadGate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;

    public HostConfig Current { get; private set; } = HostConfig.Empty;
    public ConfigError? LastError { get; private set; }

    /// <summary>Raised on a thread-pool thread after a successful reload.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on a thread-pool thread when an edited file fails to parse. Current stays unchanged.</summary>
    public event EventHandler<ConfigError>? ReloadFailed;

    public void Load()
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultJson);
                log.Info($"Created default config at {path}");
            }

            text = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Current = HostConfig.Empty;
            LastError = new ConfigError("Could not read config.json: " + ex.Message);
            log.Error(LastError.Message, ex);
            return;
        }

        var result = ConfigParser.Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
        }
        else
        {
            LastError = result.Error;
            log.Error($"config.json is invalid: {result.Error}");
        }
    }

    public void StartWatching()
    {
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    // Runs on a thread-pool timer. Nothing may escape: an unhandled exception would kill the host.
    void Reload()
    {
        lock (_reloadGate)
        {
            try
            {
                string text;
                try { text = ReadWithRetry(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Could not read config.json: {ex.Message}");
                    return;
                }

                var result = ConfigParser.Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
                    LastError = null;
                    log.Info("Reloaded config.json");
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    LastError = result.Error;
                    log.Warn($"config.json edit rejected: {result.Error}");
                    ReloadFailed?.Invoke(this, result.Error!);
                }
            }
            catch (Exception ex)
            {
                log.Error("Config reload failed", ex);
            }
        }
    }

    // Editors often hold the file briefly while saving.
    string ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
