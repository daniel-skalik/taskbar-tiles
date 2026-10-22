using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Host;

public partial class App : Application
{
    Mutex? _singleInstance;
    ConfigStore? _config;
    TaskbarTracker? _tracker;
    HostController? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump-taskbars"))
        {
            DumpTaskbars();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\TaskbarTiles.Host", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        var log = FileLog.For(AppPaths.LogsDir, "host");
        try
        {
            DispatcherUnhandledException += (_, a) => { log.Error("Unhandled UI exception", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, a) => log.Error("Unhandled exception", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, a) => { log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

            var catalog = new PluginLoader(log).LoadFrom(AppPaths.PluginRoots);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
            _config.Load();

            var ui = new WpfUiDispatcher(Dispatcher);
            _tracker = new TaskbarTracker(Dispatcher);
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar);

            _config.Changed += (_, _) => ui.Post(() => _host.ConfigChanged());
            _config.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
        }
        catch (Exception ex)
        {
            log.Error("Startup failed", ex);
            Shutdown();
        }
    }

    ITileBarView CreateBar(string displayId)
    {
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"));
        bar.OpenConfigRequested += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.ConfigFile) { UseShellExecute = true });
        bar.ExitRequested += (_, _) => Shutdown();
        return bar;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _host?.NotifyResumed());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        FlyoutWindow.CloseOpen();
        if (_host is not null)
        {
            // Bars close synchronously on this (UI) thread inside DisposeAsync before its first await;
            // the rest does not need the dispatcher (ConfigureAwait(false)), so wait off-thread with a bound.
            var disposal = _host.DisposeAsync().AsTask();
            try { Task.Run(() => disposal).Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
        _tracker?.Dispose();
        _config?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
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
