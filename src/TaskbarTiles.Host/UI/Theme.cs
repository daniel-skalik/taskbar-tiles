using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.UI;

/// <summary>Brushes for light/dark taskbars, semantic colors, glyph names and image decoding.</summary>
public static class Theme
{
    static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = "", ["pause"] = "", ["previous"] = "", ["next"] = "",
        ["check"] = "", ["cancel"] = "", ["warning"] = "", ["refresh"] = "",
        ["music"] = "", ["info"] = "", ["sync"] = "", ["cloud"] = "", ["clock"] = "",
        ["mouse"] = "\uE962", ["keyboard"] = "\uE765", ["headphones"] = "\uE7F6",
        ["cpu"] = "\uE950", ["memory"] = "\uE964", ["gpu"] = "\uF211", ["network"] = "\uE968", ["disk"] = "\uEDA2",
        ["activity"] = "\uE9D9", ["package"] = "\uE7B8", ["branch"] = "\uF003",
    };

    // battery-0..battery-10 and battery-charging-0..battery-charging-10 (tenths of full). Segoe keeps 10 out of sequence.
    static readonly string[] BatteryGlyphs =
        ["\uE850", "\uE851", "\uE852", "\uE853", "\uE854", "\uE855", "\uE856", "\uE857", "\uE858", "\uE859", "\uE83F"];
    static readonly string[] ChargingGlyphs =
        ["\uE85A", "\uE85B", "\uE85C", "\uE85D", "\uE85E", "\uE85F", "\uE860", "\uE861", "\uE862", "\uE83E", "\uEA93"];

    static Theme()
    {
        for (var i = 0; i <= 10; i++)
        {
            Glyphs[$"battery-{i}"] = BatteryGlyphs[i];
            Glyphs[$"battery-charging-{i}"] = ChargingGlyphs[i];
        }
    }

    public static string Glyph(string name) => Glyphs.TryGetValue(name, out var g) ? g : Glyphs["info"];

    /// <summary>
    /// A 14 DIP drawing for a popup row icon, or null when <paramref name="name"/> is not a row shape (the row then shows
    /// a colored dot): <c>ring</c> and <c>half-ring</c> (a state, open or under way), <c>bars-1</c> to <c>bars-3</c>
    /// (a level out of three), <c>dashes</c> (no level) and <c>alert</c> (a filled square with "!"). Neutral shapes take
    /// the theme's dim text color, which reads better than the neutral gray on either theme.
    /// </summary>
    public static FrameworkElement? RowShape(string name, TileColor color)
    {
        var brush = ColorFor(color);
        void Paint(System.Windows.Shapes.Shape shape, DependencyProperty property)
        {
            if (color == TileColor.Neutral) shape.SetResourceReference(property, "TileDim");
            else shape.SetValue(property, brush);
        }
        const double size = 14;
        var canvas = new System.Windows.Controls.Canvas { Width = size, Height = size };
        void Add(UIElement e, double x, double y)
        {
            System.Windows.Controls.Canvas.SetLeft(e, x);
            System.Windows.Controls.Canvas.SetTop(e, y);
            canvas.Children.Add(e);
        }
        void Bar(double x, double height, bool on)
        {
            var bar = new System.Windows.Shapes.Rectangle { Width = 3, Height = height, RadiusX = 1, RadiusY = 1, Opacity = on ? 1 : 0.35 };
            Paint(bar, System.Windows.Shapes.Shape.FillProperty);
            Add(bar, x, 12.5 - height);
        }
        System.Windows.Shapes.Ellipse Ring()
        {
            var ring = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, StrokeThickness = 1.5 };
            Paint(ring, System.Windows.Shapes.Shape.StrokeProperty);
            return ring;
        }

        switch (name.ToLowerInvariant())
        {
            case "ring":
                Add(Ring(), 1, 1);
                return canvas;
            case "half-ring":
                Add(Ring(), 1, 1);
                var half = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 7,3.5 A 3.5,3.5 0 0 1 7,10.5 Z") };
                Paint(half, System.Windows.Shapes.Shape.FillProperty);
                Add(half, 0, 0);
                return canvas;
            case "bars-1" or "bars-2" or "bars-3":
                var level = name[^1] - '0';
                Bar(1, 5, level >= 1);
                Bar(5.5, 8, level >= 2);
                Bar(10, 11, level >= 3);
                return canvas;
            case "dashes":
                for (var i = 0; i < 3; i++)
                {
                    var dash = new System.Windows.Shapes.Rectangle { Width = 3, Height = 1.5, Opacity = 0.6 };
                    Paint(dash, System.Windows.Shapes.Shape.FillProperty);
                    Add(dash, 1 + i * 4.5, 6.25);
                }
                return canvas;
            case "alert":
                var square = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3 };
                Paint(square, System.Windows.Shapes.Shape.FillProperty);
                Add(square, 1, 1);
                Add(new System.Windows.Shapes.Rectangle { Width = 1.6, Height = 5, Fill = Brushes.White }, 6.2, 3.5);
                Add(new System.Windows.Shapes.Rectangle { Width = 1.6, Height = 1.6, Fill = Brushes.White }, 6.2, 9.4);
                return canvas;
            default:
                return null;
        }
    }

    public static Brush ColorFor(TileColor color) => color switch
    {
        TileColor.Ok => Frozen(0x3F, 0xB9, 0x7A),
        TileColor.Warn => Frozen(0xE6, 0xA2, 0x3C),
        TileColor.Bad => Frozen(0xE5, 0x53, 0x4B),
        TileColor.Accent => Frozen(0x4C, 0x7C, 0xE0),
        _ => Frozen(0x6B, 0x6F, 0x7A),
    };

    /// <summary>Sets the brush keys used by TileView, TileBar and FlyoutWindow.</summary>
    public static void ApplyTo(ResourceDictionary r, bool light)
    {
        r["TileForeground"] = light ? Frozen(0x1B, 0x1B, 0x1B) : Brushes.White;
        r["TileDim"] = light ? Frozen(0x99, 0, 0, 0) : Frozen(0xB3, 0xFF, 0xFF, 0xFF);
        r["TileFill"] = light ? Frozen(0x0A, 0, 0, 0) : Frozen(0x0F, 0xFF, 0xFF, 0xFF);
        r["TileHover"] = light ? Frozen(0x14, 0, 0, 0) : Frozen(0x1A, 0xFF, 0xFF, 0xFF);
        r["TileOutline"] = light ? Frozen(0x1F, 0, 0, 0) : Frozen(0x24, 0xFF, 0xFF, 0xFF);
        r["FlyoutBackground"] = light ? Frozen(0xF3, 0xF3, 0xF3) : Frozen(0x2B, 0x2A, 0x31);
        r["FlyoutBorder"] = light ? Frozen(0x1F, 0, 0, 0) : Frozen(0x17, 0xFF, 0xFF, 0xFF);
        r["ButtonHover"] = light ? Frozen(0x1A, 0, 0, 0) : Frozen(0x22, 0xFF, 0xFF, 0xFF);
        r["FlyoutCard"] = light ? Frozen(0x80, 0xFF, 0xFF, 0xFF) : Frozen(0x12, 0xFF, 0xFF, 0xFF);
        r["FlyoutDivider"] = light ? Frozen(0x14, 0, 0, 0) : Frozen(0x14, 0xFF, 0xFF, 0xFF);
    }

    public static ImageSource? Decode(byte[]? bytes, int decodeWidth = 96)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A TileCanvas as a bitmap; null when there is none or its pixel count does not match its size.</summary>
    public static BitmapSource? FromPixels(TileCanvas? canvas)
    {
        if (canvas is not { Width: > 0 and <= 1024, Height: > 0 and <= 256 } c || c.Pixels is null || c.Pixels.Length != c.Width * c.Height) return null;
        // 0xAARRGGBB ints are B, G, R, A bytes in memory: Bgra32.
        var bmp = BitmapSource.Create(c.Width, c.Height, 96, 96, PixelFormats.Bgra32, null, c.Pixels, c.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
