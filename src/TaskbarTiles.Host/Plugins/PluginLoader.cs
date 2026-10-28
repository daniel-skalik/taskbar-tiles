using System.IO;
using System.Reflection;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

public sealed record ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath);

public sealed class ConnectorCatalog(IReadOnlyDictionary<string, ConnectorDescriptor> connectors)
{
    public static ConnectorCatalog Empty { get; } = new(new Dictionary<string, ConnectorDescriptor>());

    public IReadOnlyDictionary<string, ConnectorDescriptor> Connectors => connectors;

    /// <summary>Runs the plugin's constructor; its exception propagates unwrapped.</summary>
    public ITileConnector? Create(string id) => connectors.TryGetValue(id, out var d) ? Instantiate(d) : null;

    /// <summary>Runs the plugin's constructor; its exception propagates unwrapped.</summary>
    public static ITileConnector Instantiate(ConnectorDescriptor d) =>
        (ITileConnector)Activator.CreateInstance(d.Type,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions, binder: null, args: null, culture: null)!;
}

/// <summary>Loads plugins laid out as &lt;root&gt;\&lt;Name&gt;\&lt;Name&gt;.dll. Earlier roots win on duplicate ids.</summary>
public sealed class PluginLoader(ITileLog log)
{
    public const double DefaultTileWidth = 132;

    public ConnectorCatalog LoadFrom(IEnumerable<string> roots)
    {
        var map = new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal);

        foreach (var root in roots.Where(Directory.Exists))
        foreach (var dir in Directory.GetDirectories(root))
        {
            var main = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (!File.Exists(main))
            {
                log.Warn($"Plugin folder {dir} has no {Path.GetFileName(main)}; skipped.");
                continue;
            }

            try
            {
                var assembly = new PluginLoadContext(main).LoadFromAssemblyPath(main);
                foreach (var connector in Connectors(assembly, main, log))
                {
                    if (map.TryGetValue(connector.Id, out var existing))
                    {
                        log.Warn($"Connector '{connector.Id}' in {main} ignored; already provided by {existing.SourcePath}.");
                        continue;
                    }
                    map[connector.Id] = connector;
                    log.Info($"Loaded connector '{connector.Id}' from {main}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load plugin {main}", ex);
            }
        }

        return new ConnectorCatalog(map);
    }

    /// <summary>The [TileConnector] types an assembly exports; an invalid width is logged and replaced by the default.</summary>
    internal static IEnumerable<ConnectorDescriptor> Connectors(Assembly assembly, string path, ITileLog log)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(ITileConnector).IsAssignableFrom(type)) continue;
            var attr = type.GetCustomAttribute<TileConnectorAttribute>();
            if (attr is null) continue;

            var width = attr.Width;
            if (!double.IsFinite(width) || width <= 0)
            {
                log.Warn($"Connector '{attr.Id}' in {path} declares invalid width {width}; using {DefaultTileWidth}.");
                width = DefaultTileWidth;
            }
            yield return new ConnectorDescriptor(attr.Id, type, width, path);
        }
    }
}
