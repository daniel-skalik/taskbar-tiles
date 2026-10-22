using System.IO;
using System.Runtime.Loader;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class PluginLoaderTests
{
    static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    [Fact]
    public void Build_copies_sample_plugin_into_plugins_folder() =>
        Assert.True(File.Exists(Path.Combine(PluginsRoot, "TaskbarTiles.Tests.SamplePlugin", "TaskbarTiles.Tests.SamplePlugin.dll")));

    [Fact]
    public void Discovers_connector_by_attribute_with_width()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        var d = catalog.Connectors["sample"];
        Assert.Equal("sample", d.Id);
        Assert.Equal(150, d.Width);
    }

    [Fact]
    public void Invalid_width_falls_back_to_default_with_warning()
    {
        var log = new CollectingLog();

        var catalog = new PluginLoader(log).LoadFrom([PluginsRoot]);

        Assert.Equal(132, catalog.Connectors["badwidth"].Width);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN") && m.Contains("badwidth"));
    }

    [Fact]
    public void Created_connector_shares_contract_types_with_host()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        var connector = catalog.Create("sample");

        Assert.IsAssignableFrom<ITileConnector>(connector); // same ITileConnector type identity as the host
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(connector!.GetType().Assembly));
    }

    [Fact]
    public void Script_plugin_is_discovered()
    {
        var catalog = new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]);

        Assert.Equal(PluginLoader.DefaultTileWidth, catalog.Connectors["script"].Width);
        Assert.IsAssignableFrom<ITileConnector>(catalog.Create("script"));
    }

    [Fact]
    public void Unknown_connector_id_creates_null() =>
        Assert.Null(new PluginLoader(new CollectingLog()).LoadFrom([PluginsRoot]).Create("nope"));

    [Fact]
    public void Missing_root_is_ignored() =>
        Assert.Empty(new PluginLoader(new CollectingLog()).LoadFrom([Path.Combine(PluginsRoot, "does-not-exist")]).Connectors);

    [Fact]
    public void Folder_without_matching_dll_is_skipped_with_warning()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "broken"));
        var log = new CollectingLog();

        var catalog = new PluginLoader(log).LoadFrom([dir.Path]);

        Assert.Empty(catalog.Connectors);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN") && m.Contains("broken"));
    }
}
