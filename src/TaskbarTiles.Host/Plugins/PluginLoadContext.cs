using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// One context per plugin folder. Assemblies the host itself ships (Contracts, WinRT projections)
/// always come from the host so types unify across plugins; everything else from the plugin folder.
/// </summary>
sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath))
{
    readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll")))
            return null; // fall back to the default context

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
