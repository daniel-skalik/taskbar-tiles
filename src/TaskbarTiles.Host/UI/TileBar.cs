using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One per display: a borderless topmost strip in the free zone. LayoutEngine decides per slot: full, compact, or
/// inside the » overflow popup. Reflows on every free-zone or slot change. In edit mode it shows dashed tiles that can
/// be dragged, a × on each, and + and Done at the end; every change is saved to config.json right away.
/// </summary>
public sealed class TileBar : Window, ITileBarView
{
    const double GapDip = 6;
    const double InsetDip = 4; // tiles sit 4 DIP inside the taskbar's top and bottom edges
    const double AddWidth = 40;
    const double DoneWidth = 56;
    const double DragThreshold = 4;

    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, InsetDip, 0, InsetDip) };
    readonly List<SlotView> _views = [];
    readonly Button _overflowButton;
    readonly Button _addButton;
    readonly Button _doneButton;
    readonly ITileLog _log;
    readonly TileCommands _commands;
    readonly ILayoutEditor _editor;
    readonly TileBarRegistry _bars;
    IReadOnlyList<BarSlot> _slots = [];
    List<SlotView> _overflowed = [];
    StackPanel? _overflowPanel; // set while the overflow popup is open
    IntPtr _hwnd;
    TaskbarInfo? _info;
    bool? _light;
    bool _editing;
    IntPtr _menuForeground; // the foreground window before a menu took it for the bar
    IntPtr _previousForeground; // the window that was in front when edit mode activated this bar
    SlotView? _dragSource;
    SlotView? _dropTarget; // the slot of this bar a drop would stack onto
    TileBar? _hoverBar; // while this bar drags: the bar under the pointer, which shows the drop preview
    Point _dragStart;
    bool _dragging;

    public TileBar(string displayId, ITileLog log, TileCommands commands, ILayoutEditor editor, TileBarRegistry bars)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
        _bars = bars;
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

        _overflowButton = BarButton("»", LayoutEngine.OverflowWidth, "More tiles");
        _overflowButton.Click += (_, _) => ToggleOverflow();
        _addButton = BarButton("+", AddWidth, "Add a tile");
        _addButton.FontSize = 18;
        _addButton.Click += (_, _) => ShowGallery();
        _doneButton = BarButton("Done", DoneWidth, "Leave edit mode (Esc)");
        _doneButton.Click += (_, _) => _editor.SetEditMode(false);

        // Bar-wide menu for the gaps between tiles; each slot gets its own menu in CreateView.
        ContextMenu = new ContextMenu();
        FillBarMenu(ContextMenu);
        // Outside edit mode the bar is WS_EX_NOACTIVATE, so it never becomes the foreground window and the menu
        // never sees outside clicks. Bring the bar to the foreground while a menu is open.
        // ContextMenuOpening bubbles, so this also covers the slots' menus.
        ContextMenuOpening += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            var foreground = GetForegroundWindow();
            if (foreground != _hwnd) _menuForeground = foreground;
            SetForegroundWindow(_hwnd);
        };
        ContextMenuClosing += (_, _) => ForgetMenuForegroundAfterClick();
        PreviewKeyDown += (_, e) =>
        {
            if (!_editing || e.Key != Key.Escape) return;
            e.Handled = true;
            _editor.SetEditMode(false);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetActivatable(_editing);
            Reflow();
        };

        // Create the HWND now (no Show) so _hwnd and ex-styles exist; Reflow alone owns Show/Hide.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        bars.Add(this);
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
        EndDrag();
        ClearDropPreview(); // another bar may be dragging over this one
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

    /// <summary>
    /// Edit mode. The bar can be activated meanwhile (so Esc reaches it); popups close and stacks stop rotating.
    /// </summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        EndDrag();
        ClearDropPreview();
        FlyoutWindow.CloseOpen();
        foreach (var view in _views) view.Editing = editing;
        SetActivatable(editing);
        if (!editing) RestoreForeground();
        FillBarMenu(ContextMenu);
        Reflow();
    }

    /// <summary>Gives activation back to the window that was in front before edit mode, if this bar still holds it.</summary>
    void RestoreForeground()
    {
        var previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (_hwnd == IntPtr.Zero || GetForegroundWindow() != _hwnd) return;
        if (previous != IntPtr.Zero && previous != _hwnd && IsWindow(previous)) SetForegroundWindow(previous);
    }

    Button BarButton(string content, double width, string tooltip) => new()
    {
        Content = content,
        Width = width,
        ToolTip = tooltip,
        Style = TryFindResource("BarButton") as Style,
    };

    void SetActivatable(bool activatable)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
        ex = activatable ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    SlotView CreateView(BarSlot slot)
    {
        var view = new SlotView(slot, () => _light ?? false, _commands, _log) { ContextMenu = new ContextMenu(), Editing = _editing };
        // Built when it opens, for the member shown at that moment.
        view.ContextMenuOpening += (_, _) => FillSlotMenu(view.ContextMenu, view);
        // Tiles in the overflow popup live in another window, so their menu events never reach the bar.
        view.ContextMenuClosing += (_, _) => ForgetMenuForegroundAfterClick();
        view.MouseLeftButtonDown += (_, e) => BeginDrag(view, e);
        view.RemoveRequested += (_, _) =>
        {
            var current = view.Slot;
            var member = view.CurrentMember;
            Save(() => _editor.RemoveTile(DisplayId, current.Index, current.IsStack ? member : null));
        };
        return view;
    }

    void FillBarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(null, _editing)) menu.Items.Add(Item(entry, null, 0));
    }

    void FillSlotMenu(ContextMenu menu, SlotView view)
    {
        // Every slot of this display, overflowed ones included: the stack pickers offer all its plain tiles. A tile in the
        // error look has no title (its "Error" says nothing), so the picker shows its id.
        var slots = _slots
            .Select(s => new MenuSlot(s.Index, s.IsStack, s.Members.Select(m => m.TileId).ToList(), s.Members.FirstOrDefault() is { LastError: null } first ? first.State.Title : null))
            .ToList();
        var state = new SlotMenuState(
            IsStack: view.Slot.IsStack,
            RotationPaused: view.Rotation?.Paused == true,
            StackChoices: TileMenu.StackChoices(slots, view.Slot.Index));
        var member = view.CurrentMember; // the member shown when the menu opened, even if the stack rotates meanwhile
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(state, _editing)) menu.Items.Add(Item(entry, view, member));
    }

    Control Item(MenuEntry entry, SlotView? view, int member)
    {
        if (entry.IsSeparator) return new Separator();
        // Tile titles may hold "_", which a menu header would take as an access key marker.
        var item = new MenuItem { Header = entry.Header.Replace("_", "__"), IsEnabled = entry.Enabled };
        if (entry.Submenu is { } submenu)
        {
            foreach (var child in submenu) item.Items.Add(Item(child, view, member));
        }
        else if (entry.Tile is { } command && view is not null)
        {
            var runtime = view.Slot.Members[member];
            item.Click += async (_, _) => await _commands.ExecuteAsync(runtime, command); // never throws
        }
        else if (entry.Layout is { } layout && view is not null)
            item.Click += (_, _) => Run(layout, view, member, entry.Choice);
        else if (entry.Bar == BarCommand.EditLayout)
            item.Click += (_, _) => EnterEditMode();
        else if (entry.Bar == BarCommand.DoneEditing)
            item.Click += (_, _) => _editor.SetEditMode(false);
        else if (entry.Bar == BarCommand.OpenConfig)
            item.Click += (_, _) => _commands.OpenConfig();
        else if (entry.Bar == BarCommand.Exit)
            item.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        return item;
    }

    // The saved window belongs to one menu. ContextMenuClosing may fire before or after the chosen item's Click
    // handler (EnterEditMode reads it there), so clear from a queued callback that runs after both.
    void ForgetMenuForegroundAfterClick() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _menuForeground = IntPtr.Zero);

    void EnterEditMode()
    {
        var menuForeground = _menuForeground; // take it: it must not outlive this menu choice
        _menuForeground = IntPtr.Zero;
        _editor.SetEditMode(true); // every bar, this one included
        if (!IsVisible) return;
        _previousForeground = menuForeground;
        Activate();
        Focus();
    }

    void Run(LayoutCommand command, SlotView view, int member, StackChoice? choice)
    {
        var slot = view.Slot;
        switch (command)
        {
            case LayoutCommand.PauseRotation when view.Rotation is { } rotation:
                rotation.Paused = true;
                break;
            case LayoutCommand.ResumeRotation when view.Rotation is { } rotation:
                rotation.Paused = false;
                break;
            case LayoutCommand.CreateStack or LayoutCommand.AddToStack when choice is not null:
                // The chosen tile joins the clicked slot: a new stack [clicked, chosen] where the clicked tile is,
                // or the end of the clicked stack.
                Edit(l => LayoutEdits.StackInto(l, choice.SlotIndex, slot.Index));
                break;
            case LayoutCommand.RemoveFromStack:
                Edit(l => LayoutEdits.RemoveFromStack(l, slot.Index, member));
                break;
            case LayoutCommand.RemoveTile:
                Save(() => _editor.RemoveTile(DisplayId, slot.Index, slot.IsStack ? member : null));
                break;
        }
    }

    void Edit(Func<DisplayLayout, DisplayLayout> edit) => Save(() => _editor.EditLayout(DisplayId, edit));

    /// <summary>Saves a layout change; the new slots arrive through SetSlots. Shows a notice when nothing was saved.</summary>
    void Save(Func<ConfigError?> save)
    {
        ConfigError? error;
        try { error = save(); }
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

    /// <summary>The + button: tiles this display does not show yet, a new tile per plugin, and the sample scripts.</summary>
    void ShowGallery()
    {
        IReadOnlyList<GalleryItem> items;
        try { items = _editor.GalleryFor(DisplayId); }
        catch (Exception ex)
        {
            _log.Error("Could not list the tile gallery", ex);
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(FlyoutWindow.Text("Add a tile", 14, FontWeights.SemiBold, "TileForeground"));
        var hint = FlyoutWindow.Text(items.Count == 0
            ? "Nothing to add: every tile is on this display already."
            : "It is added at the end of this display's tiles.", 12, FontWeights.Normal, "TileDim");
        hint.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(hint);

        var grid = new UniformGrid { Columns = 2 };
        foreach (var item in items)
        {
            var content = new StackPanel();
            var title = FlyoutWindow.Text(item.Title, 12.5, FontWeights.SemiBold, "TileForeground");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var subtitle = FlyoutWindow.Text(item.Subtitle, 11.5, FontWeights.Normal, "TileDim");
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(title);
            content.Children.Add(subtitle);
            var button = new Button
            {
                Content = content,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 6, 8, 6),
                Style = TryFindResource("FlyoutButton") as Style,
            };
            button.Click += (_, _) =>
            {
                FlyoutWindow.CloseOpen();
                Save(() => _editor.AddFromGallery(DisplayId, item));
            };
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        FlyoutWindow.ShowPanel(_addButton, panel, _light ?? false, _log);
    }

    void BeginDrag(SlotView view, MouseButtonEventArgs e)
    {
        if (!_editing || !ReferenceEquals(view.Parent, _panel)) return;
        _dragSource = view;
        _dragStart = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragSource is null) return;
        var p = e.GetPosition(this);
        if (!_dragging && (Math.Abs(p.X - _dragStart.X) > DragThreshold || Math.Abs(p.Y - _dragStart.Y) > DragThreshold))
        {
            _dragging = true;
            _dragSource.Opacity = 0.35;
        }
        if (_dragging) HoverAt(PointToScreen(p));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragSource is null) return;
        var dragged = _dragging ? _dragSource : null;
        var screen = PointToScreen(e.GetPosition(this));
        var bar = dragged is null ? null : _bars.BarAt(screen);
        var drop = bar?.DropAt(screen, ReferenceEquals(bar, this) ? dragged : null);
        EndDrag();
        if (dragged is null || bar is null || drop is not { } d) return; // a click, or released outside every bar: cancel
        var from = dragged.Slot.Index;
        var toDisplay = bar.DisplayId;
        Save(() => _editor.MoveToDisplay(DisplayId, from, toDisplay, d.Target, d.Position));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    /// <summary>Shows where the slot would land on the bar under the pointer: this one or another display's.</summary>
    void HoverAt(Point screen)
    {
        var bar = _bars.BarAt(screen);
        if (!ReferenceEquals(bar, _hoverBar))
        {
            _hoverBar?.ClearDropPreview();
            _hoverBar = bar;
        }
        bar?.ShowDropPreview(screen, ReferenceEquals(bar, this) ? _dragSource : null);
    }

    /// <summary>True when this bar is in edit mode, on screen, and its window holds the screen point (physical pixels).</summary>
    internal bool HoldsPoint(Point screen) =>
        _editing && IsVisible && NativeMethods.Rect(_hwnd) is { } rect
        && rect.Contains((int)Math.Floor(screen.X), (int)Math.Floor(screen.Y));

    /// <summary>
    /// Where a slot released at the screen point lands on this bar: before, into or after one of its shown slots, or
    /// (null, After) to append when it shows none. <paramref name="dragged"/> is the slot itself when the drag started here.
    /// </summary>
    internal (int? Target, DropPosition Position) DropAt(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        return DropGeometry.ToDrop(spot, candidates.Select(v => v.Slot.Index).ToList());
    }

    /// <summary>While a drag (from this bar or another) is over this bar: highlights the slot a drop would stack onto.</summary>
    internal void ShowDropPreview(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        SetDropTarget(spot.Into ? candidates[spot.Index] : null);
    }

    internal void ClearDropPreview() => SetDropTarget(null);

    /// <summary>The shown slots a drop can land by (all but the dragged one) and the spot under the screen point.</summary>
    (List<SlotView> Candidates, DropSpot Spot) SpotAt(Point screen, SlotView? dragged)
    {
        var candidates = _views.Where(v => ReferenceEquals(v.Parent, _panel) && !ReferenceEquals(v, dragged)).ToList();
        // Layout offsets, not rendered positions: render transforms must not move the drop spots.
        var spans = candidates.Select(v => new SlotSpan(VisualTreeHelper.GetOffset(v).X, v.ActualWidth)).ToList();
        return (candidates, DropGeometry.SpotAt(spans, _panel.PointFromScreen(screen).X));
    }

    void SetDropTarget(SlotView? target)
    {
        if (ReferenceEquals(target, _dropTarget)) return;
        if (_dropTarget is not null) _dropTarget.DropHighlight = false;
        _dropTarget = target;
        if (target is not null) target.DropHighlight = true;
    }

    void EndDrag()
    {
        var source = _dragSource;
        _dragSource = null; // first: releasing the capture raises LostMouseCapture, which calls back here
        _dragging = false;
        if (source is not null) source.Opacity = 1;
        var hover = _hoverBar;
        _hoverBar = null;
        hover?.ClearDropPreview();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var zone = _info.FreeZone.Zone;
        var scale = _info.DpiScale;
        var hidden = _info.Hidden || zone.IsEmpty;
        var chrome = _editing ? AddWidth + GapDip + DoneWidth : 0;

        var layout = hidden
            ? null
            : LayoutEngine.Compute(_views.Select(v => new LayoutItem(v.FullWidth, v.Slot.Pinned)).ToList(),
                zone.Width / scale - (_editing ? chrome + GapDip : 0), GapDip);
        Arrange(layout, showChrome: _editing && !hidden);

        var widthDip = (layout?.Width ?? 0) + (_editing && !hidden ? chrome + (layout?.Width > 0 ? GapDip : 0) : 0);
        if (widthDip <= 0)
        {
            CloseOverflow();
            if (IsVisible) Hide();
            return;
        }

        var w = (int)Math.Ceiling(widthDip * scale);
        var clamped = _editing && w > zone.Width;
        if (clamped) w = zone.Width; // never cover the taskbar icons
        _panel.HorizontalAlignment = clamped ? HorizontalAlignment.Right : HorizontalAlignment.Left; // keep + and Done visible
        var x = _info.FreeZone.IconsCentered ? zone.Left : zone.Right - w;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Puts full and compact slots on the bar, then the » tile when anything overflows, then + and Done in edit mode.</summary>
    void Arrange(LayoutResult? layout, bool showChrome)
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
        if (showChrome)
        {
            shown.Add(_addButton);
            shown.Add(_doneButton);
        }

        if (_dragSource is not null && !shown.Contains(_dragSource)) EndDrag(); // the dragged slot went into overflow
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
        EndDrag();
        ClearDropPreview();
        _bars.Remove(this);
        CloseOverflow();
        foreach (var v in _views) v.Detach();
        base.OnClosed(e);
    }
}
