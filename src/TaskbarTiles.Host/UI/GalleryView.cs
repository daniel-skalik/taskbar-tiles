using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Layout;

namespace TaskbarTiles.Host.UI;

/// <summary>The + picker's list: folders, then tile kinds with their icon, description and a check mark when shown here.</summary>
public static class GalleryView
{
    /// <summary>
    /// One picker folder as a list: its subfolders (opened with <paramref name="open"/>), then its choices (run with
    /// <paramref name="pick"/>), each with its icon, its whole description and a check mark when this display shows a
    /// tile of that kind already.
    /// </summary>
    public static StackPanel List(IReadOnlyList<GalleryItem> items, string path, Action<string> open, Action<GalleryItem> pick, ITileLog log)
    {
        var list = new StackPanel();
        var (folders, here) = TileGallery.Open(items, path);
        foreach (var folder in folders)
            list.Children.Add(Row(GlyphIcon("", null), folder.Name, folder.Count == 1 ? "1 tile" : $"{folder.Count} tiles",
                onDisplay: false, chevron: true, () => open(folder.Path)));
        foreach (var item in here)
            list.Children.Add(Row(KindIcon(item, log), item.Title, item.Subtitle, item.OnThisDisplay, chevron: false, () => pick(item)));
        return list;
    }

    static Button Row(FrameworkElement icon, string titleText, string subtitleText, bool onDisplay, bool chevron, Action click)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = FlyoutWindow.Text(titleText, 12.5, FontWeights.SemiBold, "TileForeground");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);
        if (subtitleText.Length > 0)
        {
            var subtitle = FlyoutWindow.Text(subtitleText, 11.5, FontWeights.Normal, "TileDim");
            subtitle.TextWrapping = TextWrapping.Wrap;
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            subtitle.MaxHeight = 32; // two lines
            texts.Children.Add(subtitle);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (onDisplay || chevron)
        {
            var mark = new TextBlock
            {
                Text = chevron ? "" : "", // ChevronRight, CheckMark
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = chevron ? null : "On this taskbar already; you can add another",
            };
            if (onDisplay) mark.Foreground = Theme.ColorFor(TileColor.Ok);
            else mark.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }

        var button = new Button
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 7, 10, 7),
            Style = Application.Current?.TryFindResource("FlyoutButton") as Style,
        };
        button.Click += (_, _) =>
        {
            FlyoutWindow.CloseOpen();
            click();
        };
        return button;
    }

    const double IconSize = 28;

    /// <summary>The kind's icon: its picture (in one color when it says so), its glyph, or the first letter of its name.</summary>
    static FrameworkElement KindIcon(GalleryItem item, ITileLog log)
    {
        if (item.Icon?.Picture is { } path)
        {
            try
            {
                var picture = new BitmapImage();
                picture.BeginInit();
                picture.UriSource = new Uri(path);
                picture.CacheOption = BitmapCacheOption.OnLoad; // no file lock on the kind folder
                picture.DecodePixelWidth = 64;
                picture.EndInit();
                picture.Freeze();
                if (item.Icon.Color is { } color)
                    return new Border
                    {
                        Width = IconSize, Height = IconSize,
                        Background = Theme.ColorFor(color),
                        OpacityMask = new ImageBrush(picture) { Stretch = Stretch.Uniform },
                    };
                return new Image { Source = picture, Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform };
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException or ArgumentException)
            {
                log.Warn($"Could not show the icon {path}: {ex.Message}");
            }
        }
        if (item.Icon?.Glyph is { } glyph) return GlyphIcon(Theme.Glyph(glyph), item.Icon.Color ?? TileColor.Accent);
        var letter = item.Title.FirstOrDefault(char.IsLetterOrDigit);
        var text = new TextBlock
        {
            Text = letter == default ? "?" : char.ToUpperInvariant(letter).ToString(),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(6), Background = Theme.ColorFor(TileColor.Accent), Child = text };
    }

    /// <summary>A glyph in a rounded square: in a state color, or dim (a folder) when <paramref name="color"/> is null.</summary>
    static FrameworkElement GlyphIcon(string glyph, TileColor? color)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (color is { } c) text.Foreground = Theme.ColorFor(c);
        else text.SetResourceReference(TextBlock.ForegroundProperty, "TileDim");
        var box = new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(6), Child = text };
        box.SetResourceReference(Border.BackgroundProperty, "TileHover");
        return box;
    }
}
