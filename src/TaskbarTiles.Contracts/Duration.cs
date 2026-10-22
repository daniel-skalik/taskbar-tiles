using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskbarTiles.Contracts;

/// <summary>Parses "500ms", "30s", "10m", "1h". Shared by config.json and plugin settings.</summary>
public static partial class Duration
{
    // [0-9] rather than \d: \d also matches non-ASCII digits, which double.Parse rejects.
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*(ms|s|m|h)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (text is null) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        try
        {
            value = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                _ => TimeSpan.FromHours(n),
            };
        }
        catch (OverflowException)
        {
            value = default;
            return false;
        }
        return true;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid duration '{text}'. Use e.g. 500ms, 30s, 10m, 1h.");
}
