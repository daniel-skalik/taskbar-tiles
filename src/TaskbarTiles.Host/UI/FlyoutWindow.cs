using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>The popup above a tile. Only one is open at a time; it closes when it loses focus.</summary>
public static class FlyoutWindow
{
    const double WidthDip = 320;
    static Window? _open;

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log)
    {
        CloseOpen();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = WidthDip,
            UseLayoutRounding = true,
            Opacity = 0, // hidden until positioned on the target monitor at the right DPI
        };
        Theme.ApplyTo(window.Resources, light);
        window.Content = Build(state, runtime, commands, log);
        window.Deactivated += (_, _) => { if (_open == window) CloseOpen(); };
        window.Loaded += (_, _) =>
        {
            try { Position(window, anchor); }
            catch (Exception ex) { log.Error("Flyout positioning failed", ex); }
            finally { window.Opacity = 1; }
        };
        _open = window;
        window.Show();
        window.Activate();
    }

    static UIElement Build(FlyoutState state, TileRuntime runtime, TileCommands commands, ITileLog log)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (Theme.Decode(state.Image, 144) is { } img)
            header.Children.Add(new Border
            {
                Width = 72, Height = 72, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 12, 0),
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(state.Title, 14, FontWeights.SemiBold, "TileForeground"));
        if (!string.IsNullOrEmpty(state.Subtitle)) titles.Children.Add(Text(state.Subtitle, 12, FontWeights.Normal, "TileDim"));
        header.Children.Add(titles);
        panel.Children.Add(header);

        foreach (var row in state.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (row.Icon is { } icon)
                grid.Children.Add(new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = Theme.ColorFor(icon.Color), VerticalAlignment = VerticalAlignment.Center,
                });
            var text = Text(row.Text, 13, FontWeights.Normal, "TileForeground");
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (row.Value is not null)
            {
                var value = Text(row.Value, 11.5, FontWeights.Normal, "TileDim");
                value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                value.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(value, 2);
                grid.Children.Add(value);
            }
            panel.Children.Add(grid);
        }

        if (state.Actions.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var a in state.Actions)
            {
                var button = new Button
                {
                    Content = a.Label,
                    Style = (Style)Application.Current.FindResource(a.Primary ? "FlyoutPrimaryButton" : "FlyoutButton"),
                };
                var id = a.Id;
                // Host ids (Restart tile, Open log) become commands; the rest go to the connector.
                button.Click += async (_, _) =>
                {
                    try { CloseOpen(); await commands.InvokeAsync(runtime, id); }
                    catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
                };
                actions.Children.Add(button);
            }
            panel.Children.Add(actions);
        }

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

    static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>
    /// Centers the window above the anchor in physical pixels, 8 DIP above the taskbar, clamped to the work area
    /// of the monitor holding the anchor. The window starts at Opacity 0; moving it onto the target monitor may
    /// change its DPI (WPF rescales synchronously), so we re-measure and re-apply until the DPI is stable (max 3 passes).
    /// </summary>
    static void Position(Window window, FrameworkElement anchor)
    {
        var anchorSource = PresentationSource.FromVisual(anchor) as HwndSource;
        if (anchorSource?.CompositionTarget is null) return;
        var anchorScale = anchorSource.CompositionTarget.TransformToDevice.M11;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var anchorWidthPx = anchor.ActualWidth * anchorScale;

        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(anchorSource.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var haveWork = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info);

        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            int x = (int)(topLeft.X + anchorWidthPx / 2 - w / 2.0);
            int y = (int)(topLeft.Y - 4 * anchorScale - 8 * anchorScale - h); // anchor sits 4 DIP below the taskbar top
            if (haveWork)
            {
                x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - w));
                y = Math.Max(y, info.rcWork.Top);
            }
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);

            var after = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(after - dpi) < 0.001) break;
            dpi = after;
        }
    }
}
