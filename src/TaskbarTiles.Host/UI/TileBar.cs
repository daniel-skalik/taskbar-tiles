using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
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
    readonly ITileLog _log;
    readonly TileCommands _commands;
    IReadOnlyList<TileRuntime> _requested = [];
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId, ITileLog log, TileCommands commands)
    {
        _log = log;
        _commands = commands;
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

        // Bar-wide menu for the gaps between tiles; each tile gets its own menu in SetTiles.
        ContextMenu = BuildMenu(null);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the tiles' menus.
        ContextMenuOpening += (_, _) => { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); };

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
    public event EventHandler? ExitRequested;

    /// <summary>Shows the first member of each slot (stacks render in full later in milestone 3).</summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots) => SetTiles(slots.Select(s => s.Members[0]).ToList());

    /// <summary>Replaces the tiles unless the same runtimes are already shown in the same order.</summary>
    void SetTiles(IReadOnlyList<TileRuntime> runtimes)
    {
        if (_requested.SequenceEqual(runtimes)) return;
        _requested = runtimes.ToList();

        foreach (var v in _views) v.Detach();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var rt in runtimes)
        {
            TileView view;
            try
            {
                view = new TileView(rt, () => _light ?? false, _commands, _log) { Margin = new Thickness(0, 0, GapDip, 0) };
                view.ContextMenu = BuildMenu(rt);
            }
            catch (Exception ex)
            {
                _log.Error($"Tile '{rt.TileId}' could not be shown", ex);
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

    ContextMenu BuildMenu(TileRuntime? tile)
    {
        var menu = new ContextMenu();
        foreach (var entry in TileMenu.Entries(onTile: tile is not null))
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Header };
            if (entry.Tile is { } command && tile is not null)
                item.Click += async (_, _) => await _commands.ExecuteAsync(tile, command); // never throws
            else if (entry.Bar == BarCommand.OpenConfig)
                item.Click += (_, _) => _commands.OpenConfig();
            else if (entry.Bar == BarCommand.Exit)
                item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(item);
        }
        return menu;
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
