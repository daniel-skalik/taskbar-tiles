using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Config;

/// <summary>Saves an edit of config.json. ConfigStore implements it; tests use a fake.</summary>
public interface IConfigWriter
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the file's JSON tree and saves it. Returns null on success, otherwise why
    /// nothing was written. Never throws.
    /// </summary>
    ConfigError? Save(Action<JsonObject> edit);
}

/// <summary>
/// Owns config.json: creates it on first run, parses it, reloads it when it changes on disk, and saves edits.
/// <paramref name="validate"/> runs after every successful parse (load, reload, save, recheck); a non-null result rejects
/// the config like a parse error. The app passes the kind check (<see cref="KindValidation"/>).
/// </summary>
public sealed class ConfigStore(string path, ITileLog log, Func<HostConfig, ConfigError?>? validate = null) : IConfigWriter, IDisposable
{
    public const string DefaultJson = """
    {
      // Each tile is an instance of a tile kind: a folder in tiles\ next to this file with its tile.json, scripts and DLLs.
      // "settings" here replaces the kind's settings key by key. Place tiles on displays under "displays".
      // Edit layout (right-click a tile) rewrites this file without comments; the previous file is kept as config.json.bak.
      "tiles": {
        "spotify": { "kind": "media" },
        "uptime":  { "kind": "uptime" }
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true }, "uptime" ] },
        "*":       { "slots": [] }
      }
    }
    """;

    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    readonly object _reloadGate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    string? _appliedText; // the file text Current was parsed from; reloading the same text is a no-op

    public HostConfig Current { get; private set; } = HostConfig.Empty;
    public ConfigError? LastError { get; private set; }

    /// <summary>The copy of the previous file that every save leaves behind.</summary>
    public string BackupPath => path + ".bak";

    /// <summary>Raised after a reload (on a thread-pool thread) or a save (on the saving thread) changed Current.</summary>
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

        var result = Parse(text);
        if (result.Success)
        {
            Current = result.Config!;
            LastError = null;
            _appliedText = text;
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

    /// <summary>
    /// Reads the file as it is on disk now, applies the edit and writes the result atomically (temp file, then replace),
    /// keeping the previous file as config.json.bak. Refuses to touch a file that does not parse, and refuses an edit
    /// whose result would not parse. The watcher then sees the text Current already came from and ignores it.
    /// </summary>
    public ConfigError? Save(Action<JsonObject> edit)
    {
        lock (_reloadGate)
        {
            try
            {
                var text = ReadWithRetry();
                var before = Parse(text);
                if (!before.Success)
                    return new ConfigError("config.json has an error, so nothing was saved: " + before.Error!.Message,
                        before.Error.Line, before.Error.Column);

                // JsonDocument (the parser) accepts duplicate keys; JsonNode throws ArgumentException on them,
                // either at parse time or as soon as the edit touches the object.
                JsonObject root;
                try
                {
                    root = ConfigJson.ParseRoot(text);
                    edit(root);
                }
                catch (ArgumentException ex)
                {
                    log.Warn($"config.json has a duplicate key: {ex.Message}");
                    return new ConfigError($"config.json has a duplicate key ({ex.Message}); remove it, then try again.");
                }
                var newText = ConfigJson.Serialize(root);
                var after = Parse(newText);
                if (!after.Success) return new ConfigError("The change was not saved: " + after.Error);

                WriteAtomically(newText);
                Current = after.Config!;
                LastError = null;
                _appliedText = newText;
                log.Info($"Saved config.json; the previous version is in {Path.GetFileName(BackupPath)}");
            }
            catch (Exception ex)
            {
                log.Error("Saving config.json failed", ex);
                return new ConfigError("Could not save config.json: " + ex.Message);
            }
        }

        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { log.Error("A config Changed handler failed", ex); }
        return null;
    }

    void WriteAtomically(string text)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Replace(temp, path, BackupPath, ignoreMetadataErrors: true);
        }
        catch
        {
            // File.Replace can fail after moving config.json to .bak but before moving .tmp into place;
            // then .tmp is the only copy of the new config, so it is kept.
            if (File.Exists(path))
                try { File.Delete(temp); } catch (Exception) { /* best effort; the next save overwrites it */ }
            throw;
        }
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

                // Our own save, or a save without changes: Current already reflects this text.
                if (LastError is null && text == _appliedText) return;

                var result = Parse(text);
                if (result.Success)
                {
                    Current = result.Config!;
                    LastError = null;
                    _appliedText = text;
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

    /// <summary>ConfigParser, then the validator.</summary>
    ConfigParseResult Parse(string text)
    {
        var result = ConfigParser.Parse(text);
        if (!result.Success || validate?.Invoke(result.Config!) is not { } error) return result;
        return new ConfigParseResult(null, error);
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
