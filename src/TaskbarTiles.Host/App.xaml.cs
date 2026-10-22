using System.IO;
using System.Text.Json;
using System.Windows;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump-taskbars"))
        {
            DumpTaskbars();
            return;
        }
        Shutdown();
    }

    /// <summary>Diagnostics: writes what the tracker sees to %TEMP%\taskbartiles-dump.json and exits.</summary>
    void DumpTaskbars()
    {
        var tracker = new TaskbarTracker(Dispatcher);
        tracker.Changed += (_, _) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "taskbartiles-dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(tracker.Current, new JsonSerializerOptions { WriteIndented = true }));
            tracker.Dispose();
            Shutdown();
        };
        tracker.Start();
    }
}
