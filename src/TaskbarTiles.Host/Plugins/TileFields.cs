using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Plugins;

public enum FieldType { Text, Number, Toggle, Choice, Duration, Pick }

/// <summary>
/// One input of the settings dialog, from tile.json's "fields": the settings key it edits, how it is shown and the
/// values it accepts. Min/Max bound numbers; MinDuration/MaxDuration bound durations; Choices lists a choice's values,
/// a pick's fixed ones, or a text field's suggestions (for picks and text the connector adds its own when the dialog opens).
/// Multiple: a text field holding a comma-separated list, whose suggestions add or remove an item instead of replacing it.
/// Browse: a text field holding a path, with a button in its box that picks one ("folder" or "file"); null for none.
/// </summary>
public sealed record TileField(string Key, string Label, FieldType Type, string? Help = null, double? Min = null, double? Max = null,
    TimeSpan? MinDuration = null, TimeSpan? MaxDuration = null, IReadOnlyList<string>? Choices = null, bool Multiple = false,
    string? Browse = null);

/// <summary>A button in the settings dialog, from tile.json's "settingsActions": a click runs the action:&lt;Id&gt; hook.</summary>
public sealed record SettingsAction(string Id, string Label);

public static class TileFields
{
    /// <summary>Reads a "settingsActions" array of { "id", "label" }; the error names <paramref name="shownPath"/>.</summary>
    public static (IReadOnlyList<SettingsAction> Actions, string? Error) ParseActions(JsonElement actions, string shownPath)
    {
        if (actions.ValueKind != JsonValueKind.Array) return ([], $"{shownPath}: \"settingsActions\" must be a list.");
        var list = new List<SettingsAction>();
        var n = 0;
        foreach (var el in actions.EnumerateArray())
        {
            n++;
            if (el.ValueKind != JsonValueKind.Object || Text(el, "id") is not { } id || Text(el, "label") is not { } label)
                return ([], $"{shownPath}: settings action {n} needs an \"id\" and a \"label\".");
            if (list.Any(a => a.Id == id)) return ([], $"{shownPath}: settings action {n} repeats the id \"{id}\".");
            list.Add(new SettingsAction(id, label));
        }
        return (list, null);
    }

    static readonly Dictionary<string, FieldType> Types = new(StringComparer.Ordinal)
    {
        ["text"] = FieldType.Text, ["number"] = FieldType.Number, ["toggle"] = FieldType.Toggle,
        ["choice"] = FieldType.Choice, ["duration"] = FieldType.Duration, ["pick"] = FieldType.Pick,
    };

    /// <summary>Reads a "fields" array; the error names <paramref name="shownPath"/> and the field's position (1-based).</summary>
    public static (IReadOnlyList<TileField> Fields, string? Error) Parse(JsonElement fields, string shownPath)
    {
        if (fields.ValueKind != JsonValueKind.Array) return ([], $"{shownPath}: \"fields\" must be a list.");
        var list = new List<TileField>();
        var n = 0;
        foreach (var el in fields.EnumerateArray())
        {
            n++;
            string Error(string what) => $"{shownPath}: field {n} {what}";
            if (el.ValueKind != JsonValueKind.Object) return ([], Error("must be an object."));
            if (Text(el, "key") is not { } key) return ([], Error("needs a \"key\": the settings key it edits."));
            if (key == "hooks") return ([], Error("cannot edit \"hooks\"."));
            if (list.Any(f => f.Key == key)) return ([], Error($"repeats the key \"{key}\"."));
            if (Text(el, "label") is not { } label) return ([], Error("needs a \"label\"."));
            if (Text(el, "type") is not { } typeName || !Types.TryGetValue(typeName, out var type))
                return ([], Error("needs a \"type\": text, number, toggle, choice, duration or pick."));
            var field = new TileField(key, label, type, Text(el, "help"));

            switch (type)
            {
                case FieldType.Number:
                    double? min = null, max = null;
                    if (el.TryGetProperty("min", out var a)) { if (a.ValueKind != JsonValueKind.Number) return ([], Error("has a \"min\" that is not a number.")); min = a.GetDouble(); }
                    if (el.TryGetProperty("max", out var b)) { if (b.ValueKind != JsonValueKind.Number) return ([], Error("has a \"max\" that is not a number.")); max = b.GetDouble(); }
                    field = field with { Min = min, Max = max };
                    break;
                case FieldType.Duration:
                    TimeSpan? minD = null, maxD = null;
                    if (el.TryGetProperty("min", out var c)) { if (!TryDuration(c, out var v)) return ([], Error("has a \"min\" that is not a duration such as \"10s\".")); minD = v; }
                    if (el.TryGetProperty("max", out var d)) { if (!TryDuration(d, out var v)) return ([], Error("has a \"max\" that is not a duration such as \"1h\".")); maxD = v; }
                    field = field with { MinDuration = minD, MaxDuration = maxD };
                    break;
                case FieldType.Choice:
                    if (!el.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array
                        || ch.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) || ch.GetArrayLength() == 0)
                        return ([], Error("needs \"choices\": a list of strings."));
                    field = field with { Choices = ch.EnumerateArray().Select(x => x.GetString()!).ToList() };
                    break;
                case FieldType.Pick or FieldType.Text:
                    if (type == FieldType.Text && el.TryGetProperty("browse", out var br))
                    {
                        if (br.ValueKind != JsonValueKind.String || br.GetString() is not ("folder" or "file"))
                            return ([], Error("has a \"browse\" that is not \"folder\" or \"file\"."));
                        field = field with { Browse = br.GetString() };
                    }
                    if (type == FieldType.Text && el.TryGetProperty("multiple", out var m))
                    {
                        if (m.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return ([], Error("has a \"multiple\" that is not true or false."));
                        field = field with { Multiple = m.GetBoolean() };
                    }
                    if (!el.TryGetProperty("choices", out var pc)) { if (type == FieldType.Pick) field = field with { Choices = [] }; break; }
                    if (pc.ValueKind != JsonValueKind.Array || pc.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        return ([], Error("has \"choices\" that are not a list of strings."));
                    field = field with { Choices = pc.EnumerateArray().Select(x => x.GetString()!).ToList() };
                    break;
            }
            list.Add(field);
        }
        return (list, null);
    }

    /// <summary>
    /// Checks what was typed or picked. Returns the JSON value to save, or a null value for "use the default" (empty
    /// input), or an error for the user. A toggle takes "true"/"false"; a pick a JSON list of the ticked values.
    /// </summary>
    public static (JsonNode? Value, string? Error) Check(TileField field, string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return (null, null);
        switch (field.Type)
        {
            case FieldType.Text:
                return (JsonValue.Create(text), null);
            case FieldType.Toggle:
                return bool.TryParse(text, out var on) ? (JsonValue.Create(on), null) : (null, "Choose on or off.");
            case FieldType.Choice:
                return field.Choices!.Contains(text) ? (JsonValue.Create(text), null) : (null, $"Choose one of: {string.Join(", ", field.Choices!)}.");
            case FieldType.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    || !double.IsFinite(number) || number < field.Min || number > field.Max)
                    return (null, NumberHint(field));
                return (number == Math.Floor(number) && Math.Abs(number) < 1e15 ? JsonValue.Create((long)number) : JsonValue.Create(number), null);
            case FieldType.Pick:
                try
                {
                    if (JsonNode.Parse(text) is JsonArray list && list.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
                        return (new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x!.GetValue<string>())).ToArray()), null);
                }
                catch (JsonException) { }
                return (null, "Tick the values to use.");
            case FieldType.Duration:
                if (!Duration.TryParse(text, out var span) || span < field.MinDuration || span > field.MaxDuration)
                    return (null, DurationHint(field));
                return (JsonValue.Create(ConfigJson.FormatDuration(span)), null);
            default:
                return (null, "Unknown field type.");
        }
    }

    /// <summary>The text an input shows for a settings value (a default or the tile's own); empty when there is none.</summary>
    public static string Display(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble().ToString(CultureInfo.CurrentCulture),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        null or { ValueKind: JsonValueKind.Null } => "", // a null default: no value, the connector works one out
        var v => v.Value.GetRawText(),
    };

    /// <summary>
    /// What a pick field offers: the connector's values, then the field's fixed ones, then values saved earlier that
    /// neither knows now (a device not connected), each once, ignoring case.
    /// </summary>
    public static IReadOnlyList<string> PickChoices(TileField field, IEnumerable<string> live, JsonElement? own)
    {
        IEnumerable<string> saved = own switch
        {
            { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!),
            { ValueKind: JsonValueKind.String } text when field.Multiple => ListItems(text.GetString()!), // so each can be taken out again
            _ => [],
        };
        return live.Concat(field.Choices ?? []).Concat(saved).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The suggestions a text field shows for <paramref name="typed"/>: those containing it (ignoring case), first the
    /// ones starting with it, without the one already typed; at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> Suggest(IReadOnlyList<string> suggestions, string typed, int max = 8) =>
        suggestions
            .Where(s => s.Contains(typed, StringComparison.CurrentCultureIgnoreCase) && !s.Equals(typed, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(s => s.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .Take(max)
            .ToList();

    /// <summary>The items of a comma-separated list, trimmed, without empty ones.</summary>
    public static IReadOnlyList<string> ListItems(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A "multiple" field's suggestion clicked: <paramref name="item"/> leaves the list when it is in it (ignoring case), else joins its end.</summary>
    public static string Toggle(string text, string item)
    {
        var items = ListItems(text).ToList();
        var removed = items.RemoveAll(i => i.Equals(item, StringComparison.CurrentCultureIgnoreCase)) > 0;
        if (!removed) items.Add(item);
        return string.Join(", ", items);
    }

    static string NumberHint(TileField f) => (f.Min, f.Max) switch
    {
        ({ } min, { } max) => $"Enter a number from {min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}.",
        ({ } min, null) => $"Enter a number of at least {min.ToString(CultureInfo.CurrentCulture)}.",
        (null, { } max) => $"Enter a number of at most {max.ToString(CultureInfo.CurrentCulture)}.",
        _ => "Enter a number.",
    };

    static string DurationHint(TileField f) => (f.MinDuration, f.MaxDuration) switch
    {
        ({ } min, { } max) => $"Enter a duration from {ConfigJson.FormatDuration(min)} to {ConfigJson.FormatDuration(max)}, e.g. 30s.",
        ({ } min, null) => $"Enter a duration of at least {ConfigJson.FormatDuration(min)}, e.g. 30s.",
        (null, { } max) => $"Enter a duration of at most {ConfigJson.FormatDuration(max)}, e.g. 30s.",
        _ => "Enter a duration, e.g. 30s or 5m.",
    };

    static bool TryDuration(JsonElement el, out TimeSpan value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.String && Duration.TryParse(el.GetString(), out value);
    }

    static string? Text(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;
}
