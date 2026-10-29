using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// Builds the kind table from tiles\*\tile.json. Every top-level DLL of a kind folder that references the contracts loads
/// in its own PluginLoadContext, and the connector ids it declares belong to that kind only. A bad kind never stops the
/// scan: it is kept with an Error and logged, and the other kinds load.
/// </summary>
public sealed class KindLoader(ITileLog log)
{
    public const string TileJsonName = "tile.json";

    /// <summary>Files whose change restarts a kind's tiles, besides tile.json. Data a script writes next to itself does not.</summary>
    static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".py", ".cmd", ".bat"];

    static readonly string ContractsName = typeof(ITileConnector).Assembly.GetName().Name!;

    /// <summary>What each kind loaded at startup, so a kind whose folder vanishes for one rescan and returns keeps its DLLs.</summary>
    readonly Dictionary<string, KindDlls> _startupDlls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first scan: reads every tile.json and loads every kind's DLLs.</summary>
    public KindTable Load(string tilesDir)
    {
        _startupDlls.Clear();
        return Scan(tilesDir, previous: null);
    }

    /// <summary>
    /// A later scan: reads every tile.json again but never loads a DLL (.NET cannot unload one safely). A known kind keeps
    /// the DLLs it loaded at startup even when they changed on disk (<see cref="TileKind.NeedsRestart"/>); a kind folder
    /// that appeared with DLLs is broken until the app restarts.
    /// </summary>
    public KindTable Rescan(KindTable previous) => Scan(previous.TilesDir, previous);

    KindTable Scan(string tilesDir, KindTable? previous)
    {
        var kinds = new Dictionary<string, TileKind>(StringComparer.OrdinalIgnoreCase);
        var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] dirs;
        try
        {
            dirs = Directory.Exists(tilesDir) ? Directory.GetDirectories(tilesDir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Could not list {tilesDir}", ex);
            dirs = [];
        }

        foreach (var dir in dirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(dir);
            try
            {
                if (!File.Exists(Path.Combine(dir, TileJsonName)))
                {
                    bare.Add(id);
                    continue;
                }
                var kind = LoadKind(id, dir, previous);
                if (kind.Error is not null) log.Error($"Tile kind '{id}' is broken: {kind.Error}");
                else if (previous is null) log.Info($"Loaded tile kind '{id}' ({kind.Connector}) from {dir}");
                kinds[id] = kind;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Could not read tile kind folder {dir}", ex); // deleted or locked mid-scan; the next scan sees it
            }
        }

        return new KindTable(tilesDir, kinds, bare);
    }

    TileKind LoadKind(string id, string dir, KindTable? previous)
    {
        var json = TileJson.Read(dir, id);
        var dllStamp = Stamp(dir, path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var dlls = previous is null ? _startupDlls[id] = LoadDlls(id, dir, dllStamp)
            : _startupDlls.TryGetValue(id, out var before) ? before
            : dllStamp.Length == 0 ? KindDlls.None
            : new KindDlls(KindDlls.None.Connectors,
                $@"The DLLs in tiles\{id} were added while Taskbar Tiles was running; restart it to load them.", Stamp: "");

        return new TileKind(id, dir,
            Name: json.Error is null ? json.Name : id,
            Description: json.Error ?? json.Description,
            json.Connector, json.Settings, dlls, ErrorOf(id, json, dlls), Stamp(dir, IsWatched), dllStamp,
            json.Width, json.Compact);
    }

    static string? ErrorOf(string id, TileJson json, KindDlls dlls)
    {
        if (json.Error is not null) return json.Error;
        if (dlls.Error is not null) return dlls.Error;
        if (json.Connector == TileKind.ScriptConnectorId || dlls.Connectors.ContainsKey(json.Connector)) return null;
        return $@"tiles\{id}\tile.json names connector '{json.Connector}', but no DLL in tiles\{id} declares it.";
    }

    /// <summary>Loads the folder's connector DLLs; the first failure or duplicate id ends the load with an Error.</summary>
    KindDlls LoadDlls(string id, string dir, string stamp)
    {
        var connectors = new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            try
            {
                if (!ReferencesContracts(path)) continue; // a dependency or a native DLL: the plugin loads it when it needs it
                var assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
                foreach (var connector in PluginLoader.Connectors(assembly, path, log))
                {
                    if (connectors.TryGetValue(connector.Id, out var other))
                        return new KindDlls(connectors,
                            $@"{Path.GetFileName(other.SourcePath)} and {file} in tiles\{id} both declare connector '{connector.Id}'.", stamp);
                    connectors[connector.Id] = connector;
                    log.Info($"Loaded connector '{connector.Id}' for tile kind '{id}' from {path}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load {path}", ex);
                return new KindDlls(connectors, $@"Could not load {file} from tiles\{id}: {ex.Message}", stamp);
            }
        }
        return new KindDlls(connectors, null, stamp);
    }

    /// <summary>True for a .NET assembly that references the contracts (without loading it); false for other assemblies and native DLLs.</summary>
    internal static bool ReferencesContracts(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return false; // throws BadImageFormatException for a file that is not a PE image
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences.Any(h => reader.GetString(reader.GetAssemblyReference(h).Name) == ContractsName);
    }

    static bool IsWatched(string path) =>
        Path.GetFileName(path).Equals(TileJsonName, StringComparison.OrdinalIgnoreCase)
        || ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Name, size and write time of the folder's top-level files that match; changes when any of them does.</summary>
    static string Stamp(string dir, Func<string, bool> include) =>
        string.Join("|", Directory.GetFiles(dir).Where(include).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var info = new FileInfo(path);
            return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
}
