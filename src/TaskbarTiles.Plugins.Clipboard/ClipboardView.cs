using System.Globalization;
using System.Text.RegularExpressions;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>Turns the history into the tile and its popup. Pure.</summary>
public static partial class ClipboardView
{
    public const string CopyPrefix = "copy:";
    public const string ClearAction = "clear";
    const int PopupPreview = 40, TooltipChars = 300;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One line: runs of whitespace (newlines, tabs) become a space, ends trimmed, cut with an ellipsis past <paramref name="max"/>.</summary>
    public static string OneLine(string text, int max = int.MaxValue)
    {
        var line = Whitespace().Replace(text, " ").Trim();
        if (line.Length <= max) return line;
        var cut = line[..Math.Max(max - 1, 0)];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1]; // never leave half a character
        return cut.TrimEnd() + "…";
    }

    public static TileState Tile(IReadOnlyList<ClipEntry> history)
    {
        if (history.Count == 0)
            return new TileState { Title = "Clipboard", Subtitle = "Nothing copied yet", Icon = new TileIcon("info") };

        var latest = history[0];
        return new TileState
        {
            Title = OneLine(latest.Item.Text),
            Subtitle = Subtitle(latest),
            Icon = new TileIcon("info"),
            Tooltip = Tooltip(latest.Item.Text),
        };
    }

    /// <summary>"Copied 14:03 · 42 chars" for text, else "Image 800×600" or "3 files".</summary>
    public static string Subtitle(ClipEntry entry)
    {
        if (entry.Item.Kind != ClipKind.Text) return entry.Item.Detail ?? entry.Item.Text;
        var chars = entry.Item.Text.Length;
        return $"Copied {Time(entry)} · {chars} {(chars == 1 ? "char" : "chars")}";
    }

    public static FlyoutState Flyout(IReadOnlyList<ClipEntry> history) => new(
        "Clipboard",
        history.Count switch { 0 => "Nothing copied yet", 1 => "1 recent copy", var n => $"{n} recent copies" },
        history.Select(Row).ToList(),
        [new FlyoutAction(ClearAction, "Clear")]);

    /// <summary>Only text can be put back, so only text rows are clickable.</summary>
    static FlyoutRow Row(ClipEntry entry) => new(
        OneLine(entry.Item.Text, PopupPreview),
        Time(entry),
        Action: entry.Item.Kind == ClipKind.Text ? CopyPrefix + entry.Id.ToString(CultureInfo.InvariantCulture) : null);

    /// <summary>The id in a "copy:&lt;id&gt;" action, or null for any other action.</summary>
    public static long? CopyId(string actionId) =>
        actionId.StartsWith(CopyPrefix, StringComparison.Ordinal)
        && long.TryParse(actionId.AsSpan(CopyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    static string Tooltip(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= TooltipChars ? trimmed : trimmed[..(TooltipChars - 1)] + "…";
    }

    static string Time(ClipEntry entry) => entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
