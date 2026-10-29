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
    public static FrameworkElement Build(TileSettingsForm form, Func<IReadOnlyDictionary<string, JsonNode?>, bool> save, Action close)
    {
        var panel = new StackPanel();
        panel.Children.Add(FlyoutWindow.Text(form.KindName, 14, FontWeights.SemiBold, "TileForeground"));
        var id = FlyoutWindow.Text(form.TileId, 12, FontWeights.Normal, "TileDim");
        id.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(id);

        var rows = form.Fields.Select(f => new Row(f, Default(form, f), Own(form, f))).ToList();
        var saveButton = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("FlyoutPrimaryButton") };
        void Revalidate() => saveButton.IsEnabled = rows.All(r => r.Validate());
        foreach (var row in rows)
        {
            row.Changed += Revalidate;
            panel.Children.Add(row.Element);
        }

        var reset = new Button { Content = "Reset to defaults", Style = (Style)Application.Current.FindResource("FlyoutButton") };
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
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
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

            var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var label = FlyoutWindow.Text(field.Label, 12.5, FontWeights.SemiBold, "TileForeground");
            label.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(label);

            switch (field.Type)
            {
                case FieldType.Toggle:
                {
                    var box = new CheckBox { Content = "On", IsChecked = (current.Length > 0 ? current : _default) == "true" };
                    box.SetResourceReference(Control.ForegroundProperty, "TileForeground");
                    box.Checked += (_, _) => Changed?.Invoke();
                    box.Unchecked += (_, _) => Changed?.Invoke();
                    Input = () => box.IsChecked == true ? "true" : "false";
                    _set = text => box.IsChecked = text == "true";
                    stack.Children.Add(box);
                    break;
                }
                case FieldType.Choice:
                {
                    var combo = new ComboBox { ItemsSource = field.Choices, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
                    combo.SelectedItem = field.Choices!.FirstOrDefault(c => c == (current.Length > 0 ? current : _default));
                    combo.SelectionChanged += (_, _) => Changed?.Invoke();
                    Input = () => combo.SelectedItem as string ?? "";
                    _set = text => combo.SelectedItem = field.Choices!.FirstOrDefault(c => c == text);
                    stack.Children.Add(combo);
                    break;
                }
                case FieldType.Pick:
                {
                    // Nothing saved yet (the default) shows every value ticked; the first change saves the ticked list.
                    var picks = new StackPanel();
                    var boxes = new List<CheckBox>();
                    var saved = own is { ValueKind: JsonValueKind.Array } list
                        ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.CurrentCultureIgnoreCase)
                        : null;
                    var isDefault = saved is null;
                    var setting = false;
                    foreach (var choice in field.Choices ?? [])
                    {
                        var box = new CheckBox { Content = choice, IsChecked = saved?.Contains(choice) ?? true, Margin = new Thickness(0, 2, 0, 2) };
                        box.SetResourceReference(Control.ForegroundProperty, "TileForeground");
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
                    var box = new TextBox { Text = current, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1) };
                    box.SetResourceReference(Control.BackgroundProperty, "TileFill");
                    box.SetResourceReference(Control.ForegroundProperty, "TileForeground");
                    box.SetResourceReference(Control.BorderBrushProperty, "TileOutline");
                    box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TileForeground");
                    // The default shows dim behind the box while it is empty.
                    var placeholder = FlyoutWindow.Text(_default, 13, FontWeights.Normal, "TileDim");
                    placeholder.Margin = new Thickness(8, 5, 8, 0);
                    placeholder.IsHitTestVisible = false;
                    placeholder.Visibility = current.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    box.TextChanged += (_, _) =>
                    {
                        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                        Changed?.Invoke();
                    };
                    Input = () => box.Text;
                    _set = text => box.Text = text;
                    var grid = new Grid();
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
            Element = stack;
        }

        public TileField Field { get; }
        public FrameworkElement Element { get; }
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
