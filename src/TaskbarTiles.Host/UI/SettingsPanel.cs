using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.UI;

/// <summary>
/// The settings dialog's content, shown in the popup frame above a tile: one row per field of the tile's kind, then
/// Reset to defaults and Save. Text, number and duration boxes hold the tile's own value; empty means the kind's
/// default, shown dim behind the box. Save stays off while a value does not check out.
/// </summary>
public static class SettingsPanel
{
    /// <summary>
    /// <paramref name="save"/> gets the values (null: use the default) and returns true when they were saved;
    /// <paramref name="close"/> closes the popup (after a save, and on Esc).
    /// </summary>
    public static FrameworkElement Build(TileSettingsForm form, byte[]? image, Func<IReadOnlyDictionary<string, JsonNode?>, bool> save, Action close)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(form, image));

        var rows = form.Fields.Select(f => new Row(f, Default(form, f), Own(form, f))).ToList();
        var saveButton = new Button
        {
            Content = "Save", Style = (Style)Application.Current.FindResource("FlyoutPrimaryButton"),
            Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
        };
        void Revalidate() => saveButton.IsEnabled = rows.All(r => r.Validate());
        // The fields share one card, a hairline between them, like the rows of a tile's popup.
        var fields = new StackPanel();
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Changed += Revalidate;
            rows[i].Element.BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
            fields.Children.Add(rows[i].Element);
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 14, 0, 0),
            Child = fields,
        };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutCard");
        panel.Children.Add(card);

        var reset = new Button
        {
            Content = "Reset to defaults", Style = (Style)Application.Current.FindResource("FlyoutButton"),
            Margin = new Thickness(0), Padding = new Thickness(12, 6, 12, 6),
        };
        reset.Click += (_, _) => { foreach (var row in rows) row.Reset(); };
        saveButton.Click += (_, _) =>
        {
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var (value, error) = TileFields.Check(row.Field, row.Input());
                if (error is not null) return;
                values[row.Field.Key] = value;
            }
            if (save(values)) close();
        };
        var buttons = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(reset);
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);

        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            close();
        };
        Revalidate();
        return panel;
    }

    /// <summary>
    /// The tile's image (a gear badge without one), the kind's name and the tile id, laid out like a tile popup's header.
    /// </summary>
    static Grid Header(TileSettingsForm form, byte[]? image)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gear = new TextBlock
        {
            Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        gear.SetResourceReference(TextBlock.ForegroundProperty, "TileForeground");
        var badge = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0), Child = gear,
        };
        if (Theme.Decode(image, 112) is { } img)
        {
            badge.Child = null;
            badge.Background = new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        }
        else badge.SetResourceReference(Border.BackgroundProperty, "FlyoutCard");
        header.Children.Add(badge);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = FlyoutWindow.Text(form.KindName, 15, FontWeights.SemiBold, "TileForeground");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(title);
        var id = FlyoutWindow.Text($"Settings · {form.TileId}", 12, FontWeights.Normal, "TileDim");
        id.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(id);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        return header;
    }

    static JsonElement? Default(TileSettingsForm form, TileField f) =>
        form.Defaults.ValueKind == JsonValueKind.Object && form.Defaults.TryGetProperty(f.Key, out var v) ? v : null;

    static JsonElement? Own(TileSettingsForm form, TileField f) =>
        form.Own.ValueKind == JsonValueKind.Object && form.Own.TryGetProperty(f.Key, out var v) ? v : null;

    /// <summary>One field: label, input, help and error line.</summary>
    sealed class Row
    {
        readonly string _default;
        readonly TextBlock _error;
        readonly Action<string> _set;

        public Row(TileField field, JsonElement? fallback, JsonElement? own)
        {
            Field = field;
            _default = TileFields.Display(fallback);
            var current = own is null ? "" : TileFields.Display(own);

            var stack = new StackPanel();
            // The label line; a toggle's switch sits at its right end.
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = FlyoutWindow.Text(field.Label, 13, FontWeights.SemiBold, "TileForeground");
            label.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(label);
            stack.Children.Add(top);

            switch (field.Type)
            {
                case FieldType.Toggle:
                {
                    var box = new CheckBox
                    {
                        IsChecked = (current.Length > 0 ? current : _default) == "true",
                        Style = (Style)Application.Current.FindResource("FlyoutToggle"),
                    };
                    box.Checked += (_, _) => Changed?.Invoke();
                    box.Unchecked += (_, _) => Changed?.Invoke();
                    Input = () => box.IsChecked == true ? "true" : "false";
                    _set = text => box.IsChecked = text == "true";
                    Grid.SetColumn(box, 1);
                    top.Children.Add(box);
                    break;
                }
                case FieldType.Choice:
                {
                    // One pill per choice; the ticked one is the value.
                    var pills = new WrapPanel { Margin = new Thickness(0, 8, 0, -6) };
                    var buttons = new List<RadioButton>();
                    var selected = current.Length > 0 ? current : _default;
                    foreach (var choice in field.Choices!)
                    {
                        var pill = new RadioButton
                        {
                            Content = choice, IsChecked = choice == selected,
                            Style = (Style)Application.Current.FindResource("FlyoutPill"),
                        };
                        pill.Checked += (_, _) => Changed?.Invoke();
                        buttons.Add(pill);
                        pills.Children.Add(pill);
                    }
                    Input = () => buttons.FirstOrDefault(b => b.IsChecked == true)?.Content as string ?? "";
                    _set = text => { foreach (var b in buttons) b.IsChecked = (string)b.Content == text; };
                    stack.Children.Add(pills);
                    break;
                }
                case FieldType.Pick:
                {
                    // Nothing saved yet (the default) shows every value ticked; the first change saves the ticked list.
                    var picks = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                    var boxes = new List<CheckBox>();
                    var saved = own is { ValueKind: JsonValueKind.Array } list
                        ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.CurrentCultureIgnoreCase)
                        : null;
                    var isDefault = saved is null;
                    var setting = false;
                    foreach (var choice in field.Choices ?? [])
                    {
                        var box = new CheckBox
                        {
                            Content = choice, IsChecked = saved?.Contains(choice) ?? true, Margin = new Thickness(0, 4, 0, 4),
                            Style = (Style)Application.Current.FindResource("FlyoutCheckBox"),
                        };
                        RoutedEventHandler changed = (_, _) =>
                        {
                            if (setting) return;
                            isDefault = false;
                            Changed?.Invoke();
                        };
                        box.Checked += changed;
                        box.Unchecked += changed;
                        boxes.Add(box);
                        picks.Children.Add(box);
                    }
                    if (boxes.Count == 0) picks.Children.Add(FlyoutWindow.Text("Nothing to choose from yet.", 12, FontWeights.Normal, "TileDim"));
                    Input = () => isDefault ? "" : new JsonArray(boxes.Where(b => b.IsChecked == true)
                        .Select(b => (JsonNode?)JsonValue.Create((string)b.Content)).ToArray()).ToJsonString();
                    _set = _ =>
                    {
                        setting = true;
                        foreach (var box in boxes) box.IsChecked = true;
                        setting = false;
                        isDefault = true;
                        Changed?.Invoke();
                    };
                    stack.Children.Add(picks);
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = current, Style = (Style)Application.Current.FindResource("FlyoutTextBox") };
                    // The default shows dim behind the box while it is empty.
                    var placeholder = FlyoutWindow.Text(_default, 13, FontWeights.Normal, "TileDim");
                    placeholder.Margin = new Thickness(11, 0, 9, 0); // the box's padding and border
                    placeholder.VerticalAlignment = VerticalAlignment.Center;
                    placeholder.IsHitTestVisible = false;
                    placeholder.Visibility = current.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    box.TextChanged += (_, _) =>
                    {
                        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                        Changed?.Invoke();
                    };
                    Input = () => box.Text;
                    _set = text => box.Text = text;
                    var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                    grid.Children.Add(box);
                    grid.Children.Add(placeholder);
                    stack.Children.Add(grid);
                    break;
                }
            }

            if (field.Help is { } help)
            {
                var helpText = FlyoutWindow.Text(help, 11.5, FontWeights.Normal, "TileDim");
                helpText.TextWrapping = TextWrapping.Wrap;
                helpText.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(helpText);
            }
            _error = new TextBlock
            {
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Foreground = Theme.ColorFor(TileColor.Bad), Visibility = Visibility.Collapsed,
            };
            stack.Children.Add(_error);
            Element = new Border { Padding = new Thickness(0, 12, 0, 12), Child = stack };
            Element.SetResourceReference(Border.BorderBrushProperty, "FlyoutDivider");
        }

        public TileField Field { get; }

        /// <summary>The field's line in the card; the panel sets the hairline above it.</summary>
        public Border Element { get; }
        public Func<string> Input { get; } = () => "";
        public event Action? Changed;

        /// <summary>Shows the input's error, if any; true when it checks out.</summary>
        public bool Validate()
        {
            var (_, error) = TileFields.Check(Field, Input());
            _error.Text = error ?? "";
            _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            return error is null;
        }

        /// <summary>Back to the kind's default: an empty box, the default's state for a toggle and a choice, every pick ticked.</summary>
        public void Reset() => _set(Field.Type is FieldType.Toggle or FieldType.Choice ? _default : "");
    }
}
