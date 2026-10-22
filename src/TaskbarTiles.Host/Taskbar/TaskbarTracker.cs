using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.Taskbar;

/// <summary>
/// Tracks every taskbar window and publishes its free zone. Event-driven only:
///  - WinEvent hooks on Explorer (taskbar, Start, icon area, tray moved/resized/re-ordered) and on foreground changes
///  - ABN_FULLSCREENAPP from an appbar registration (fullscreen without a foreground change, e.g. F11)
///  - TaskbarCreated (Explorer restarted), WM_DISPLAYCHANGE, WM_DPICHANGED, WM_SETTINGCHANGE
/// </summary>
public sealed class TaskbarTracker(Dispatcher dispatcher) : ITaskbarSource
{
    const double MarginDip = 8;
    const double ClockReserveDip = 120;
    const int AppBarCallbackMessage = 0x8000 + 1; // WM_APP + 1

    readonly List<IntPtr> _hooks = [];
    readonly HashSet<IntPtr> _watched = [];
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    HwndSource? _messageWindow;
    bool _refreshQueued;
    uint _explorerPid;

    public IReadOnlyList<TaskbarInfo> Current { get; private set; } = [];
    public event EventHandler? Changed;

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
        RegisterAppBar();
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
            || (idObject == OBJID_WINDOW && _watched.Contains(hwnd)))
            QueueRefresh();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated)
        {
            RegisterAppBar();
            Rehook();
            QueueRefresh();
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            // Secondary taskbars appear a moment after the display change; refresh again shortly after.
            QueueRefresh();
            RefreshAfter(TimeSpan.FromSeconds(1));
            RefreshAfter(TimeSpan.FromSeconds(3));
        }
        else if ((msg == AppBarCallbackMessage && wParam.ToInt32() == ABN_FULLSCREENAPP)
                 || msg is WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    void RefreshAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); QueueRefresh(); };
        timer.Start();
    }

    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    void Refresh()
    {
        _watched.Clear();
        var light = IsLightTaskbar();
        var list = new List<TaskbarInfo>();

        Collect("Shell_TrayWnd", iconAreaClass: "ReBarWindow32", hasTray: true, light, list);
        Collect("Shell_SecondaryTrayWnd", iconAreaClass: "WorkerW", hasTray: false, light, list);

        Current = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Collect(string taskbarClass, string iconAreaClass, bool hasTray, bool light, List<TaskbarInfo> into)
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
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

            var geometry = new TaskbarGeometry(barRect, Rect(start), Rect(icons), Rect(tray));
            var zone = FreeZoneCalculator.Compute(geometry, (int)Math.Round(MarginDip * scale), (int)Math.Round(ClockReserveDip * scale));

            into.Add(new TaskbarInfo(
                DisplayId: DeviceId(info.szDevice),
                IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Taskbar: barRect,
                FreeZone: zone,
                Hidden: IsFullscreenOn(monitor, info.rcMonitor),
                IsLightTheme: light,
                DpiScale: scale));
        }
    }

    /// <summary>Stable monitor id (device interface path); falls back to \\.\DISPLAYn.</summary>
    static string DeviceId(string gdiDevice)
    {
        var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
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
        return r.Left <= monitorRect.Left && r.Top <= monitorRect.Top && r.Right >= monitorRect.Right && r.Bottom >= monitorRect.Bottom;
    }

    static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // Registering as an appbar (without reserving space) is the documented way to receive ABN_FULLSCREENAPP.
    void RegisterAppBar()
    {
        if (_messageWindow is null) return;
        var data = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = _messageWindow.Handle,
            uCallbackMessage = AppBarCallbackMessage,
        };
        SHAppBarMessage(ABM_REMOVE, ref data);
        SHAppBarMessage(ABM_NEW, ref data);
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        if (_messageWindow is not null)
        {
            var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = _messageWindow.Handle };
            SHAppBarMessage(ABM_REMOVE, ref data);
            _messageWindow.Dispose();
        }
    }
}
