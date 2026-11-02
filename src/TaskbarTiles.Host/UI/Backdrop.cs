using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// The Windows 11 acrylic backdrop (blurred desktop behind the window) with DWM's rounded corners, border and shadow.
/// Windows before 11 22H2 have no system backdrop: popups keep their solid background there.
/// </summary>
static class Backdrop
{
    public static bool Supported { get; } = Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Prepares a window that is not shown yet: a real frame (DWM draws backdrops only on framed, non-layered windows)
    /// hidden by WindowChrome, then creates its handle and turns on acrylic. The window starts cloaked; call
    /// <see cref="Uncloak"/> once it is positioned.
    /// </summary>
    public static void Apply(Window window, bool light)
    {
        window.AllowsTransparency = false;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        // Without the system menu DWM draws no caption buttons into the glass (a close X in the corner otherwise).
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style & ~NativeMethods.WS_SYSMENU));
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        Set(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, light ? 0 : 1);
        Set(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        Set(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_TRANSIENTWINDOW);
        Set(hwnd, NativeMethods.DWMWA_CLOAK, 1);
    }

    public static void Uncloak(Window window) =>
        Set(new WindowInteropHelper(window).Handle, NativeMethods.DWMWA_CLOAK, 0);

    static void Set(IntPtr hwnd, int attribute, int value) =>
        NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
