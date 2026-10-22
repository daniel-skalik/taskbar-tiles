using System.IO;
using System.Reflection;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.Plugins;

public sealed record ConnectorDescriptor(string Id, Type Type, double Width, string SourcePath);

public sealed class ConnectorCatalog(IReadOnlyDictionary<string, ConnectorDescriptor> connectors)
{
    public static ConnectorCatalog Empty { get; } = new(new Dictionary<string, ConnectorDescriptor>());

    public IReadOnlyDictionary<string, ConnectorDescriptor> Connectors => connectors;

    public ITileConnector? Create(string id) =>
        connectors.TryGetValue(id, out var d) ? (ITileConnector)Activator.CreateInstance(d.Type)! : null;
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
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(ITileConnector).IsAssignableFrom(type)) continue;
                    var attr = type.GetCustomAttribute<TileConnectorAttribute>();
                    if (attr is null) continue;

                    if (map.TryGetValue(attr.Id, out var existing))
                    {
                        log.Warn($"Connector '{attr.Id}' in {main} ignored; already provided by {existing.SourcePath}.");
                        continue;
                    }
                    var width = attr.Width;
                    if (!double.IsFinite(width) || width <= 0)
                    {
                        log.Warn($"Connector '{attr.Id}' in {main} declares invalid width {width}; using {DefaultTileWidth}.");
                        width = DefaultTileWidth;
                    }
                    map[attr.Id] = new ConnectorDescriptor(attr.Id, type, width, main);
                    log.Info($"Loaded connector '{attr.Id}' from {main}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load plugin {main}", ex);
            }
        }

        return new ConnectorCatalog(map);
    }
}
