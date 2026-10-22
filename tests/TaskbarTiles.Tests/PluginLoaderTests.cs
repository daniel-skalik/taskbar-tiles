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

        var d = Assert.Single(catalog.Connectors).Value;
        Assert.Equal("sample", d.Id);
        Assert.Equal(150, d.Width);
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
