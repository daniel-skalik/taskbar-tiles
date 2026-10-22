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
    };

    public static string Glyph(string name) => Glyphs.TryGetValue(name, out var g) ? g : Glyphs["info"];

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

    static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
