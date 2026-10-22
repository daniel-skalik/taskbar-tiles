using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>One per display: a borderless topmost strip sized to its visible tiles, placed in the free zone.</summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
    readonly List<TileView> _views = [];
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId)
    {
        DisplayId = displayId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        Content = _panel;
        Theme.ApplyTo(Resources, light: false);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open config file" };
        open.Click += (_, _) => OpenConfigRequested?.Invoke(this, EventArgs.Empty);
        var exit = new MenuItem { Header = "Exit Taskbar Tiles" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        ContextMenu = menu;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public string DisplayId { get; }
    public event EventHandler? OpenConfigRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    public void SetTiles(IReadOnlyList<TileRuntime> runtimes)
    {
        if (_views.Select(v => v.Runtime).SequenceEqual(runtimes)) return;

        foreach (var v in _views) v.Detach();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var rt in runtimes)
        {
            TileView view;
            try
            {
                view = new TileView(rt, () => _light ?? false) { Margin = new Thickness(0, 0, GapDip, 0) };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tile '{rt.TileId}' failed to render: {ex}");
                continue;
            }
            _views.Add(view);
            _panel.Children.Add(view);
        }
        Reflow();
    }

    public void Apply(TaskbarInfo info)
    {
        _info = info;
        if (_light != info.IsLightTheme)
        {
            _light = info.IsLightTheme;
            Theme.ApplyTo(Resources, info.IsLightTheme);
        }
        Reflow();
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var widths = _views.Select(v => v.Width).ToList();
        var count = _info.Hidden || zone.IsEmpty ? 0 : FitTiles.Count(widths, zone.Width / scale, GapDip);
        for (var i = 0; i < _views.Count; i++)
        {
            _views[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            _views[i].Margin = new Thickness(0, 0, i < count - 1 ? GapDip : 0, 0);
        }

        if (count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        var totalDip = widths.Take(count).Sum() + GapDip * (count - 1);
        var w = (int)Math.Ceiling(totalDip * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
