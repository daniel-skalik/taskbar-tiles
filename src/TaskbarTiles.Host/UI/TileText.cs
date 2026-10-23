using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host.UI;

/// <summary>Text a compact (icon-only) tile shows instead of its title.</summary>
public static class TileText
{
    /// <summary>The first letter or digit of the title in upper case; "•" when there is none. Shown when a compact tile has no icon.</summary>
    public static string Monogram(string? title)
    {
        foreach (var c in title ?? "")
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "•";
    }

    /// <summary>Title, subtitle and tooltip, one per line, skipping empty and repeated lines; null when all are empty.</summary>
    public static string? CompactTooltip(TileState state)
    {
        var lines = new[] { state.Title, state.Subtitle, state.Tooltip }
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}
