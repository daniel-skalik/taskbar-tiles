using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace SpotifyTaskbar;

public partial class MainWindow : Window
{
    const double WidgetWidthDip = 320;
    const double EdgeMarginDip = 8;

    const int AppBarCallbackMessage = 0x8000 + 1; // WM_APP + 1

    IntPtr _hwnd;
    IntPtr _taskbar;
    IntPtr _start;
    IntPtr _tray;
    readonly List<IntPtr> _winEventHooks = new();
    readonly WinEventDelegate _winEventProc;
    readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    bool _repositionQueued;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _lastTrackKey;

    public MainWindow()
    {
        InitializeComponent();
        _winEventProc = OnWinEvent; // keep delegate alive, native code holds a pointer to it
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitMediaAsync();
        Closed += (_, _) => Teardown();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: hidden from Alt+Tab. No-activate: clicks don't steal focus from the active app.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        RegisterAppBar();
        AttachToTaskbar();
    }

    // ---------- Event sources (no polling) ----------
    //  - WinEvent hooks: taskbar moved/resized/re-ordered (explorer process), foreground window changed (any process)
    //  - Appbar ABN_FULLSCREENAPP: an app entered/left fullscreen without changing foreground (e.g. F11 in browser)
    //  - TaskbarCreated: explorer restarted, taskbar HWND and process are new
    //  - WM_DISPLAYCHANGE / WM_DPICHANGED / WM_SETTINGCHANGE: resolution, scaling, taskbar settings
    //  - Start button / tray LOCATIONCHANGE: taskbar alignment switched, icons added/removed, tray grew

    void AttachToTaskbar()
    {
        UnhookWinEvents();
        _taskbar = FindWindow("Shell_TrayWnd", null);
        _start = FindWindowEx(_taskbar, IntPtr.Zero, "Start", null);
        _tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (_taskbar != IntPtr.Zero)
        {
            GetWindowThreadProcessId(_taskbar, out var explorerPid);
            _winEventHooks.Add(SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                _winEventProc, explorerPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        }
        _winEventHooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        QueueReposition();
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_SYSTEM_FOREGROUND
            || evt == EVENT_OBJECT_REORDER
            || ((hwnd == _taskbar || hwnd == _start || hwnd == _tray) && idObject == OBJID_WINDOW))
            QueueReposition();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreatedMessage)
        {
            RegisterAppBar();
            AttachToTaskbar();
        }
        else if ((msg == AppBarCallbackMessage && wParam.ToInt32() == ABN_FULLSCREENAPP)
                 || msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            QueueReposition();
        }
        return IntPtr.Zero;
    }

    // Events arrive in bursts (a drag fires dozens of LOCATIONCHANGE); coalesce into one reposition.
    void QueueReposition()
    {
        if (_repositionQueued) return;
        _repositionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _repositionQueued = false;
            Reposition();
        });
    }

    // Registering as an appbar (without reserving space) is the official way to receive ABN_FULLSCREENAPP.
    void RegisterAppBar()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = AppBarCallbackMessage };
        SHAppBarMessage(ABM_REMOVE, ref data);
        SHAppBarMessage(ABM_NEW, ref data);
    }

    void Teardown()
    {
        UnhookWinEvents();
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    void UnhookWinEvents()
    {
        foreach (var h in _winEventHooks)
            if (h != IntPtr.Zero) UnhookWinEvent(h);
        _winEventHooks.Clear();
    }

    // ---------- Positioning over the taskbar ----------

    void Reposition()
    {
        if (!IsWindow(_taskbar))
        {
            AttachToTaskbar();
            if (_taskbar == IntPtr.Zero) { if (IsVisible) Hide(); return; }
        }

        if (!GetWindowRect(_taskbar, out var tb) || IsFullscreenOnTaskbarMonitor())
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)(WidgetWidthDip * scale);
        int height = tb.Bottom - tb.Top;
        int margin = (int)(EdgeMarginDip * scale);
        int y = tb.Top;

        // Prefer the free space left of the Start button (centered icons). If it doesn't fit
        // (left-aligned icons, or a crowded centered taskbar), sit just left of the system tray.
        // Geometry instead of the TaskbarAl registry value: Settings doesn't reliably write it.
        int x = tb.Left + margin;
        int startLeft = GetWindowRect(_start, out var st) && st.Right > st.Left ? st.Left : tb.Left;
        if (startLeft - tb.Left < width + 2 * margin)
        {
            int trayLeft = GetWindowRect(_tray, out var tr) && tr.Right > tr.Left ? tr.Left : tb.Right;
            x = trayLeft - margin - width;
        }

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    bool IsFullscreenOnTaskbarMonitor()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;

        var cls = new StringBuilder(256);
        GetClassName(fg, cls, cls.Capacity);
        var name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

        // Fullscreen video on another monitor must not hide the widget here.
        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (mon != MonitorFromWindow(_taskbar, MONITOR_DEFAULTTONEAREST)) return false;

        if (!GetWindowRect(fg, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---------- Media session (Windows media controls, no Spotify API key needed) ----------

    async Task InitMediaAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(PickSession);
        PickSession();
    }

    void PickSession()
    {
        var spotify = _manager?.GetSessions()
            .FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

        if (spotify == _session) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = spotify;
        _lastTrackKey = null;

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        _ = UpdateMediaAsync();
        UpdatePlayback();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => Dispatcher.InvokeAsync(UpdateMediaAsync);

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => Dispatcher.InvokeAsync(UpdatePlayback);

    async Task UpdateMediaAsync()
    {
        if (_session == null)
        {
            TitleText.Text = "Spotify";
            ArtistText.Text = "Not running";
            Art.Source = null;
            _lastTrackKey = null;
            return;
        }

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Spotify" : props.Title;
            ArtistText.Text = string.IsNullOrEmpty(props.Artist) ? "" : props.Artist;
            TitleText.ToolTip = ArtistText.ToolTip = $"{props.Title}\n{props.Artist}\n{props.AlbumTitle}";

            var key = $"{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key == _lastTrackKey) return;
            _lastTrackKey = key;

            Art.Source = props.Thumbnail == null ? null : await LoadThumbnailAsync(props.Thumbnail);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    static async Task<BitmapImage?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        using var winStream = await reference.OpenReadAsync();
        using var stream = winStream.AsStreamForRead();
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.DecodePixelWidth = 96;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    void UpdatePlayback()
    {
        var info = _session?.GetPlaybackInfo();
        var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlayButton.Content = playing ? "" : "";

        PrevButton.IsEnabled = info?.Controls.IsPreviousEnabled ?? false;
        NextButton.IsEnabled = info?.Controls.IsNextEnabled ?? false;
        PlayButton.IsEnabled = _session != null;
    }

    // ---------- UI handlers ----------

    async void Prev_Click(object sender, RoutedEventArgs e) { if (_session != null) await _session.TrySkipPreviousAsync(); }
    async void Play_Click(object sender, RoutedEventArgs e) { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    async void Next_Click(object sender, RoutedEventArgs e) { if (_session != null) await _session.TrySkipNextAsync(); }

    void Art_Click(object sender, MouseButtonEventArgs e) => OpenSpotify();
    void OpenSpotify_Click(object sender, RoutedEventArgs e) => OpenSpotify();
    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    // ---------- Win32 ----------

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const long WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_REORDER = 0x8004;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int OBJID_WINDOW = 0;
    const uint ABM_NEW = 0x0;
    const uint ABM_REMOVE = 0x1;
    const int ABN_FULLSCREENAPP = 0x2;
    const int WM_DISPLAYCHANGE = 0x007E;
    const int WM_SETTINGCHANGE = 0x001A;
    const int WM_DPICHANGED = 0x02E0;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
}
