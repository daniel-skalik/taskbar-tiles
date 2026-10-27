using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// A see-through picture of the dragged slot that follows the pointer across displays. Topmost, never activated and
/// click-through (WS_EX_TRANSPARENT), so it never takes focus or mouse input from the bar that holds the capture.
/// </summary>
public sealed class DragGhost : Window
{
    const double GhostOpacity = 0.7;
    readonly Point _grab; // where the pointer holds the slot, in DIP from the slot's top-left corner
    readonly IntPtr _hwnd;

    public DragGhost(FrameworkElement slot, Point grab)
    {
        _grab = grab;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Math.Max(1, slot.ActualWidth);
        Height = Math.Max(1, slot.ActualHeight);
        Opacity = GhostOpacity;
        Content = new Image { Source = Snapshot(slot, VisualTreeHelper.GetDpi(slot).DpiScaleX), Stretch = Stretch.Fill };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ExStyle(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64())));
    }

    /// <summary>The ghost's extended styles: click-through, never activated, no taskbar button.</summary>
    public static long ExStyle(long current) => current | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

    /// <summary>The element as it looks now, at <paramref name="scale"/> physical pixels per DIP.</summary>
    public static BitmapSource Snapshot(FrameworkElement element, double scale)
    {
        var width = Math.Max(1, element.ActualWidth);
        var height = Math.Max(1, element.ActualHeight);
        // Through a VisualBrush, so the element's offset in its parent does not shift the picture.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element)
            {
                Viewbox = new Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
            }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Moves the ghost so the grabbed point sits under <paramref name="screen"/> (physical pixels); shows it the first time.
    /// Moving onto a monitor with another scale changes the window's DPI, which changes the grab offset in pixels, so the
    /// ghost is placed again at the new scale (always on the first show).
    /// </summary>
    public void MoveTo(Point screen)
    {
        var first = !IsVisible;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Place(screen);
        if (first) Show(); // ShowActivated is false and the window is WS_EX_NOACTIVATE: it never takes activation
        if (first || VisualTreeHelper.GetDpi(this).DpiScaleX != dpi) Place(screen);
    }

    void Place(Point screen)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        SetWindowPos(_hwnd, HWND_TOPMOST, (int)Math.Round(screen.X - _grab.X * scale), (int)Math.Round(screen.Y - _grab.Y * scale),
            0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
