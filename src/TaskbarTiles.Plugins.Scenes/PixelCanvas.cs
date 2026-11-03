namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Drawing helpers over a frame of 0xAARRGGBB pixels, row by row. A struct around the caller's array, so drawing a frame
/// allocates nothing. Everything clips to the canvas, so scenes can draw sprites half off the edge.
/// </summary>
public readonly struct PixelCanvas
{
    /// <summary>
    /// Frames are 19 pixels high, 2 DIP per pixel on a tile 38 DIP high inside its border, and as wide as the tile at
    /// the same scale: 100 for the default 200 DIP. <see cref="WidthFor"/> turns a tile width into a frame width.
    /// </summary>
    public const int DefaultWidth = 100, Height = 19, MinWidth = 20, MaxWidth = 320;

    readonly int[] _px;

    /// <summary>A canvas over <paramref name="pixels"/>, rows of <see cref="Height"/>; its width follows from the length.</summary>
    public PixelCanvas(int[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length == 0 || pixels.Length % Height != 0) throw new ArgumentException($"A frame has {Height} rows of pixels.", nameof(pixels));
        _px = pixels;
        Width = pixels.Length / Height;
    }

    public int Width { get; }

    public static int[] NewFrame(int width = DefaultWidth) => new int[width * Height];

    /// <summary>The frame width for a tile <paramref name="tileWidth"/> DIP wide (2 DIP per pixel); the default for 0.</summary>
    public static int WidthFor(double tileWidth) =>
        tileWidth <= 0 ? DefaultWidth : Math.Clamp((int)Math.Round(tileWidth / 2), MinWidth, MaxWidth);

    /// <summary>An opaque color from 0xRRGGBB.</summary>
    public static int Rgb(int rgb) => unchecked((int)0xFF000000) | (rgb & 0xFFFFFF);

    /// <summary>A color with the given alpha (0..255) from 0xRRGGBB or an opaque color.</summary>
    public static int Rgba(int rgb, int alpha) => (Math.Clamp(alpha, 0, 255) << 24) | (rgb & 0xFFFFFF);

    /// <summary>Mixes two colors: t = 0 gives <paramref name="a"/>, t = 256 gives <paramref name="b"/>. Alpha mixes too.</summary>
    public static int Lerp(int a, int b, int t)
    {
        t = Math.Clamp(t, 0, 256);
        int u = 256 - t;
        int aa = (a >> 24) & 255, ar = (a >> 16) & 255, ag = (a >> 8) & 255, ab = a & 255;
        int ba = (b >> 24) & 255, br = (b >> 16) & 255, bg = (b >> 8) & 255, bb = b & 255;
        return ((aa * u + ba * t) >> 8) << 24 | ((ar * u + br * t) >> 8) << 16 | ((ag * u + bg * t) >> 8) << 8 | ((ab * u + bb * t) >> 8);
    }

    /// <summary>Scales the color channels by f/256 (keeps alpha), for shading a palette by daylight.</summary>
    public static int Scale(int c, int f)
    {
        int r = Math.Min(255, (((c >> 16) & 255) * f) >> 8), g = Math.Min(255, (((c >> 8) & 255) * f) >> 8), b = Math.Min(255, ((c & 255) * f) >> 8);
        return (c & unchecked((int)0xFF000000)) | r << 16 | g << 8 | b;
    }

    public void Clear(int color) => Array.Fill(_px, color);

    public int Get(int x, int y) => (uint)x < Width && (uint)y < Height ? _px[y * Width + x] : 0;

    /// <summary>Draws one pixel, blending by the color's alpha over what is there.</summary>
    public void Set(int x, int y, int color)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        int a = (color >> 24) & 255;
        if (a == 0) return;
        int i = y * Width + x;
        if (a == 255) { _px[i] = color; return; }
        int d = _px[i], u = 255 - a;
        int r = (((color >> 16) & 255) * a + ((d >> 16) & 255) * u) / 255;
        int g = (((color >> 8) & 255) * a + ((d >> 8) & 255) * u) / 255;
        int b = ((color & 255) * a + (d & 255) * u) / 255;
        _px[i] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
    }

    public void Fill(int x, int y, int w, int h, int color)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++) Set(xx, yy, color);
    }

    /// <summary>Fills rows <paramref name="y0"/>..<paramref name="y1"/> (inclusive) blending from top to bottom color.</summary>
    public void VerticalGradient(int y0, int y1, int top, int bottom)
    {
        int span = Math.Max(1, y1 - y0);
        for (int y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
        {
            int c = Lerp(top, bottom, (y - y0) * 256 / span);
            for (int x = 0; x < Width; x++) Set(x, y, c);
        }
    }

    /// <summary>
    /// Draws string art: '1'..'9' pick palette[0..8], anything else is transparent. <paramref name="flip"/> mirrors it
    /// left to right, so one sprite faces both ways.
    /// </summary>
    public void Sprite(int x, int y, string[] art, int[] palette, bool flip = false)
    {
        for (int row = 0; row < art.Length; row++)
        {
            var line = art[row];
            for (int col = 0; col < line.Length; col++)
            {
                int k = line[col] - '1';
                if ((uint)k >= (uint)palette.Length) continue;
                Set(flip ? x + line.Length - 1 - col : x + col, y + row, palette[k]);
            }
        }
    }
}
