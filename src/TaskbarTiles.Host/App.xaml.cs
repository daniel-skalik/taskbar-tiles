using System.Windows;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
