namespace TaskbarTiles.Contracts;

public enum TileColor { Neutral, Accent, Ok, Warn, Bad }

/// <summary>Glyph names: play, pause, previous, next, check, cancel, warning, refresh, music, info, sync, cloud, clock,
/// mouse, keyboard, headphones, battery-0..battery-10, battery-charging-0..battery-charging-10.</summary>
public sealed record TileIcon(string Glyph, TileColor Color = TileColor.Neutral);

public sealed record TileButton(string Id, string Glyph, string? Tooltip = null, bool Enabled = true);

/// <summary>
/// Pixels drawn across the whole tile, under the text: <paramref name="Width"/> × <paramref name="Height"/>, row by row,
/// each 0xAARRGGBB. Scaled up with hard pixel edges to cover the tile, so a small canvas (e.g. 72×20) gives pixel art.
/// </summary>
public sealed record TileCanvas(int Width, int Height, int[] Pixels);

public sealed record TileState
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public TileIcon? Icon { get; init; }

    /// <summary>PNG or JPEG bytes, shown instead of the icon (e.g. album art).</summary>
    public byte[]? Image { get; init; }

    /// <summary>A picture across the whole tile, e.g. an animation frame; title and subtitle draw over it.</summary>
    public TileCanvas? Canvas { get; init; }

    public IReadOnlyList<TileButton> Buttons { get; init; } = [];

    /// <summary>0..1, drawn as a thin bar along the bottom edge.</summary>
    public double? Progress { get; init; }

    /// <summary>
    /// 0..1, where the progress bar should be by now (say, an even pace through a usage limit): a dim bar under the
    /// progress bar. While <see cref="Progress"/> is past it, the bar is blue up to the mark and orange beyond. Only drawn with a progress bar.
    /// </summary>
    public double? ProgressMark { get; init; }

    /// <summary>Recent values, oldest first, drawn as a chart behind the title and subtitle.</summary>
    public IReadOnlyList<double>? Sparkline { get; init; }

    /// <summary>
    /// The value at the top of the sparkline, with 0 at the bottom (100 for a percentage). Null: the chart runs from the
    /// values' own minimum to their maximum.
    /// </summary>
    public double? SparklineMax { get; init; }
    public bool Urgent { get; init; }
    public string? Tooltip { get; init; }
}
