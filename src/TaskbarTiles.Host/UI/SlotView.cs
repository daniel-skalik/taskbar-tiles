using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// One slot on a bar: a single tile, or a stack that shows one member at a time with position dots. A stack rotates
/// every "rotate", pauses while hovered, cycles on the mouse wheel, and lets an urgent member take over until it is clicked.
/// In edit mode it gets a dashed outline, a shield that keeps clicks away from the tile (the bar turns them into drags),
/// a × button and, when its tiles may be resized, a grip on its outer edge: dragging it previews the new width, letting
/// go asks for it to be saved, a double-click asks for the kinds' widths again.
/// </summary>
public sealed class SlotView : Grid
{
    static readonly Brush Accent = Frozen(Color.FromRgb(0x4C, 0x7C, 0xE0));
    static readonly Brush AccentFill = Frozen(Color.FromArgb(0x2E, 0x7A, 0xA2, 0xF7));

    readonly List<TileView> _members = [];
    readonly StackPanel? _dots;
    readonly DispatcherTimer? _timer;
    readonly Rectangle _outline;
    readonly Border _shield;
    readonly Border _grip;
    double? _previewWidth;
    bool _resizing, _gripOnLeft;
    double _resizeStartX, _resizeStartWidth;
    bool _compact;
    bool _editing;
    bool _dropHighlight;

    public SlotView(BarSlot slot, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        Slot = slot;
        try
        {
            // The slot subscribes before its tiles: the runtime wraps the whole invocation in one catch,
            // so a tile whose render throws must not stop the slot from seeing an urgent member.
            if (slot.IsStack) foreach (var runtime in slot.Members) runtime.StateChanged += OnMemberStateChanged;

            foreach (var runtime in slot.Members)
            {
                // Popups anchor to the slot: the shown member can change while a click awaits its flyout.
                var view = new TileView(runtime, isLightTheme, commands, log) { FlyoutAnchor = this };
                _members.Add(view);
                Children.Add(view);
            }

            if (slot.IsStack)
            {
                Rotation = new StackRotation(slot.Members.Count);
                _dots = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2),
                    IsHitTestVisible = false,
                };
                for (var i = 0; i < slot.Members.Count; i++)
                {
                    var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1.5, 0, 1.5, 0) };
                    dot.SetResourceReference(Shape.FillProperty, "TileForeground");
                    _dots.Children.Add(dot);
                }
                Children.Add(_dots);

                for (var i = 0; i < slot.Members.Count; i++) Rotation.SetUrgent(i, slot.Members[i].State.Urgent);

                MouseEnter += (_, _) => Rotation.Hovered = true;
                MouseLeave += (_, _) => Rotation.Hovered = false;
                MouseWheel += OnWheel;
                // After the member's own click handling: the click that opens the urgent member's popup also releases the stack.
                AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Changed(Rotation.Acknowledge())), handledEventsToo: true);
            }

            _outline = new Rectangle
            {
                RadiusX = 6,
                RadiusY = 6,
                StrokeThickness = 1,
                StrokeDashArray = [3, 2],
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _shield = new Border { Background = Frozen(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll, Visibility = Visibility.Collapsed };
            RemoveButton = new Button
            {
                Content = "×",
                ToolTip = slot.IsStack ? "Remove the tile shown from this stack" : "Remove tile",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Visibility = Visibility.Collapsed,
                Style = TryFindResource("RemoveButton") as Style,
            };
            RemoveButton.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            var notch = new Rectangle { Width = 2, Height = 14, RadiusX = 1, RadiusY = 1, Opacity = 0.7, IsHitTestVisible = false };
            notch.SetResourceReference(Shape.FillProperty, "TileForeground");
            _grip = new Border
            {
                Width = 8,
                Background = Frozen(Color.FromArgb(1, 0, 0, 0)), // hit-testable but invisible
                Cursor = Cursors.SizeWE,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Drag to resize · double-click for the default width",
                Visibility = Visibility.Collapsed,
                Child = notch,
            };
            _grip.MouseLeftButtonDown += OnGripDown;
            _grip.MouseMove += OnGripMove;
            _grip.MouseLeftButtonUp += OnGripUp;
            _grip.LostMouseCapture += (_, _) => { if (_resizing) { _resizing = false; ClearPreview(); } };
            Children.Add(_outline);
            Children.Add(_shield);
            Children.Add(_grip);
            Children.Add(RemoveButton);

            ApplyWidths();
            ShowCurrent();
            ApplyEditLook();

            // Last: nothing after it can throw and leave a running timer behind.
            if (Rotation is not null && slot.Members.Count > 1)
            {
                _timer = new DispatcherTimer { Interval = slot.Rotate!.Value };
                _timer.Tick += (_, _) => Changed(Rotation.Tick());
                _timer.Start();
            }
        }
        catch
        {
            Detach(); // a constructor that throws must not leave a subscription or a running timer behind
            throw;
        }
    }

    /// <summary>The × was clicked: remove this slot, or for a stack the member shown.</summary>
    public event EventHandler? RemoveRequested;

    /// <summary>The grip moved: <see cref="FullWidth"/> is the width being previewed, so the bar should lay out again.</summary>
    public event EventHandler? ResizePreview;

    /// <summary>The grip was let go at a new width (whole DIP), or double-clicked (null: the kinds' widths).</summary>
    public event EventHandler<double?>? ResizeCommitted;

    /// <summary>The grip sits on the left edge: on the right-side bar, which grows leftwards from the tray.</summary>
    public bool GripOnLeft
    {
        set
        {
            _gripOnLeft = value;
            _grip.HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
    }

    /// <summary>Drops the previewed width, e.g. when saving it failed.</summary>
    public void ClearPreview()
    {
        if (_previewWidth is null) return;
        _previewWidth = null;
        ApplyWidths();
        ResizePreview?.Invoke(this, EventArgs.Empty);
    }

    void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // not a drag of the whole slot
        if (e.ClickCount == 2)
        {
            ResizeCommitted?.Invoke(this, null);
            return;
        }
        _resizing = true;
        _resizeStartX = ScreenX(e);
        _resizeStartWidth = FullWidth;
        _grip.CaptureMouse();
    }

    void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var dx = ScreenX(e) - _resizeStartX;
        var width = Math.Round(Math.Clamp(_resizeStartWidth + (_gripOnLeft ? -dx : dx), Slot.MinWidth, Slot.MaxWidth));
        if (width == FullWidth) return;
        _previewWidth = width;
        ApplyWidths();
        ResizePreview?.Invoke(this, EventArgs.Empty);
    }

    void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        e.Handled = true;
        _resizing = false;
        _grip.ReleaseMouseCapture();
        if (_previewWidth is { } width && width != Slot.FullWidth) ResizeCommitted?.Invoke(this, width);
        else ClearPreview();
    }

    // In DIP from the screen's left edge: the bar window itself moves and grows while the tile is resized.
    double ScreenX(MouseEventArgs e) => PointToScreen(e.GetPosition(this)).X / VisualTreeHelper.GetDpi(this).DpiScaleX;

    internal Button RemoveButton { get; }

    internal Border ResizeGrip => _grip;

    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            if (Rotation is not null) Rotation.Suspended = value;
            if (!value) _dropHighlight = false;
            ApplyEditLook();
        }
    }

    /// <summary>While dragging in edit mode: this slot is where the dragged one would be stacked.</summary>
    public bool DropHighlight
    {
        get => _dropHighlight;
        set
        {
            if (_dropHighlight == value) return;
            _dropHighlight = value && _editing;
            ApplyEditLook();
        }
    }

    public BarSlot Slot { get; private set; }

    /// <summary>Null for a single tile.</summary>
    public StackRotation? Rotation { get; }

    /// <summary>Index of the member shown (always 0 for a single tile).</summary>
    public int CurrentMember => Rotation?.Index ?? 0;

    public TileView Current => _members[CurrentMember];

    public double FullWidth => _previewWidth ?? Slot.FullWidth;

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            ApplyWidths();
            ApplyEditLook();
        }
    }

    /// <summary>Where member popups appear; null (the default) places them above the slot.</summary>
    public FrameworkElement? FlyoutAnchor
    {
        set { foreach (var m in _members) m.FlyoutAnchor = value ?? this; }
    }

    /// <summary>For the popup's gear: what opens a member's Settings panel, or null when its kind has no settings.</summary>
    public Func<TileRuntime, Action?>? SettingsOpener
    {
        set { foreach (var m in _members) m.SettingsOpener = value; }
    }

    /// <summary>Takes a slot with the same content at a new index (after a reorder), keeping rotation state.</summary>
    public void Retarget(BarSlot slot)
    {
        if (!slot.SameContent(Slot)) throw new ArgumentException("Retarget needs a slot with the same content.", nameof(slot));
        Slot = slot;
        if (!_resizing) _previewWidth = null; // the saved width has arrived
        ApplyWidths();
        ApplyEditLook();
    }

    public void Detach()
    {
        _timer?.Stop();
        foreach (var runtime in Slot.Members) runtime.StateChanged -= OnMemberStateChanged;
        foreach (var m in _members) m.Detach();
    }

    void OnMemberStateChanged(object? sender, EventArgs e)
    {
        if (Rotation is null || sender is not TileRuntime runtime) return;
        for (var i = 0; i < Slot.Members.Count; i++)
            if (ReferenceEquals(Slot.Members[i], runtime)) Changed(Rotation.SetUrgent(i, runtime.State.Urgent));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Rotation is null) return;
        Changed(Rotation.Step(e.Delta < 0 ? +1 : -1)); // wheel down shows the next member
        e.Handled = true;
    }

    void Changed(bool indexChanged)
    {
        if (indexChanged) ShowCurrent();
    }

    void ApplyWidths()
    {
        var width = _compact ? LayoutEngine.CompactWidth : FullWidth;
        Width = width;
        foreach (var m in _members)
        {
            m.Width = width;
            m.Compact = _compact;
        }
    }

    void ApplyEditLook()
    {
        var edit = _editing ? Visibility.Visible : Visibility.Collapsed;
        _outline.Visibility = edit;
        _shield.Visibility = edit;
        RemoveButton.Visibility = edit;
        _grip.Visibility = _editing && Slot.Resizable && !_compact ? Visibility.Visible : Visibility.Collapsed;
        if (_dropHighlight)
        {
            _outline.Stroke = Accent;
            _outline.Fill = AccentFill;
            _outline.StrokeDashArray = null;
        }
        else
        {
            _outline.SetResourceReference(Shape.StrokeProperty, "TileForeground");
            _outline.Fill = null;
            _outline.StrokeDashArray = [3, 2];
        }
        _outline.Opacity = _dropHighlight ? 1 : 0.6;
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    void ShowCurrent()
    {
        var current = CurrentMember;
        for (var i = 0; i < _members.Count; i++)
            _members[i].Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
        if (_dots is null) return;
        for (var i = 0; i < _dots.Children.Count; i++)
            ((UIElement)_dots.Children[i]).Opacity = i == current ? 1 : 0.3;
    }
}
