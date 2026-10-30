using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// The popup above a tile, the overflow popup and the tile gallery. Only one is open at a time; it closes when it
/// loses focus. On Windows 11 it has the acrylic backdrop (<see cref="Backdrop"/>), elsewhere a solid background.
/// </summary>
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

    /// <summary>A tile's popup; <paramref name="openSettings"/> (null when the tile has no settings) runs from the gear.</summary>
    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log, Action? openSettings = null) =>
        ShowPanel(anchor, Build(state, runtime, commands, log, openSettings), light, log, WidthDip, tint: state.Tint);

    /// <summary>
    /// Shows any content in the popup frame above <paramref name="anchor"/>: 320 DIP wide by default, or as wide as the
    /// content with a null width. <paramref name="onClosed"/> runs when it closes, for whatever reason.
    /// <paramref name="tint"/> ("#RRGGBB") colors the frame.
    /// </summary>
    public static void ShowPanel(FrameworkElement anchor, UIElement content, bool light, ITileLog log,
        double? width = WidthDip, Action? onClosed = null, string? tint = null)
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
            SizeToContent = width is null ? SizeToContent.WidthAndHeight : SizeToContent.Height,
            UseLayoutRounding = true,
            Opacity = 0, // hidden until positioned on the target monitor at the right DPI
        };
        if (width is { } w) window.Width = w;
        Theme.ApplyTo(window.Resources, light);
        var acrylic = Backdrop.Supported && TryBackdrop(window, light, log);
        window.Content = Frame(content, acrylic, tint);
        window.Deactivated += (_, _) => { if (_open == window) CloseOpen(); };
        if (onClosed is not null)
            window.Closed += (_, _) =>
            {
                try { onClosed(); }
                catch (Exception ex) { log.Error("Closing a popup failed", ex); }
            };
        window.Loaded += (_, _) =>
        {
            var positioned = false;
            try { positioned = Position(window, anchor); }
            catch (Exception ex) { log.Error("Flyout positioning failed", ex); }
            if (positioned)
            {
                if (acrylic) Backdrop.Uncloak(window);
                else window.Opacity = 1;
            }
            else
            {
                // Never show a popup that could not be placed (for example its anchor left the visual tree).
                log.Warn("Flyout closed: its anchor is not on screen");
                if (_open == window) CloseOpen(); else window.Close();
            }
        };
        _open = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Turns on acrylic; the window then stays hidden by cloaking instead of Opacity.</summary>
    static bool TryBackdrop(Window window, bool light, ITileLog log)
    {
        try
        {
            window.Opacity = 1;
            Backdrop.Apply(window, light);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Acrylic backdrop failed; the popup gets a solid background", ex);
            return false;
        }
    }

    static UIElement Build(FlyoutState state, TileRuntime runtime, TileCommands commands, ITileLog log, Action? openSettings)
    {
        var panel = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (Theme.Decode(state.Image, 112) is { } img)
            header.Children.Add(new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0),
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(state.Title, 15, FontWeights.SemiBold, "TileForeground");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(title);
        if (!string.IsNullOrEmpty(state.Subtitle))
        {
            var subtitle = Text(state.Subtitle, 12, FontWeights.Normal, "TileDim");
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            titles.Children.Add(subtitle);
        }
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        if (openSettings is not null)
        {
            var gear = new Button
            {
                Content = "", // Settings
                ToolTip = "Settings",
                Style = (Style)Application.Current.FindResource("IconButton"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, -6, -6, 0),
            };
            gear.Click += (_, _) =>
            {
                try { CloseOpen(); openSettings(); }
                catch (Exception ex) { log.Error($"Opening the settings of tile '{runtime.TileId}' failed", ex); }
            };
            Grid.SetColumn(gear, 2);
            header.Children.Add(gear);
        }
        panel.Children.Add(header);

        if (state.Rows.Count > 0)
        {
            var rows = new StackPanel();
            for (var i = 0; i < state.Rows.Count; i++) rows.Children.Add(Row(state.Rows[i], first: i == 0));
            var card = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 14, 0, 0),
                Child = rows,
            };
            card.SetResourceReference(Border.BackgroundProperty, "FlyoutCard");
            panel.Children.Add(card);
        }

        if (state.Actions.Count > 0)
        {
            // Buttons share the width: one button spans the popup.
            var actions = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
            for (var i = 0; i < state.Actions.Count; i++)
            {
                var a = state.Actions[i];
                var button = new Button
                {
                    Content = a.Label,
                    Style = (Style)Application.Current.FindResource(a.Primary ? "FlyoutPrimaryButton" : "FlyoutButton"),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                    Padding = new Thickness(12, 6, 12, 6),
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

        return panel;
    }

    /// <summary>One line of the rows card: dot, text, and the value on the right; a hairline above all but the first.</summary>
    static Border Row(FlyoutRow row, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (row.Icon is { } icon)
            grid.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                Background = Theme.ColorFor(icon.Color), VerticalAlignment = VerticalAlignment.Center,
            });
        var text = Text(row.Text, 13, FontWeights.Normal, "TileForeground");
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (row.Value is not null)
        {
            var value = Text(row.Value, 12.5, FontWeights.SemiBold, "TileForeground");
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular); // digits line up from row to row
            value.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
        }
        var line = new Border
        {
            Padding = new Thickness(0, 9, 0, 9),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Child = grid,
        };
        line.SetResourceReference(Border.BorderBrushProperty, "FlyoutDivider");
        return line;
    }

    /// <summary>
    /// With acrylic, DWM draws the corners, border and shadow and the frame only adds the tint; without it the frame is
    /// the solid rounded panel. The tint washes over either, strongest at the top.
    /// </summary>
    static Border Frame(UIElement content, bool acrylic, string? tint)
    {
        var inner = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Background = TintBrush(tint) ?? Brushes.Transparent,
            Child = content,
        };
        var border = new Border { CornerRadius = new CornerRadius(8), Child = inner };
        if (!acrylic)
        {
            border.BorderThickness = new Thickness(1);
            border.SetResourceReference(Border.BackgroundProperty, "FlyoutBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        }
        return border;
    }

    static Brush? TintBrush(string? tint)
    {
        if (tint is null) return null;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(tint); }
        catch (FormatException) { return null; }
        var brush = new LinearGradientBrush(Color.FromArgb(0x8C, color.R, color.G, color.B), Color.FromArgb(0x1F, color.R, color.G, color.B), 90);
        brush.Freeze();
        return brush;
    }

    /// <summary>A text line in the popup's colors: brushKey is a Theme key such as "TileForeground" or "TileDim".</summary>
    public static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.NoWrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>
    /// Centers the window above the anchor in physical pixels, 8 DIP above the taskbar, clamped to the work area
    /// of the monitor holding the anchor. The window starts hidden; moving it onto the target monitor may
    /// change its DPI (WPF rescales synchronously), so we re-measure and re-apply until the DPI is stable (max 3 passes).
    /// </summary>
    static bool Position(Window window, FrameworkElement anchor)
    {
        if (SourceOf(anchor) is not { } anchorSource) return false;
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
        return true;
    }

    /// <summary>The window source of an anchor, or null when the anchor is in no window.</summary>
    internal static HwndSource? SourceOf(Visual anchor) =>
        PresentationSource.FromVisual(anchor) is HwndSource { CompositionTarget: not null } source ? source : null;
}
