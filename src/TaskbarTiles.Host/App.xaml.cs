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
    TileCommands? _commands;

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
            SampleScripts.InstallIfAbsent(AppPaths.SampleScriptsDir, AppPaths.ScriptsDir, log);
            _config = new ConfigStore(AppPaths.ConfigFile, log);
            _config.Load();
            _commands = new TileCommands(AppPaths.ConfigFile, AppPaths.LogsDir, OpenWithShell, log);

            var ui = new WpfUiDispatcher(Dispatcher);
            _tracker = new TaskbarTracker(Dispatcher);
            _host = new HostController(catalog, () => _config.Current, _tracker, ui,
                name => FileLog.For(AppPaths.LogsDir, name), CreateBar, AppPaths.DataDir);

            _config.Changed += (_, _) => ui.Post(() =>
            {
                NotificationWindow.CloseOpen(); // a notice about an earlier bad edit is stale now
                _host.ConfigChanged();
            });
            _config.ReloadFailed += (_, error) => ui.Post(() => ShowConfigNotice(error, atStartup: false));
            _config.StartWatching();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _host.Start();
            if (_config.LastError is { } startupError) ShowConfigNotice(startupError, atStartup: true);
            log.Info($"Started with {catalog.Connectors.Count} connector(s): {string.Join(", ", catalog.Connectors.Keys)}");
        }
        catch (Exception ex)
        {
            log.Error("Startup failed", ex);
            Shutdown();
        }
    }

    static void OpenWithShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    void ShowConfigNotice(ConfigError error, bool atStartup)
    {
        var log = FileLog.For(AppPaths.LogsDir, "host");
        try
        {
            var light = _tracker?.Current.FirstOrDefault(t => t.IsPrimary)?.IsLightTheme ?? false;
            NotificationWindow.Show(ConfigNotice.Title, ConfigNotice.Body(error, atStartup), light,
                () => _commands?.OpenConfig(), log);
        }
        catch (Exception ex) { log.Error("Could not show the config notice", ex); }
    }

    ITileBarView CreateBar(string displayId)
    {
        var bar = new TileBar(displayId, FileLog.For(AppPaths.LogsDir, "host"), _commands!);
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
        NotificationWindow.CloseOpen();
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
