using System.Text.Json;

namespace TaskbarTiles.Host.Config;

public sealed record ConfigError(string Message, int? Line = null, int? Column = null)
{
    public override string ToString() => Line is null ? Message : $"{Message} (line {Line}, column {Column})";
}

public sealed record ConfigParseResult(HostConfig? Config, ConfigError? Error)
{
    public bool Success => Config is not null;
}

public static class ConfigParser
{
    static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly TimeSpan DefaultRotate = TimeSpan.FromSeconds(5);

    public static ConfigParseResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            return new(null, new ConfigError("Invalid JSON: " + TrimJsonMessage(ex.Message),
                (int?)(ex.LineNumber + 1), (int?)(ex.BytePositionInLine + 1)));
        }

        using (doc)
        {
            try
            {
                return new(Build(doc.RootElement), null);
            }
            catch (ConfigException ex)
            {
                return new(null, new ConfigError(ex.Message));
            }
        }
    }

    static HostConfig Build(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ConfigException("The config must be a JSON object.");

        var tiles = new Dictionary<string, TileConfig>(StringComparer.Ordinal);
        if (root.TryGetProperty("tiles", out var tilesEl))
        {
            if (tilesEl.ValueKind != JsonValueKind.Object) throw new ConfigException("\"tiles\" must be an object.");
            foreach (var tile in tilesEl.EnumerateObject())
                tiles[tile.Name] = BuildTile(tile.Name, tile.Value);
        }

        var displays = new Dictionary<string, DisplayLayout>(StringComparer.Ordinal);
        if (root.TryGetProperty("displays", out var displaysEl))
        {
            if (displaysEl.ValueKind != JsonValueKind.Object) throw new ConfigException("\"displays\" must be an object.");
            foreach (var display in displaysEl.EnumerateObject())
                displays[display.Name] = BuildLayout(display.Name, display.Value, tiles);
        }

        return new HostConfig(tiles, displays);
    }

    static TileConfig BuildTile(string id, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ConfigException($"Tile '{id}' must be an object.");
        if (!el.TryGetProperty("connector", out var c) || c.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(c.GetString()))
            throw new ConfigException($"Tile '{id}' needs a \"connector\" name.");

        var settings = el.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.Clone()
            : EmptyObject();
        return new TileConfig(c.GetString()!, settings);
    }

    static DisplayLayout BuildLayout(string display, JsonElement el, IReadOnlyDictionary<string, TileConfig> tiles)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ConfigException($"Display '{display}' must be an object.");
        if (!el.TryGetProperty("slots", out var slotsEl)) return DisplayLayout.Empty;
        if (slotsEl.ValueKind != JsonValueKind.Array) throw new ConfigException($"\"slots\" on display '{display}' must be an array.");

        var slots = new List<Slot>();
        var index = 0;
        foreach (var slotEl in slotsEl.EnumerateArray())
        {
            index++;
            var slot = BuildSlot(display, index, slotEl);
            IReadOnlyList<string> ids = slot is StackSlot st ? st.TileIds : new[] { ((TileSlot)slot).TileId };
            foreach (var id in ids)
                if (!tiles.ContainsKey(id))
                    throw new ConfigException($"Display '{display}' references unknown tile '{id}'.");
            slots.Add(slot);
        }
        return new DisplayLayout(slots);
    }

    static Slot BuildSlot(string display, int index, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return new TileSlot(el.GetString()!, false);

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("tile", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var pin = el.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.True;
            return new TileSlot(t.GetString()!, pin);
        }

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("stack", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            var ids = s.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new ConfigException($"Stack in slot {index} on display '{display}' must list tile ids."))
                .ToList();
            var rotate = DefaultRotate;
            if (el.TryGetProperty("rotate", out var r) && !Duration.TryParse(r.GetString(), out rotate))
                throw new ConfigException($"Invalid \"rotate\" in slot {index} on display '{display}'. Use e.g. 5s.");
            return new StackSlot(ids, rotate);
        }

        throw new ConfigException($"Slot {index} on display '{display}' must be a tile id, {{\"tile\": ...}} or {{\"stack\": [...]}}.");
    }

    static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    static string TrimJsonMessage(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }

    sealed class ConfigException(string message) : Exception(message);
}
