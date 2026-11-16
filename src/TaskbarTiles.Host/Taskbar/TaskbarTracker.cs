using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.Taskbar;

/// <summary>
/// Tracks every taskbar window and publishes its free zone. Event-driven only:
///  - WinEvent hooks on Explorer (taskbar, Start, icon area, tray moved/resized/re-ordered) and on foreground changes
///  - ABN_FULLSCREENAPP from an appbar registration on each monitor (fullscreen without a foreground change, e.g. F11;
///    Explorer tells only the appbars on the monitor that went full screen)
///  - TaskbarCreated (Explorer restarted), WM_DISPLAYCHANGE, WM_DPICHANGED, WM_SETTINGCHANGE
/// While <see cref="TrackIconEdges"/> is on, each of those batches also reads where the app icons end with
/// <paramref name="edges"/> on a thread-pool thread, for the right zone; a changed edge refreshes again.
/// </summary>
public sealed class TaskbarTracker(Dispatcher dispatcher, IIconEdgeReader? edges = null, Action<string>? warn = null) : ITaskbarSource
{
    const double MarginDip = 8;
    const double ClockReserveDip = 120;
    const double IconSafetyDip = 44; // when the icon edge cannot be read: the icon area window can end an icon early
    const int AppBarCallbackMessage = 0x8000 + 1; // WM_APP + 1

    readonly List<IntPtr> _hooks = [];
    readonly HashSet<IntPtr> _watched = [];
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    HwndSource? _messageWindow;
    readonly List<HwndSource> _appBars = []; // one hidden appbar window per monitor, for ABN_FULLSCREENAPP
    bool _refreshQueued;
    bool _disposed;
    uint _explorerPid;
    bool _trackIconEdges;
    Dictionary<IntPtr, int?> _iconEdges = [];
    bool _edgeReadRunning, _edgeReadAgain, _skipEdgeRead, _edgeFailureLogged;

    public IReadOnlyList<TaskbarInfo> Current { get; private set; } = [];
    public event EventHandler? Changed;

    public bool TrackIconEdges
    {
        get => _trackIconEdges;
        set
        {
            if (_trackIconEdges == value) return;
            _trackIconEdges = value;
            if (!value) _iconEdges = [];
            QueueRefresh();
        }
    }

    public void Start()
    {
        // Hidden top-level window: receives broadcasts (WM_SETTINGCHANGE) and appbar notifications.
        _messageWindow = new HwndSource(new HwndSourceParameters("TaskbarTilesTracker")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        });
        _messageWindow.AddHook(WndProc);
        RegisterAppBars();
        Rehook();
        QueueRefresh();
    }

    // One cached delegate instance: native code holds a pointer to it, so it must stay alive.
    WinEventDelegate WinEventProc => _winEventProcCache ??= OnWinEvent;
    WinEventDelegate? _winEventProcCache;

    void Rehook()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();

        var main = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        GetWindowThreadProcessId(main, out _explorerPid);
        if (_explorerPid != 0)
            _hooks.Add(SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, WinEventProc,
                _explorerPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, WinEventProc,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_SYSTEM_FOREGROUND
            || evt == EVENT_OBJECT_REORDER
            || (evt == EVENT_OBJECT_LOCATIONCHANGE && idObject == OBJID_WINDOW && _watched.Contains(hwnd)))
            QueueRefresh();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated)
        {
            RegisterAppBars();
            Rehook();
            QueueRefresh();
            // Secondary taskbars can be created after the broadcast; refresh again shortly after.
            RefreshAfter(TimeSpan.FromSeconds(1));
            RefreshAfter(TimeSpan.FromSeconds(3));
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            RegisterAppBars(); // monitors may have come or gone
            // Secondary taskbars appear a moment after the display change; refresh again shortly after.
            QueueRefresh();
            RefreshAfter(TimeSpan.FromSeconds(1));
            RefreshAfter(TimeSpan.FromSeconds(3));
        }
        else if (msg is WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    // The appbar windows only listen for their callback; broadcasts are handled once, by the message window.
    IntPtr AppBarProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBarCallbackMessage && wParam.ToInt32() == ABN_FULLSCREENAPP) QueueRefresh();
        return IntPtr.Zero;
    }

    void RefreshAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed) QueueRefresh();
        };
        timer.Start();
    }

    void QueueRefresh()
    {
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    void Refresh()
    {
        if (_disposed) return;
        _watched.Clear();
        var light = IsLightTaskbar();
        var list = new List<TaskbarInfo>();
        var bars = new List<IntPtr>();

        Collect("Shell_TrayWnd", iconAreaClass: "ReBarWindow32", hasTray: true, light, list, bars);
        Collect("Shell_SecondaryTrayWnd", iconAreaClass: "WorkerW", hasTray: false, light, list, bars);

        Current = list;
        Changed?.Invoke(this, EventArgs.Empty);

        if (_skipEdgeRead) _skipEdgeRead = false; // this refresh only applied a new edge
        else if (_trackIconEdges && edges is not null) ReadIconEdges(bars);
    }

    /// <summary>One read at a time; events during a read ask for one more after it.</summary>
    void ReadIconEdges(IReadOnlyList<IntPtr> bars)
    {
        if (_edgeReadRunning) { _edgeReadAgain = true; return; }
        _edgeReadRunning = true;
        Task.Run(() =>
        {
            var read = new Dictionary<IntPtr, int?>();
            string? failure = null;
            foreach (var bar in bars)
            {
                try { read[bar] = edges!.IconsRight(bar); }
                catch (Exception ex) { read[bar] = null; failure ??= ex.Message; }
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => EdgesRead(read, failure));
        });
    }

    void EdgesRead(Dictionary<IntPtr, int?> read, string? failure)
    {
        _edgeReadRunning = false;
        if (_disposed || !_trackIconEdges) return;
        if (failure is not null && !_edgeFailureLogged)
        {
            _edgeFailureLogged = true;
            warn?.Invoke($"Could not read where the taskbar icons end ({failure}); keeping a gap after the icon area instead.");
        }
        var changed = read.Count != _iconEdges.Count || read.Any(kv => !_iconEdges.TryGetValue(kv.Key, out var old) || old != kv.Value);
        _iconEdges = read;
        if (_edgeReadAgain)
        {
            _edgeReadAgain = false;
            QueueRefresh(); // reads again after it
        }
        else if (changed)
        {
            _skipEdgeRead = true;
            QueueRefresh();
        }
    }

    void Collect(string taskbarClass, string iconAreaClass, bool hasTray, bool light, List<TaskbarInfo> into, List<IntPtr> bars)
    {
        var bar = IntPtr.Zero;
        while ((bar = FindWindowEx(IntPtr.Zero, bar, taskbarClass, null)) != IntPtr.Zero)
        {
            if (Rect(bar) is not { } barRect) continue;

            var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
            var icons = FindWindowEx(bar, IntPtr.Zero, iconAreaClass, null);
            var tray = hasTray ? FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
            _watched.UnionWith(new[] { bar, start, icons, tray }.Where(h => h != IntPtr.Zero));

            var monitor = MonitorFromWindow(bar, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) continue;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

            var geometry = new TaskbarGeometry(barRect, Rect(start), Rect(icons), Rect(tray));
            var margin = (int)Math.Round(MarginDip * scale);
            var clockReserve = (int)Math.Round(ClockReserveDip * scale);
            var zone = FreeZoneCalculator.Compute(geometry, margin, clockReserve);
            bars.Add(bar);
            int? iconsRight = _trackIconEdges
                ? FreeZoneCalculator.IconsRight(_iconEdges.GetValueOrDefault(bar), geometry, (int)Math.Round(IconSafetyDip * scale))
                : null;

            into.Add(new TaskbarInfo(
                DisplayId: DeviceId(info.szDevice),
                IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Taskbar: barRect,
                FreeZone: zone,
                Hidden: IsFullscreenOn(monitor, info.rcMonitor) || IsCoveredAbove(bar, info.rcMonitor),
                IsLightTheme: light,
                DpiScale: scale,
                RightZone: FreeZoneCalculator.ComputeRight(geometry, margin, clockReserve, iconsRight),
                Handle: bar));
        }
    }

    /// <summary>Stable monitor id (device interface path); falls back to \\.\DISPLAYn.</summary>
    static string DeviceId(string gdiDevice)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(gdiDevice, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME) && !string.IsNullOrEmpty(dd.DeviceID)
            ? dd.DeviceID
            : gdiDevice;
    }

    static bool IsFullscreenOn(IntPtr monitor, RECT monitorRect)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        if (ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        return Covers(r, monitorRect);
    }

    // A full-screen window stays above its monitor's taskbar after the focus moves to another monitor (a presentation
    // on the second screen while you work on the first): Explorer keeps that taskbar covered, so the tiles hide too.
    static bool IsCoveredAbove(IntPtr bar, RECT monitorRect)
    {
        for (var w = GetWindow(bar, GW_HWNDPREV); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDPREV))
        {
            if (!IsWindowVisible(w) || IsIconic(w)) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId) continue;
            if (GetWindowRect(w, out var r) && Covers(r, monitorRect)) return true;
        }
        return false;
    }

    static bool Covers(RECT r, RECT monitorRect) =>
        r.Left <= monitorRect.Left && r.Top <= monitorRect.Top && r.Right >= monitorRect.Right && r.Bottom >= monitorRect.Bottom;

    static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // Registering as an appbar (without reserving space) is the documented way to receive ABN_FULLSCREENAPP. Explorer
    // sends it only to the appbars on the monitor that went full screen, so each monitor gets its own hidden window.
    void RegisterAppBars()
    {
        RemoveAppBars();
        if (_disposed) return;
        var monitors = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT r, IntPtr _) => { monitors.Add(r); return true; }, IntPtr.Zero);
        foreach (var r in monitors)
        {
            var window = new HwndSource(new HwndSourceParameters("TaskbarTilesFullscreen")
            {
                PositionX = r.Left,
                PositionY = r.Top,
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
                ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            });
            window.AddHook(AppBarProc);
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = window.Handle, uCallbackMessage = AppBarCallbackMessage };
            SHAppBarMessage(ABM_NEW, ref data);
            _appBars.Add(window);
        }
    }

    void RemoveAppBars()
    {
        foreach (var window in _appBars)
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = window.Handle };
            SHAppBarMessage(ABM_REMOVE, ref data);
            window.Dispose();
        }
        _appBars.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        RemoveAppBars();
        if (_messageWindow is not null)
        {
            _messageWindow.Dispose();
            _messageWindow = null;
        }
    }
}
