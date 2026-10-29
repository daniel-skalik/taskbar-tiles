using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Taskbar;
using static TaskbarTiles.Host.Taskbar.NativeMethods;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// Two per display, one per side (<see cref="BarSide"/>): a borderless topmost strip in that side's free zone. LayoutEngine decides per slot: full, compact, or
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
    const double DropGapDip = 40; // how far a drop preview slides tiles apart; edit mode keeps this much room after Done
    static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(150);

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
    IntPtr _owner; // this display's taskbar
    int? _shownLeft; // the bar's left edge on screen, null while hidden
    SlotView? _dragSource;
    SlotView? _dropTarget; // the slot of this bar a drop would stack onto
    TileBar? _hoverBar; // while this bar drags: the bar under the pointer, which shows the drop preview
    DragGhost? _ghost;
    Point _grab; // where the pointer holds the dragged slot, in DIP from its top-left corner
    readonly Dictionary<FrameworkElement, double> _slides = []; // elements of this bar slid by a drop preview, and how far
    Point _dragStart;
    bool _dragging;

    public TileBar(string displayId, BarSide side, ITileLog log, TileCommands commands, ILayoutEditor editor, TileBarRegistry bars)
    {
        _log = log;
        _commands = commands;
        _editor = editor;
        _bars = bars;
        DisplayId = displayId;
        Side = side;
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
    public BarSide Side { get; }
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
        if (_owner != info.Handle && _hwnd != IntPtr.Zero)
        {
            // Opening Start lifts the taskbar into a z-band above every topmost window; windows it owns go along.
            _owner = info.Handle;
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, _owner);
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
            Save(() => _editor.RemoveTile(DisplayId, current.Index, current.IsStack ? member : null, Side));
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
        var state = new SlotMenuState(IsStack: view.Slot.IsStack, RotationPaused: view.Rotation?.Paused == true);
        var member = view.CurrentMember; // the member shown when the menu opened, even if the stack rotates meanwhile
        menu.Items.Clear();
        foreach (var entry in TileMenu.Entries(state, _editing, Side)) menu.Items.Add(Item(entry, view, member));
    }

    /// <summary>
    /// The plain tiles of this display the clicked slot can stack with, overflowed ones included. A tile in the error
    /// look has no title (its "Error" says nothing), so it is listed by its id.
    /// </summary>
    IReadOnlyList<StackChoice> StackChoicesFor(SlotView view)
    {
        var slots = _slots
            .Select(s => new MenuSlot(s.Index, s.IsStack, s.Members.Select(m => m.TileId).ToList(), s.Members.FirstOrDefault() is { LastError: null } first ? first.State.Title : null))
            .ToList();
        return TileMenu.StackChoices(slots, view.Slot.Index);
    }

    Control Item(MenuEntry entry, SlotView? view, int member)
    {
        if (entry.IsSeparator) return new Separator();
        // Tile titles may hold "_", which a menu header would take as an access key marker.
        var item = new MenuItem { Header = entry.Header.Replace("_", "__"), IsEnabled = entry.Enabled };
        if (entry.Tile is { } command && view is not null)
        {
            var runtime = view.Slot.Members[member];
            item.Click += async (_, _) => await _commands.ExecuteAsync(runtime, command); // never throws
        }
        else if (entry.Layout is { } layout && view is not null)
            item.Click += (_, _) => Run(layout, view, member);
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

    void Run(LayoutCommand command, SlotView view, int member)
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
            case LayoutCommand.CreateStack or LayoutCommand.AddToStack:
                ShowStackPicker(view);
                break;
            case LayoutCommand.RemoveFromStack:
                Edit(l => LayoutEdits.RemoveFromStack(l, slot.Index, member));
                break;
            case LayoutCommand.MoveToOtherSide:
                var other = Side == BarSide.Right ? BarSide.Left : BarSide.Right;
                Save(() => _editor.MoveToDisplay(DisplayId, slot.Index, DisplayId, null, DropPosition.After, Side, other));
                break;
            case LayoutCommand.RemoveTile:
                Save(() => _editor.RemoveTile(DisplayId, slot.Index, slot.IsStack ? member : null, Side));
                break;
        }
    }

    void Edit(Func<DisplayLayout, DisplayLayout> edit) => Save(() => _editor.EditLayout(DisplayId, edit, Side));

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
        if (Gallery() is not { } items) return;
        var panel = PanelHeader("Add a tile", items.Count == 0
            ? "Nothing to add: every tile is on this display already."
            : "It is added at the end of this display's tiles.");
        panel.Children.Add(Buttons(items.Select(item => (item.Title, item.Subtitle,
            (Action)(() => Save(() => _editor.AddFromGallery(DisplayId, item, side: Side)))))));
        FlyoutWindow.ShowPanel(_addButton, panel, _light ?? false, _log);
    }

    /// <summary>
    /// "Create stack…" on a plain tile and "Add to stack…" on a stack: the panel of the + button, offering this display's
    /// plain tiles (not in the clicked stack) first. The picked tile joins the clicked slot: a new stack where the clicked
    /// tile is, or the end of the clicked stack.
    /// </summary>
    void ShowStackPicker(SlotView view)
    {
        if (Gallery() is not { } items) return;
        var target = view.Slot.Index;
        var clicked = view.Slot.Members[0].TileId;
        var choices = StackChoicesFor(view);
        var panel = PanelHeader(view.Slot.IsStack ? "Add to stack" : "Create a stack", choices.Count + items.Count == 0
            ? "Nothing to stack with: add another tile first."
            : view.Slot.IsStack ? "The tile you pick joins the end of this stack." : "The tile you pick is stacked with this one.");
        if (choices.Count > 0)
        {
            panel.Children.Add(Section("On this display"));
            panel.Children.Add(Buttons(choices.Select(c => (c.Label, c.TileId,
                (Action)(() => Edit(l => StackWith(l, c.TileId, clicked)))))));
        }
        if (items.Count > 0)
        {
            if (choices.Count > 0) panel.Children.Add(Section("Add a tile"));
            panel.Children.Add(Buttons(items.Select(item => (item.Title, item.Subtitle,
                (Action)(() => Save(() => _editor.AddFromGallery(DisplayId, item, stackWith: target, side: Side)))))));
        }
        // Opening the panel closes the » popup, which takes an overflowed tile off screen: anchor to » then.
        FlyoutWindow.ShowPanel(_overflowed.Contains(view) ? _overflowButton : view, panel, _light ?? false, _log);
    }

    /// <summary>
    /// Stacks plain tile <paramref name="chosen"/> into the slot holding <paramref name="clicked"/>, both found by id when
    /// the pick is made: the layout may have changed while the panel was open. Unchanged when either is gone.
    /// </summary>
    static DisplayLayout StackWith(DisplayLayout layout, string chosen, string clicked)
    {
        var slots = layout.Slots.ToList();
        var from = slots.FindIndex(s => s is TileSlot t && t.TileId == chosen);
        var target = slots.FindIndex(s => s switch
        {
            TileSlot t => t.TileId == clicked,
            StackSlot stack => stack.TileIds.Contains(clicked),
            _ => false,
        });
        return from < 0 || target < 0 ? layout : LayoutEdits.StackInto(layout, from, target);
    }

    IReadOnlyList<GalleryItem>? Gallery()
    {
        try { return _editor.GalleryFor(DisplayId); }
        catch (Exception ex)
        {
            _log.Error("Could not list the tile gallery", ex);
            return null;
        }
    }

    static StackPanel PanelHeader(string title, string hint)
    {
        var panel = new StackPanel();
        panel.Children.Add(FlyoutWindow.Text(title, 14, FontWeights.SemiBold, "TileForeground"));
        var text = FlyoutWindow.Text(hint, 12, FontWeights.Normal, "TileDim");
        text.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(text);
        return panel;
    }

    static TextBlock Section(string title)
    {
        var text = FlyoutWindow.Text(title, 12, FontWeights.SemiBold, "TileDim");
        text.Margin = new Thickness(0, 4, 0, 6);
        return text;
    }

    /// <summary>Two columns of buttons with a title and subtitle each; a click closes the panel and runs the action.</summary>
    UniformGrid Buttons(IEnumerable<(string Title, string Subtitle, Action Click)> items)
    {
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (titleText, subtitleText, click) in items)
        {
            var content = new StackPanel();
            var title = FlyoutWindow.Text(titleText, 12.5, FontWeights.SemiBold, "TileForeground");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var subtitle = FlyoutWindow.Text(subtitleText, 11.5, FontWeights.Normal, "TileDim");
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
                click();
            };
            grid.Children.Add(button);
        }
        return grid;
    }

    void BeginDrag(SlotView view, MouseButtonEventArgs e)
    {
        if (!_editing || !ReferenceEquals(view.Parent, _panel)) return;
        _dragSource = view;
        _dragStart = e.GetPosition(this);
        _grab = e.GetPosition(view);
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
            _ghost = CreateGhost(_dragSource); // before the fade, so the ghost shows the tile at full strength
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
        var (toDisplay, toSide) = (bar.DisplayId, bar.Side);
        Save(() => _editor.MoveToDisplay(DisplayId, from, toDisplay, d.Target, d.Position, Side, toSide));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    /// <summary>Shows where the slot would land on the bar under the pointer: this one or another display's.</summary>
    void HoverAt(Point screen)
    {
        _ghost?.MoveTo(screen);
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
    /// Where a slot released at the screen point lands on this bar: before or into one of its shown slots, or
    /// (null, After) to append at the end of the layout when released past the last shown slot or when it shows none.
    /// <paramref name="dragged"/> is the slot itself when the drag started here.
    /// </summary>
    internal (int? Target, DropPosition Position) DropAt(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        return DropGeometry.ToDrop(spot, candidates.Select(v => v.Slot.Index).ToList(), IsLastShown(dragged) ? dragged!.Slot.Index : null);
    }

    /// <summary>
    /// While a drag (from this bar or another) is over this bar: highlights the slot a drop would stack onto, or slides
    /// the slots after the drop gap (and », + and Done) apart. Render transforms only, so nothing reflows.
    /// </summary>
    internal void ShowDropPreview(Point screen, SlotView? dragged)
    {
        var (candidates, spot) = SpotAt(screen, dragged);
        SetDropTarget(spot.Into ? candidates[spot.Index] : null);

        var shown = _panel.Children.OfType<FrameworkElement>().ToList();
        var slots = shown.OfType<SlotView>().ToList();
        var draggedAt = dragged is null ? -1 : slots.IndexOf(dragged);
        var shifts = DropGeometry.Shifts(slots.Count, draggedAt, spot, DropGapDip);
        var chromeShift = DropGeometry.OpensGap(spot, draggedAt) ? DropGapDip : 0;
        foreach (var element in shown)
            SlideTo(element, element is SlotView view ? shifts[slots.IndexOf(view)] : chromeShift);
        // A slot that left the panel mid-preview (into the » popup) must not stay shifted.
        foreach (var gone in _slides.Keys.Where(e => !shown.Contains(e)).ToList())
        {
            if (gone.RenderTransform is TranslateTransform slide)
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.X = 0;
            }
            _slides.Remove(gone);
        }
    }

    /// <summary>Ends a drop preview on this bar: no highlight, and every slid element snaps back without animation.</summary>
    internal void ClearDropPreview()
    {
        SetDropTarget(null);
        foreach (var element in _slides.Keys)
        {
            if (element.RenderTransform is not TranslateTransform slide) continue;
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = 0;
        }
        _slides.Clear();
    }

    /// <summary>Animates the element's horizontal slide to <paramref name="x"/> DIP over 150 ms, unless it is already headed there.</summary>
    void SlideTo(FrameworkElement element, double x)
    {
        if ((_slides.TryGetValue(element, out var current) ? current : 0) == x) return;
        _slides[element] = x;
        if (element.RenderTransform is not TranslateTransform slide) element.RenderTransform = slide = new TranslateTransform();
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, SlideDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    DragGhost? CreateGhost(SlotView view)
    {
        try { return new DragGhost(view, _grab); }
        catch (Exception ex)
        {
            _log.Error("Could not create the drag ghost; dragging without it", ex);
            return null;
        }
    }

    /// <summary>The shown slots a drop can land by (all but the dragged one) and the spot under the screen point.</summary>
    (List<SlotView> Candidates, DropSpot Spot) SpotAt(Point screen, SlotView? dragged)
    {
        var candidates = _views.Where(v => ReferenceEquals(v.Parent, _panel) && !ReferenceEquals(v, dragged)).ToList();
        // Layout offsets, not rendered positions: render transforms must not move the drop spots.
        var spans = candidates.Select(v => new SlotSpan(VisualTreeHelper.GetOffset(v).X, v.ActualWidth)).ToList();
        var x = _panel.PointFromScreen(screen).X;
        // The dragged slot last on this bar: its own place is not a drop target, only the space past it is.
        return (candidates, IsLastShown(dragged)
            ? DropGeometry.SpotAt(spans, x, VisualTreeHelper.GetOffset(dragged!).X + dragged!.ActualWidth)
            : DropGeometry.SpotAt(spans, x));
    }

    /// <summary>True when <paramref name="dragged"/> is shown on this bar (not in the » popup) after every other shown slot.</summary>
    bool IsLastShown(SlotView? dragged) =>
        dragged is not null && ReferenceEquals(dragged.Parent, _panel)
        && _panel.Children.OfType<SlotView>().LastOrDefault() is { } last && ReferenceEquals(last, dragged);

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
        var ghost = _ghost;
        _ghost = null;
        ghost?.Close();
        var hover = _hoverBar;
        _hoverBar = null;
        hover?.ClearDropPreview();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    void Reflow()
    {
        if (_hwnd == IntPtr.Zero || _info is null) return;
        var scale = _info.DpiScale;
        var zone = _info.ZoneFor(Side);
        // With left-aligned icons both sides share the space before the tray: the left bar ends where the right one starts.
        if (Side == BarSide.Left && !_info.FreeZone.IconsCentered && _bars.Sibling(this)?._shownLeft is { } rightBarLeft)
            zone = zone with { Right = Math.Min(zone.Right, rightBarLeft - (int)Math.Round(GapDip * scale)) };
        var hidden = _info.Hidden || zone.IsEmpty;
        // In edit mode the bar keeps DropGapDip of transparent room after Done, so a drop preview can slide tiles apart
        // without resizing the bar or reflowing its tiles.
        var chrome = _editing ? AddWidth + GapDip + DoneWidth + DropGapDip : 0;

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
            Shown(null);
            return;
        }

        var w = (int)Math.Ceiling(widthDip * scale);
        var clamped = _editing && w > zone.Width;
        if (clamped) w = zone.Width; // never cover the taskbar icons
        _panel.HorizontalAlignment = clamped ? HorizontalAlignment.Right : HorizontalAlignment.Left; // keep + and Done visible
        var x = _info.AnchorsRight(Side) ? zone.Right - w : zone.Left;
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, zone.Top, w, zone.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Shown(x);
    }

    /// <summary>Remembers where the bar starts (null: hidden); the right bar's start limits the left bar's space.</summary>
    void Shown(int? left)
    {
        if (_shownLeft == left) return;
        _shownLeft = left;
        if (Side == BarSide.Right && _info is { FreeZone.IconsCentered: false }) _bars.Sibling(this)?.Reflow();
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
        _panel.Margin = new Thickness(0, InsetDip, showChrome ? DropGapDip : 0, InsetDip);

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

        var tileHeight = Math.Max(24, _info.ZoneFor(Side).Height / _info.DpiScale - 2 * InsetDip);
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
