using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// A small notice at the bottom-right of the primary monitor's work area, styled like the flyout.
/// It never takes focus, closes itself after <see cref="Lifetime"/>, and runs onClick when clicked. One at a time.
/// </summary>
public static class NotificationWindow
{
    const double WidthDip = 340;
    const double MarginDip = 12;
    static Window? _open;

    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(8);

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    public static void Show(string title, string body, bool light, Action onClick, ITileLog log)
    {
        CloseOpen();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = WidthDip,
            UseLayoutRounding = true,
            Cursor = Cursors.Hand,
            Opacity = 0, // hidden until positioned on the primary monitor at its DPI
        };
        Theme.ApplyTo(window.Resources, light);
        window.Content = Build(title, body);

        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_open == window) CloseOpen();
        };
        window.Closed += (_, _) => timer.Stop();
        window.MouseLeftButtonUp += (_, _) =>
        {
            if (_open == window) CloseOpen();
            try { onClick(); }
            catch (Exception ex) { log.Error("Notification click failed", ex); }
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
        };
        window.Loaded += (_, _) =>
        {
            try { Position(window); }
            catch (Exception ex) { log.Error("Notification positioning failed", ex); }
            finally { window.Opacity = 1; }
        };

        _open = window;
        window.Show();
        timer.Start();
    }

    static UIElement Build(string title, string body)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = Theme.Glyph("warning"),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Theme.ColorFor(TileColor.Warn),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TileForeground");
        header.Children.Add(titleText);

        var bodyText = new TextBlock { Text = body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(bodyText);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return border;
    }

    /// <summary>Bottom-right of the primary work area in physical pixels; re-applied until the window's DPI is stable (max 3 passes).</summary>
    static void Position(Window window)
    {
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;

        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            var margin = (int)Math.Round(MarginDip * dpi);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, work.Right - w - margin, work.Bottom - h - margin, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            var after = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(after - dpi) < 0.001) break;
            dpi = after;
        }
    }
}
