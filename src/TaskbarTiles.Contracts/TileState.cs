namespace TaskbarTiles.Contracts;

public enum TileColor { Neutral, Accent, Ok, Warn, Bad }

/// <summary>Glyph names: play, pause, previous, next, check, cancel, warning, refresh, music, info, sync, cloud, clock.</summary>
public sealed record TileIcon(string Glyph, TileColor Color = TileColor.Neutral);

public sealed record TileButton(string Id, string Glyph, string? Tooltip = null, bool Enabled = true);

public sealed record TileState
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public TileIcon? Icon { get; init; }

    /// <summary>PNG or JPEG bytes, shown instead of the icon (e.g. album art).</summary>
    public byte[]? Image { get; init; }

    public IReadOnlyList<TileButton> Buttons { get; init; } = [];

    /// <summary>0..1, drawn as a thin bar along the bottom edge.</summary>
    public double? Progress { get; init; }

    public IReadOnlyList<double>? Sparkline { get; init; }
    public bool Urgent { get; init; }
    public string? Tooltip { get; init; }
}
