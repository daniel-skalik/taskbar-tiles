using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>Result of reading a script's stdout. Empty output has neither a value nor an error.</summary>
public sealed record ParseResult<T>(T? Value, string? Error, IReadOnlyList<string> Warnings) where T : class
{
    public static ParseResult<T> Empty { get; } = new(null, null, []);
}

/// <summary>
/// Maps a script's stdout (one camelCase JSON object) to TileState or FlyoutState. Lenient: unknown fields
/// and badly typed values are dropped with a warning; only output that is not a JSON object is an error.
/// </summary>
public static class ScriptOutput
{
    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    static readonly HashSet<string> TileFields = new(StringComparer.Ordinal)
        { "title", "subtitle", "tooltip", "icon", "image", "buttons", "progress", "sparkline", "urgent" };

    static readonly HashSet<string> FlyoutFields = new(StringComparer.Ordinal)
        { "title", "subtitle", "rows", "actions", "image" };

    public static ParseResult<TileState> ParseTile(string? text) => Parse(text, ReadTile);

    public static ParseResult<FlyoutState> ParseFlyout(string? text) => Parse(text, ReadFlyout);

    static ParseResult<T> Parse<T>(string? text, Func<JsonElement, List<string>, T> read) where T : class
    {
        var trimmed = text?.Trim().TrimStart('\uFEFF').Trim();
        if (string.IsNullOrEmpty(trimmed)) return ParseResult<T>.Empty;
        try
        {
            using var doc = JsonDocument.Parse(trimmed, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(null, $"Expected a JSON object but got {Kind(doc.RootElement)}.", []);
            var warnings = new List<string>();
            return new(read(doc.RootElement, warnings), null, warnings);
        }
        // Lone surrogates make parsing (ArgumentException) or reading strings (InvalidOperationException) throw.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new(null, "Invalid JSON: " + ex.Message, []);
        }
    }

    static TileState ReadTile(JsonElement o, List<string> w)
    {
        WarnUnknown(o, TileFields, w);
        return new TileState
        {
            Title = Text(o, "title", w),
            Subtitle = Text(o, "subtitle", w),
            Tooltip = Text(o, "tooltip", w),
            Icon = o.TryGetProperty("icon", out var icon) ? ReadIcon(icon, "icon", w) : null,
            Image = ReadImage(o, w),
            Buttons = ReadButtons(o, w),
            Progress = Number(o, "progress", w),
            Sparkline = Numbers(o, "sparkline", w),
            Urgent = Bool(o, "urgent", w) ?? false,
        };
    }

    static FlyoutState ReadFlyout(JsonElement o, List<string> w)
    {
        WarnUnknown(o, FlyoutFields, w);
        var title = Text(o, "title", w);
        if (title is null) w.Add("\"title\" is missing.");
        return new FlyoutState(title ?? "", Text(o, "subtitle", w), ReadRows(o, w), ReadActions(o, w), ReadImage(o, w));
    }

    static void WarnUnknown(JsonElement o, HashSet<string> known, List<string> w)
    {
        foreach (var p in o.EnumerateObject())
            if (!known.Contains(p.Name)) w.Add($"Unknown field \"{p.Name}\" ignored.");
    }

    static string? Text(JsonElement o, string name, List<string> w, string? where = null)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        w.Add($"\"{FieldPath(where, name)}\" must be a string; got {Kind(v)}.");
        return null;
    }

    static double? Number(JsonElement o, string name, List<string> w)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
            w.Add($"\"{name}\" must be a finite number; dropped.");
            return null;
        }
        w.Add($"\"{name}\" must be a number; got {Kind(v)}.");
        return null;
    }

    static bool? Bool(JsonElement o, string name, List<string> w, string? where = null)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        w.Add($"\"{FieldPath(where, name)}\" must be true or false; got {Kind(v)}.");
        return null;
    }

    static IReadOnlyList<double>? Numbers(JsonElement o, string name, List<string> w)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"{name}\" must be an array of numbers; got {Kind(v)}.");
            return null;
        }
        var list = new List<double>();
        var skipped = 0;
        var nonFinite = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) skipped++;
            else if (item.TryGetDouble(out var d) && double.IsFinite(d)) list.Add(d);
            else nonFinite++;
        }
        if (skipped > 0) w.Add($"\"{name}\": {skipped} value(s) that are not numbers were skipped.");
        if (nonFinite > 0) w.Add($"\"{name}\": {nonFinite} value(s) that are not finite numbers were skipped.");
        return list;
    }

    static TileIcon? ReadIcon(JsonElement v, string where, List<string> w)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var name = v.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : new TileIcon(name.Trim());
            case JsonValueKind.Object:
                var glyph = Text(v, "glyph", w, where);
                if (string.IsNullOrWhiteSpace(glyph))
                {
                    w.Add($"\"{where}.glyph\" is missing.");
                    return null;
                }
                return new TileIcon(glyph.Trim(), ReadColor(v, where, w));
            default:
                w.Add($"\"{where}\" must be a glyph name or {{ \"glyph\", \"color\" }}; got {Kind(v)}.");
                return null;
        }
    }

    static TileColor ReadColor(JsonElement icon, string where, List<string> w)
    {
        var name = Text(icon, "color", w, where)?.Trim();
        if (string.IsNullOrEmpty(name)) return TileColor.Neutral;
        // Compare names only: Enum.TryParse would also accept numbers such as "2".
        foreach (var color in Enum.GetValues<TileColor>())
            if (string.Equals(color.ToString(), name, StringComparison.OrdinalIgnoreCase)) return color;
        w.Add($"\"{where}.color\": unknown color \"{name}\"; use neutral, accent, ok, warn or bad.");
        return TileColor.Neutral;
    }

    static byte[]? ReadImage(JsonElement o, List<string> w)
    {
        var text = Text(o, "image", w)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var comma = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? text.IndexOf(',') : -1;
        var base64 = (comma >= 0 ? text[(comma + 1)..] : text).Trim();
        var buffer = new byte[base64.Length]; // base64 decodes to at most 3/4 of its length
        if (Convert.TryFromBase64String(base64, buffer, out var written)) return buffer[..written];
        w.Add("\"image\" must be base64-encoded PNG or JPEG bytes.");
        return null;
    }

    static IReadOnlyList<TileButton> ReadButtons(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("buttons", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"buttons\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<TileButton>();
        var i = 0;
        foreach (var b in v.EnumerateArray())
        {
            var where = $"buttons[{i++}]";
            if (b.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be an object; skipped.");
                continue;
            }
            var id = Text(b, "id", w, where);
            if (string.IsNullOrWhiteSpace(id))
            {
                w.Add($"\"{where}.id\" is missing; skipped.");
                continue;
            }
            list.Add(new TileButton(id, Text(b, "glyph", w, where) ?? "info", Text(b, "tooltip", w, where), Bool(b, "enabled", w, where) ?? true));
        }
        return list;
    }

    static IReadOnlyList<FlyoutRow> ReadRows(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("rows", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"rows\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<FlyoutRow>();
        var i = 0;
        foreach (var r in v.EnumerateArray())
        {
            var where = $"rows[{i++}]";
            if (r.ValueKind == JsonValueKind.String)
            {
                list.Add(new FlyoutRow(r.GetString()!));
                continue;
            }
            if (r.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be a string or an object; skipped.");
                continue;
            }
            var text = Text(r, "text", w, where);
            if (text is null)
            {
                w.Add($"\"{where}.text\" is missing; skipped.");
                continue;
            }
            var icon = r.TryGetProperty("icon", out var iconEl) ? ReadIcon(iconEl, where + ".icon", w) : null;
            list.Add(new FlyoutRow(text, Text(r, "value", w, where), icon));
        }
        return list;
    }

    static IReadOnlyList<FlyoutAction> ReadActions(JsonElement o, List<string> w)
    {
        if (!o.TryGetProperty("actions", out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array)
        {
            w.Add($"\"actions\" must be an array; got {Kind(v)}.");
            return [];
        }
        var list = new List<FlyoutAction>();
        var i = 0;
        foreach (var a in v.EnumerateArray())
        {
            var where = $"actions[{i++}]";
            if (a.ValueKind != JsonValueKind.Object)
            {
                w.Add($"\"{where}\" must be an object; skipped.");
                continue;
            }
            var id = Text(a, "id", w, where);
            if (string.IsNullOrWhiteSpace(id))
            {
                w.Add($"\"{where}.id\" is missing; skipped.");
                continue;
            }
            list.Add(new FlyoutAction(id, Text(a, "label", w, where) ?? id, Bool(a, "primary", w, where) ?? false));
        }
        return list;
    }

    static string FieldPath(string? where, string name) => where is null ? name : $"{where}.{name}";

    static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };
}
