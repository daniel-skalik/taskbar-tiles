using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One per display: a borderless topmost strip in the free zone. LayoutEngine decides per slot: full, compact, or
/// inside the » overflow popup. Reflows on every free-zone or slot change.
/// </summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;
    const double InsetDip = 4; // tiles sit 4 DIP inside the taskbar's top and bottom edges

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, InsetDip, 0, InsetDip) };
    readonly List<SlotView> _views = [];
    readonly Button _overflowButton;
    readonly ITileLog _log;
    readonly TileCommands _commands;
    readonly ILayoutEditor _editor;
    IReadOnlyList<BarSlot> _slots = [];
    List<SlotView> _overflowed = [];
    StackPanel? _overflowPanel; // set while the overflow popup is open
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
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

        _overflowButton = new Button
        {
            Content = "»",
            Width = LayoutEngine.OverflowWidth,
            ToolTip = "More tiles",
            Style = TryFindResource("BarButton") as Style,
        };
        _overflowButton.Click += (_, _) => ToggleOverflow();

        // Bar-wide menu for the gaps between tiles; each slot gets its own menu in CreateView.
        ContextMenu = new ContextMenu();
        FillBarMenu(ContextMenu);
        // The bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the slots' menus.
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

    /// <summary>
    /// Shows the slots. Views whose slot content is unchanged are kept (with their rotation state), even when they moved.
    /// </summary>
    public void SetSlots(IReadOnlyList<BarSlot> slots)
    {
        if (_slots.SequenceEqual(slots)) return;
        _slots = slots.ToList();
        CloseOverflow(); // the popup may hold views that move or go away

        var pool = _views.ToList();
        _views.Clear();
        _panel.Children.Clear();
        foreach (var slot in slots)
        {
            var view = pool.FirstOrDefault(v => v.Slot.SameContent(slot));
            if (view is not null)
            {
                pool.Remove(view);
                view.Retarget(slot);
            }
            else
            {
                try { view = CreateView(slot); }
                catch (Exception ex)
                {
                    _log.Error($"Slot {slot.Index} ({string.Join(", ", slot.Members.Select(m => m.TileId))}) could not be shown", ex);
                    continue;
                }
            }
            _views.Add(view);
        }
        foreach (var unused in pool) unused.Detach();
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

    SlotView CreateView(BarSlot slot)
    {
        var view = new SlotView(slot, () => _light ?? false, _commands, _log) { ContextMenu = new ContextMenu() };
        // Built when it opens, for the member shown at that moment.
        view.ContextMenuOpening += (_, _) => FillSlotMenu(view.ContextMenu, view);
        return view;
    }

    void FillBarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(null)) menu.Items.Add(Item(entry, null, 0));
    }

    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        var position = _views.IndexOf(view);
        var state = new SlotMenuState(
            IsFirst: position <= 0,
            IsLast: position == _views.Count - 1,
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            CanStack: !view.Slot.IsStack && _views.Count > 1);
        var member = view.CurrentMember; // the member shown when the menu opened, even if the stack rotates meanwhile
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(state)) menu.Items.Add(Item(entry, view, member));
    }

    Control Item(MenuEntry entry, SlotView? view, int member)
    {
        if (entry.IsSeparator) return new Separator();
        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
        if (entry.Tile is { } command && view is not null)
        {
            var runtime = view.Slot.Members[member];
            item.Click += async (_, _) => await _commands.ExecuteAsync(runtime, command); // never throws
        }
        else if (entry.Layout is { } layout && view is not null)
            item.Click += (_, _) => Run(layout, view, member);
        else if (entry.Bar == BarCommand.OpenConfig)
            item.Click += (_, _) => _commands.OpenConfig();
        else if (entry.Bar == BarCommand.Exit)
            item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        return item;
    }

    void Run(LayoutCommand command, SlotView view, int member)
    {
        var slot = view.Slot;
        var position = _views.IndexOf(view);
        switch (command)
        {
            case LayoutCommand.PauseRotation when view.Rotation is { } rotation:
                rotation.Paused = true;
                break;
            case LayoutCommand.ResumeRotation when view.Rotation is { } rotation:
                rotation.Paused = false;
                break;
            case LayoutCommand.MoveLeft when position > 0:
                var left = _views[position - 1].Slot.Index;
                Edit(l => LayoutEdits.Drop(l, slot.Index, left, DropPosition.Before));
                break;
            case LayoutCommand.MoveRight when position >= 0 && position < _views.Count - 1:
                var right = _views[position + 1].Slot.Index;
                Edit(l => LayoutEdits.Drop(l, slot.Index, right, DropPosition.After));
                break;
            case LayoutCommand.AddToStack:
                Edit(l => LayoutEdits.AddToNearestStack(l, slot.Index));
                break;
            case LayoutCommand.RemoveFromStack:
                Edit(l => LayoutEdits.RemoveFromStack(l, slot.Index, member));
                break;
            case LayoutCommand.RemoveTile:
                Edit(l => LayoutEdits.Remove(l, slot.Index, slot.IsStack ? member : null));
                break;
        }
    }

    /// <summary>Saves a layout change; the new slots arrive through SetSlots. Shows a notice when nothing was saved.</summary>
    void Edit(Func<DisplayLayout, DisplayLayout> edit)
    {
        ConfigError? error;
        try { error = _editor.EditLayout(DisplayId, edit); }
        catch (Exception ex)
        {
            _log.Error("Layout edit failed", ex);
            error = new ConfigError("The layout could not be changed: " + ex.Message);
        }
        if (error is null) return;

        _log.Warn($"Layout not saved: {error}");
        try { NotificationWindow.Show(ConfigNotice.SaveFailedTitle, ConfigNotice.SaveFailedBody(error), _light ?? false, _commands.OpenConfig, _log); }
        catch (Exception ex) { _log.Error("Could not show the save notice", ex); }
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;

        var layout = _info.Hidden || zone.IsEmpty
            ? null
            : LayoutEngine.Compute(_views.Select(v => new LayoutItem(v.FullWidth, v.Slot.Pinned)).ToList(), zone.Width / scale, GapDip);
        Arrange(layout);

        if (layout is null || layout.Width <= 0)
        {
            CloseOverflow();
            if (IsVisible) Hide();
            return;
        }

        var w = (int)Math.Ceiling(layout.Width * scale);
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Puts full and compact slots on the bar, then the » tile when anything overflows.</summary>
    void Arrange(LayoutResult? layout)
    {
        var shown = new List<FrameworkElement>();
        var overflowed = new List<SlotView>();
        for (var i = 0; i < _views.Count; i++)
        {
            var mode = layout?.Modes[i] ?? SlotMode.Overflow;
            if (mode == SlotMode.Overflow)
            {
                overflowed.Add(_views[i]);
                _views[i].FlyoutAnchor = _overflowButton; // popups of overflowed tiles open above the » tile, which is on the bar
                continue;
            }
            _views[i].FlyoutAnchor = null;
            _views[i].Compact = mode == SlotMode.Compact;
            shown.Add(_views[i]);
        }
        if (layout?.ShowOverflow == true) shown.Add(_overflowButton);

        if (!overflowed.SequenceEqual(_overflowed)) CloseOverflow();
        _overflowed = overflowed;

        if (!shown.SequenceEqual(_panel.Children.Cast<FrameworkElement>()))
        {
            _panel.Children.Clear();
            foreach (var element in shown) _panel.Children.Add(element);
        }
        for (var i = 0; i < shown.Count; i++)
            shown[i].Margin = new Thickness(0, 0, i < shown.Count - 1 ? GapDip : 0, 0);
    }

    /// <summary>The » tile: a popup listing the overflowed slots at full size, still clickable, with their menus.</summary>
    void ToggleOverflow()
    {
        if (_overflowPanel is not null)
        {
            CloseOverflow();
            return;
        }
        if (_overflowed.Count == 0 || _info is null) return;

        var tileHeight = Math.Max(24, _info.FreeZone.Zone.Height / _info.DpiScale - 2 * InsetDip);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (var i = 0; i < _overflowed.Count; i++)
        {
            var view = _overflowed[i];
            view.Compact = false;
            view.Height = tileHeight;
            view.Margin = new Thickness(0, 0, 0, i < _overflowed.Count - 1 ? GapDip : 0);
            panel.Children.Add(view);
        }
        _overflowPanel = panel;
        var moved = _overflowed.ToList();
        FlyoutWindow.ShowPanel(_overflowButton, panel, _light ?? false, _log, width: null, onClosed: () =>
        {
            panel.Children.Clear(); // hand the views back so the bar can show them again
            foreach (var view in moved)
            {
                view.Height = double.NaN;
            }
            if (_overflowPanel == panel) _overflowPanel = null;
        });
    }

    void CloseOverflow()
    {
        if (_overflowPanel is not null) FlyoutWindow.CloseOpen();
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseOverflow();
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
