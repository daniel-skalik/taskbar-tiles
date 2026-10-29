using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskbarTiles.Host.Config;

/// <summary>
/// Turns config.json text into an editable tree and back. Saving rewrites the whole file: comments and custom
/// formatting are lost, everything else (unknown keys included) keeps its place; new keys are appended.
/// </summary>
public static class ConfigJson
{
    static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file for people: keep "…" and quotes readable
    };

    /// <summary>Throws JsonException on invalid JSON and InvalidOperationException when the root is not an object.</summary>
    public static JsonObject ParseRoot(string text) =>
        JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
        ?? throw new InvalidOperationException("The config must be a JSON object.");

    public static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>
    /// Sets displays[key].slots and its "side" ("right"; left is the default, so the key is removed), keeping any other
    /// keys of that display entry.
    /// </summary>
    public static void SetDisplay(JsonObject root, string key, DisplayLayout layout)
    {
        var displays = ChildObject(root, "displays");
        var display = ChildObject(displays, key);
        if (layout.Side == BarSide.Right) display["side"] = "right";
        else display.Remove("side");
        display["slots"] = new JsonArray(layout.Slots.Select(ToNode).ToArray());
    }

    /// <summary>Adds or replaces tiles[id] with { "kind": ... }, plus "settings" when it has any keys.</summary>
    public static void SetTile(JsonObject root, string id, TileConfig tile)
    {
        var tiles = ChildObject(root, "tiles");
        var node = new JsonObject { ["kind"] = tile.Kind };
        if (tile.Settings.ValueKind == JsonValueKind.Object && tile.Settings.EnumerateObject().Any())
            node["settings"] = JsonNode.Parse(tile.Settings.GetRawText());
        tiles[id] = node;
    }

    /// <summary>
    /// Deletes tiles[id] for every candidate no display entry places (including "*" and entries of displays not connected
    /// now). Call it after the layout change, with the ids that change took off a display. Placement is read with
    /// ConfigParser, so every slot form counts; a tree that does not parse is left alone (saving refuses it anyway).
    /// </summary>
    public static void RemoveUnplacedTiles(JsonObject root, IEnumerable<string> candidates)
    {
        if (root["tiles"] is not JsonObject tiles) return;
        var parsed = ConfigParser.Parse(Serialize(root));
        if (!parsed.Success) return;
        var placed = parsed.Config!.Displays.Values.SelectMany(DisplayResolver.PlacedTileIds).ToHashSet(StringComparer.Ordinal);
        foreach (var id in candidates.Distinct(StringComparer.Ordinal))
            if (!placed.Contains(id)) tiles.Remove(id);
    }

    /// <summary>A plain tile is its id, a pinned tile { "tile", "pin" }, a stack { "stack", "rotate" }.</summary>
    public static JsonNode ToNode(Slot slot) => slot switch
    {
        TileSlot { Pin: false } t => JsonValue.Create(t.TileId),
        TileSlot t => new JsonObject { ["tile"] = t.TileId, ["pin"] = true },
        StackSlot s => new JsonObject
        {
            ["stack"] = new JsonArray(s.TileIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["rotate"] = FormatDuration(s.Rotate),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>"2h", "5m" or seconds such as "30s" and "1.5s" (to the millisecond), all readable by Duration.TryParse.</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value > TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerHour == 0) return $"{(long)value.TotalHours}h";
        if (value > TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerMinute == 0) return $"{(long)value.TotalMinutes}m";
        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    static JsonObject ChildObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
