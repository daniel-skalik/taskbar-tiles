using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Renders one tile's TileState, full or compact (icon only), and turns clicks into flyouts or actions.</summary>
public partial class TileView : UserControl
{
    static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily MonogramFont = new("Segoe UI");

    readonly Func<bool> _isLightTheme;
    readonly TileCommands _commands;
    readonly ITileLog _log;
    bool _urgent;
    bool _compact;
    bool _clickInFlight;
    SolidColorBrush? _pulse;

    public TileView(TileRuntime runtime, Func<bool> isLightTheme, TileCommands commands, ITileLog log)
    {
        InitializeComponent();
        Runtime = runtime;
        _isLightTheme = isLightTheme;
        _commands = commands;
        _log = log;
        Width = runtime.Width;
        MouseLeftButtonUp += OnClick;
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        KeyDown += (_, e) => { if (e.Key == Key.Enter) OnClick(this, null); };
        Render(runtime.State);
        // Subscribe last: a constructor that throws must not leave a subscription behind.
        Runtime.StateChanged += OnStateChanged;
    }

    public TileRuntime Runtime { get; }

    /// <summary>Icon only, with title, subtitle and tooltip in the tooltip. The owner sets the width.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            Render(Runtime.State);
        }
    }

    /// <summary>What the popup is placed above; null means this tile. Tiles in the overflow popup point it at the » tile.</summary>
    public FrameworkElement? FlyoutAnchor { get; set; }

    public void Detach()
    {
        Runtime.StateChanged -= OnStateChanged;
        StopPulse();
    }

    void OnStateChanged(object? sender, EventArgs e) => Render(Runtime.State);

    void Render(TileState s)
    {
        TitleText.Text = s.Title ?? "";
        SubtitleText.Text = s.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(s.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = _compact ? TileText.CompactTooltip(s) : string.IsNullOrEmpty(s.Tooltip) ? null : s.Tooltip;

        var picture = Theme.Decode(s.Image);
        Picture.Source = picture;
        if (picture is null && s.Icon is { } icon)
        {
            IconGlyph.FontFamily = GlyphFont;
            IconGlyph.Text = Theme.Glyph(icon.Glyph);
            IconBackground.Background = Theme.ColorFor(icon.Color);
        }
        else if (picture is null && _compact)
        {
            // A compact tile without an icon still needs something to recognise it by.
            IconGlyph.FontFamily = MonogramFont;
            IconGlyph.Text = TileText.Monogram(s.Title);
            IconBackground.Background = Theme.ColorFor(TileColor.Neutral);
        }
        else
        {
            IconGlyph.Text = "";
            IconBackground.Background = Brushes.Transparent;
        }
        IconHost.Visibility = picture is null && s.Icon is null && !_compact ? Visibility.Collapsed : Visibility.Visible;
        IconHost.Margin = _compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        Root.Padding = _compact ? new Thickness(5, 0, 5, 0) : new Thickness(6, 0, 6, 0); // 5 + 28 + 5 + 2 border = 40 DIP
        TextPanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        ExtrasPanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;

        ButtonPanel.Children.Clear();
        foreach (var b in s.Buttons)
        {
            var button = new Button
            {
                Content = Theme.Glyph(b.Glyph),
                ToolTip = b.Tooltip,
                IsEnabled = b.Enabled,
                Style = (Style)FindResource("IconButton"),
            };
            var id = b.Id;
            // Same path as popup actions: host ids become commands, the rest go to the connector,
            // which drops a click on an action id that is still running.
            button.Click += async (_, _) =>
            {
                try { await _commands.InvokeAsync(Runtime, id); }
                catch (Exception ex) { _log.Error($"Tile '{Runtime.TileId}' button '{id}' failed", ex); }
            };
            ButtonPanel.Children.Add(button);
        }

        RenderSparkline(s.Sparkline);

        if (s.Progress is { } p)
        {
            var done = Math.Clamp(p, 0, 1);
            ProgressDone.Width = new GridLength(done, GridUnitType.Star);
            ProgressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
            ProgressTrack.Visibility = Visibility.Visible;
        }
        else
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
        }

        var wasUrgent = _urgent;
        _urgent = s.Urgent;
        if (_urgent && !wasUrgent) StartPulse();
        else if (!_urgent && wasUrgent) StopPulse();
        UpdateBackground();
    }

    void RenderSparkline(IReadOnlyList<double>? values)
    {
        if (values is not { Count: > 1 })
        {
            SparkHost.Visibility = Visibility.Collapsed;
            return;
        }

        double min = values.Min(), max = values.Max(), range = max - min;
        double w = SparkHost.Width, h = SparkHost.Height;
        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var y = range <= 0 ? h / 2 : h - 2 - (values[i] - min) / range * (h - 4);
            points.Add(new Point(i * w / (values.Count - 1), y));
        }
        Spark.Points = points;
        SparkHost.Visibility = Visibility.Visible;
    }

    void StartPulse()
    {
        var bad = ((SolidColorBrush)Theme.ColorFor(TileColor.Bad)).Color;
        _pulse ??= new SolidColorBrush(Color.FromArgb(0x10, bad.R, bad.G, bad.B));
        Root.Background = _pulse;
        Root.BorderBrush = Theme.ColorFor(TileColor.Bad);
        if (SystemParameters.ClientAreaAnimation)
            _pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x30, bad.R, bad.G, bad.B),
                TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    void StopPulse() => _pulse?.BeginAnimation(SolidColorBrush.ColorProperty, null);

    void UpdateBackground()
    {
        if (_urgent) return; // pulse owns the background while urgent
        Root.SetResourceReference(Border.BackgroundProperty, IsMouseOver ? "TileHover" : "TileFill");
        Root.SetResourceReference(Border.BorderBrushProperty, "TileOutline");
    }

    async void OnClick(object sender, MouseButtonEventArgs? e)
    {
        if (e?.Handled == true || _clickInFlight) return;
        _clickInFlight = true;
        try
        {
            var result = await Runtime.GetFlyoutAsync();
            switch (result.Outcome)
            {
                case FlyoutOutcome.NoPopup:
                    await Runtime.InvokeActionAsync(TileActions.Click);
                    break;
                case FlyoutOutcome.Popup:
                    FlyoutWindow.ShowAbove(FlyoutAnchor ?? this, result.Flyout!, Runtime, _isLightTheme(), _commands, _log);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Tile '{Runtime.TileId}' click failed", ex);
        }
        finally
        {
            _clickInFlight = false;
        }
    }
}
