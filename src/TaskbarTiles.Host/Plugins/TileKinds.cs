using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Plugins;

/// <summary>
/// The connectors the DLLs of one kind folder declare (ids private to that folder), why loading them failed, and the
/// stamp of the DLL files that were loaded.
/// </summary>
public sealed record KindDlls(IReadOnlyDictionary<string, ConnectorDescriptor> Connectors, string? Error, string Stamp)
{
    public static KindDlls None { get; } = new(new Dictionary<string, ConnectorDescriptor>(StringComparer.Ordinal), null, "");
}

/// <summary>
/// One folder under tiles\: a tile kind. Id is the folder name. Error is set when the kind is broken (bad tile.json, a DLL
/// that does not load, a connector nobody in the folder declares); its tiles then show that error and other kinds keep
/// working. Stamp changes when tile.json or a script in the folder changes; DllStamp when a DLL on disk does. Width is
/// tile.json's "width", which replaces the connector's width when set; Compact is false when tile.json says
/// "compact": false (its tiles never shrink to an icon).
/// </summary>
public sealed record TileKind(
    string Id,
    string Directory,
    string Name,
    string Description,
    string Connector,
    JsonElement Settings,
    KindDlls Dlls,
    string? Error,
    string Stamp,
    string DllStamp,
    double? Width = null,
    bool Compact = true)
{
    /// <summary>The connector built into the app; script kinds only need it to run their scripts.</summary>
    public const string ScriptConnectorId = "script";

    /// <summary>The connector a DLL in this folder declares for <see cref="Connector"/>; null for script kinds and broken kinds.</summary>
    public ConnectorDescriptor? Plugin =>
        Error is null && Connector != ScriptConnectorId ? Dlls.Connectors.GetValueOrDefault(Connector) : null;

    /// <summary>The DLLs on disk differ from the ones loaded at startup; only an app restart loads them.</summary>
    public bool NeedsRestart => DllStamp != Dlls.Stamp;

    /// <summary>
    /// Effective settings: this kind's settings with each top-level key of the instance's settings replacing the kind's
    /// key of the same name (shallow; new keys are appended). What connectors get and the script's TILE_SETTINGS_JSON.
    /// </summary>
    public JsonElement SettingsFor(JsonElement instanceSettings)
    {
        var merged = JsonNode.Parse(Settings.GetRawText()) as JsonObject ?? new JsonObject();
        if (instanceSettings.ValueKind == JsonValueKind.Object)
            foreach (var property in instanceSettings.EnumerateObject())
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }
}

/// <summary>Every kind in tiles\, by folder name (case-insensitive), and the folders that have no tile.json.</summary>
public sealed record KindTable(string TilesDir, IReadOnlyDictionary<string, TileKind> Kinds, IReadOnlySet<string> FoldersWithoutTileJson)
{
    public static KindTable Empty { get; } = new("",
        new Dictionary<string, TileKind>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public TileKind? Find(string id) => Kinds.GetValueOrDefault(id);

    /// <summary>Kinds whose DLLs changed on disk since startup, by id.</summary>
    public IReadOnlyList<string> DllsChanged =>
        Kinds.Values.Where(k => k.NeedsRestart).Select(k => k.Id).Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What a kind's tile.json says. Error names the file (and the line for JSON errors) when it cannot be used.</summary>
public sealed record TileJson(string Name, string Description, string Connector, JsonElement Settings, string? Error,
    double? Width = null, bool Compact = true)
{
    /// <summary>The range "width" accepts, in device-independent pixels.</summary>
    public const double MinWidth = 48, MaxWidth = 640;

    static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads dir\tile.json; messages call it tiles\&lt;id&gt;\tile.json. Never throws.</summary>
    public static TileJson Read(string dir, string id)
    {
        var shown = $@"tiles\{id}\{KindLoader.TileJsonName}";
        for (var attempt = 1; ; attempt++)
        {
            try { return Parse(File.ReadAllText(Path.Combine(dir, KindLoader.TileJsonName)), shown); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); } // an editor may hold the file while saving
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Broken($"Could not read {shown}: {ex.Message}"); }
        }
    }

    /// <summary>" (line N)" (1-based) for a JsonException's zero-based line number; nothing when the parser gave none.</summary>
    internal static string LineSuffix(long? zeroBasedLine) => zeroBasedLine is { } line ? $" (line {line + 1})" : "";

    public static TileJson Parse(string text, string shownPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            return Broken($"{shownPath} is not valid JSON: {ConfigParser.TrimJsonMessage(ex.Message)}{LineSuffix(ex.LineNumber)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Broken($"{shownPath} must be a JSON object.");
            if (Text(root, "name") is not { } name) return Broken($"{shownPath} needs a \"name\", shown in the + picker.");
            if (Text(root, "connector") is not { } connector)
                return Broken($"{shownPath} needs a \"connector\": \"script\" or a connector id declared by a DLL in its folder.");

            JsonElement settings;
            if (!root.TryGetProperty("settings", out var s)) settings = EmptyObject();
            else if (s.ValueKind == JsonValueKind.Object) settings = s.Clone();
            else return Broken($"{shownPath}: \"settings\" must be an object.");

            double? width = null;
            if (root.TryGetProperty("width", out var w))
            {
                if (w.ValueKind != JsonValueKind.Number || w.GetDouble() is < MinWidth or > MaxWidth)
                    return Broken($"{shownPath}: \"width\" must be a number from {MinWidth} to {MaxWidth}.");
                width = w.GetDouble();
            }

            var compact = true;
            if (root.TryGetProperty("compact", out var c))
            {
                if (c.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Broken($"{shownPath}: \"compact\" must be true or false.");
                compact = c.GetBoolean();
            }

            return new TileJson(name, Text(root, "description") ?? "", connector, settings, null, width, compact);
        }
    }

    static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    static TileJson Broken(string error) => new("", "", "", EmptyObject(), error);

    static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }
}
