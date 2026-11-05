using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    const int MaxInputChars = 1000;
    const double IndentDip = 24; // a checkbox glyph (14) plus its gap (10)
    static Window? _open;

    static bool _holding;

    /// <summary>
    /// Runs <paramref name="dialog"/> (a Windows dialog opened from the popup) without the popup closing when the dialog
    /// takes the focus, then gives the popup the focus back.
    /// </summary>
    public static T HoldOpenWhile<T>(Func<T> dialog)
    {
        _holding = true;
        try { return dialog(); }
        finally
        {
            _holding = false;
            _open?.Activate();
        }
    }

    public static void CloseOpen()
    {
        var w = _open;
        _open = null;
        w?.Close();
    }

    /// <summary>A tile's popup; <paramref name="openSettings"/> (null when the tile has no settings) runs from the gear.</summary>
    public static void ShowAbove(FrameworkElement anchor, FlyoutState state, TileRuntime runtime, bool light,
        TileCommands commands, ITileLog log, Action? openSettings = null)
    {
        var popup = new TilePopup(anchor, runtime, commands, log, openSettings);
        ShowPanel(anchor, popup.Build(state), light, log, WidthDip, tint: state.Tint);
        popup.Window = _open;
        popup.FocusInput();
    }

    /// <summary>
    /// Puts new content into an open popup's frame (and its tint), then places it again above its anchor: the height
    /// may have changed, and the popup grows upward from the taskbar.
    /// </summary>
    static void Replace(Window window, FrameworkElement anchor, UIElement content, string? tint, ITileLog log)
    {
        if (window.Content is not Border { Child: Border inner }) return;
        inner.Child = content;
        inner.Background = TintBrush(tint) ?? Brushes.Transparent;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_open != window) return;
            var positioned = false;
            try { positioned = Position(window, anchor); }
            catch (Exception ex) { log.Error("Flyout positioning failed", ex); }
            if (positioned) return;
            log.Warn("Flyout closed: its anchor is not on screen");
            CloseOpen();
        });
    }

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
        window.Deactivated += (_, _) => { if (_open == window && !_holding) CloseOpen(); };
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

    /// <summary>
    /// One tile popup: builds its content and runs its actions. Without <see cref="FlyoutState.StayOpen"/> an action
    /// closes the popup first, as always. With it the popup stays, ignores clicks and Enter until the action is done,
    /// then asks the tile for its popup again and shows that in place; it closes if there is none.
    /// </summary>
    sealed class TilePopup(FrameworkElement anchor, TileRuntime runtime, TileCommands commands, ITileLog log, Action? openSettings)
    {
        public Window? Window { get; set; }
        Control? _input; // a TextBox, or a PasswordBox for a secret input
        ScrollViewer? _scroll; // the rows card's, when the popup has rows
        bool _busy;

        public void FocusInput()
        {
            var box = _input;
            box?.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
        }

        Task Send(string id, string? input) =>
            input is null ? commands.InvokeAsync(runtime, id) : commands.InvokeInputAsync(runtime, id, input);

        async void Run(FlyoutState state, string id, string? input)
        {
            if (!state.StayOpen || id.StartsWith("host:", StringComparison.Ordinal))
            {
                try { CloseOpen(); await Send(id, input); }
                catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
                return;
            }
            if (_busy) return;
            _busy = true;
            var window = Window;
            var content = window?.Content is Border { Child: Border { Child: UIElement c } } ? c : null;
            var replaced = false;
            var placeholder = _input?.Tag;
            // Feedback while the action runs (it can take seconds): dim the popup, ignore the mouse, say so in the box.
            if (content is not null) { content.Opacity = 0.6; content.IsHitTestVisible = false; }
            if (_input is not null) _input.Tag = "Working…";
            try
            {
                await Send(id, input);
                if (window is null || _open != window) return;
                // Let the dispatcher run what the action queued (a failed connector shows its error popup in place) before
                // asking the tile for its popup, so the outcome does not depend on message ordering.
                await Dispatcher.Yield();
                var next = await runtime.GetFlyoutAsync();
                if (_open != window) return;
                if (next.Outcome == FlyoutOutcome.Popup && next.Flyout is { } fresh)
                {
                    var offset = _scroll?.VerticalOffset ?? 0; // ticking a task far down must not jump back to the top
                    Replace(window, anchor, Build(fresh), fresh.Tint, log);
                    replaced = true; // only now: a Build that throws must not leave the old content dimmed
                    if (_scroll is { } scroll && offset > 0)
                        scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroll.ScrollToVerticalOffset(offset));
                    FocusInput();
                }
                else CloseOpen();
            }
            catch (Exception ex) { log.Error($"Flyout action '{id}' on tile '{runtime.TileId}' failed", ex); }
            finally
            {
                _busy = false;
                if (!replaced && content is not null && _open == window)
                {
                    content.Opacity = 1; content.IsHitTestVisible = true;
                    if (_input is not null && placeholder is not null) _input.Tag = placeholder;
                }
            }
        }

        /// <summary>Enter in an input box: sends the trimmed text (never logged). Empty text, or a running action, keeps the box as is.</summary>
        void Submit(KeyEventArgs e, string raw, Action clear, FlyoutState state, string action)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = raw.Trim();
            if (text.Length == 0 || _busy) return;
            if (action.StartsWith("host:", StringComparison.Ordinal)) return; // host ids are buttons only, never input
            clear();
            Run(state, action, text);
        }

        public UIElement Build(FlyoutState state)
        {
            _input = null;
            _scroll = null;
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
                    Content = "\uE713", // Settings
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

            if (state.Input is { Secret: true } secret)
            {
                // Dots instead of text. Tag is the FlyoutPasswordBox style's placeholder, emptied while the box holds
                // text (a PasswordBox has no bindable text for a trigger). The text goes to the connector only, never the log.
                var box = new PasswordBox
                {
                    Tag = secret.Placeholder ?? "", MaxLength = MaxInputChars, Margin = new Thickness(0, 14, 0, 0),
                    Style = (Style)Application.Current.FindResource("FlyoutPasswordBox"),
                };
                box.PasswordChanged += (_, _) => box.Tag = box.Password.Length == 0 ? secret.Placeholder ?? "" : "";
                box.KeyDown += (_, e) =>
                {
                    Submit(e, box.Password, box.Clear, state, secret.Action);
                };
                _input = box;
                panel.Children.Add(box);
            }
            else if (state.Input is { } input)
            {
                // Tag is the FlyoutTextBox style's placeholder.
                var box = new TextBox
                {
                    Tag = input.Placeholder ?? "", MaxLength = MaxInputChars, Margin = new Thickness(0, 14, 0, 0),
                    Style = (Style)Application.Current.FindResource("FlyoutTextBox"),
                };
                box.KeyDown += (_, e) =>
                {
                    Submit(e, box.Text, box.Clear, state, input.Action);
                };
                _input = box;
                panel.Children.Add(box);
            }

            if (state.Rows.Count > 0)
            {
                var rows = new StackPanel();
                for (var i = 0; i < state.Rows.Count; i++) rows.Children.Add(Row(state.Rows[i], first: i == 0, id => Run(state, id, null)));
                panel.Children.Add(RowsCard(rows, out _scroll));
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
                    button.Click += (_, _) => Run(state, id, null);
                    actions.Children.Add(button);
                }
                panel.Children.Add(actions);
            }

            return panel;
        }
    }

    /// <summary>
    /// One line of the rows card: dot, text, and the value on the right; a hairline above all but the first. A row with
    /// an action highlights under the mouse and runs it on click.
    /// </summary>
    /// <summary>About ten rows; a popup with more scrolls its rows card while the header, box and buttons stay.</summary>
    internal const double RowsMaxHeight = 420;

    /// <summary>The rounded card that holds the rows, scrolling once they are taller than <see cref="RowsMaxHeight"/>.</summary>
    internal static Border RowsCard(UIElement rows, out ScrollViewer scroll)
    {
        // The rows keep the card's side padding; the scroll bar sits in the right padding, clear of the text.
        var inner = new Border { Padding = new Thickness(12, 0, 12, 0), Child = rows };
        scroll = new ScrollViewer
        {
            MaxHeight = RowsMaxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = inner,
        };
        if (Application.Current?.TryFindResource("FlyoutScrollViewer") is Style style) scroll.Style = style;
        SmoothScroll.Attach(scroll);
        var card = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 14, 0, 0), Child = scroll };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutCard");
        return card;
    }

    /// <summary>
    /// Mouse-wheel scrolling that glides instead of jumping 48 DIP a notch: each notch moves a target, and every frame
    /// covers part of the way there until it lands.
    /// </summary>
    internal static class SmoothScroll
    {
        const double PixelsPerNotch = 48; // WPF's own wheel step: 3 lines of 16
        const double Fraction = 0.3;      // of the remaining distance per frame

        /// <summary>The next offset on the way from <paramref name="current"/> to <paramref name="target"/>.</summary>
        public static double Step(double current, double target)
        {
            var next = current + (target - current) * Fraction;
            return Math.Abs(target - next) < 0.5 ? target : next;
        }

        public static void Attach(ScrollViewer scroll)
        {
            double target = 0;
            var gliding = false;

            void Frame(object? sender, EventArgs e)
            {
                var next = Step(scroll.VerticalOffset, target);
                scroll.ScrollToVerticalOffset(next);
                if (next == target) Stop();
            }
            void Stop()
            {
                if (!gliding) return;
                gliding = false;
                CompositionTarget.Rendering -= Frame;
            }

            scroll.PreviewMouseWheel += (_, e) =>
            {
                if (scroll.ScrollableHeight <= 0) return; // nothing to scroll: let the wheel go on
                e.Handled = true;
                var from = gliding ? target : scroll.VerticalOffset;
                target = Math.Clamp(from - e.Delta / 120.0 * PixelsPerNotch, 0, scroll.ScrollableHeight);
                if (gliding) return;
                gliding = true;
                CompositionTarget.Rendering += Frame;
            };
            scroll.Unloaded += (_, _) => Stop(); // a redraw or close replaces the viewer mid-glide
        }
    }

    /// <summary>A row icon: its shape when the glyph names one (<see cref="Theme.RowShape"/>), else a colored dot.</summary>
    static FrameworkElement RowIcon(TileIcon icon)
    {
        var mark = Theme.RowShape(icon.Glyph, icon.Color)
            ?? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.ColorFor(icon.Color) };
        mark.VerticalAlignment = VerticalAlignment.Center;
        return mark;
    }

    internal static Border Row(FlyoutRow row, bool first, Action<string> invoke)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (row.Check is { } check)
        {
            var box = new TextBlock
            {
                Text = check ? "\uE73A" : "\uE739", // ticked / empty checkbox
                FontFamily = GlyphFont, FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            box.SetResourceReference(TextBlock.ForegroundProperty, check ? "TileDim" : "TileForeground");
            grid.Children.Add(box);
        }
        else if (row.Icon is { } icon)
        {
            var mark = RowIcon(icon);
            mark.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(mark);
        }
        var text = Text(row.Text, 13, FontWeights.Normal, "TileForeground");
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        if (row.Check == true)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");
        }
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
        if (row.EndIcon is { } end)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = RowIcon(end);
            mark.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(mark, 3);
            grid.Children.Add(mark);
        }
        var line = new Border
        {
            Padding = new Thickness(0, 9, 0, 9),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            // Nested rows (subtasks) start under the text of the row above: one checkbox width per level, hairline included.
            Margin = new Thickness(IndentDip * Math.Clamp(row.Indent, 0, FlyoutRow.MaxIndent), 0, 0, 0),
        };
        line.SetResourceReference(Border.BorderBrushProperty, "FlyoutDivider");
        if (row.Action is not { } action)
        {
            line.Child = grid;
            return line;
        }
        // The grid goes into the hover border only: WPF refuses an element that already has a parent.

        // The highlight reaches the card's edges; the hairline stays on the outer border.
        var hover = new Border
        {
            Margin = new Thickness(-8, 0, -8, 0), Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = grid,
        };
        line.Child = hover;
        hover.MouseEnter += (_, _) => hover.SetResourceReference(Border.BackgroundProperty, "ButtonHover");
        hover.MouseLeave += (_, _) => hover.Background = Brushes.Transparent;
        hover.MouseLeftButtonUp += (_, _) => invoke(action);
        line.Padding = new Thickness(0, 3, 0, 3);
        grid.Margin = new Thickness(0, 6, 0, 6);
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
