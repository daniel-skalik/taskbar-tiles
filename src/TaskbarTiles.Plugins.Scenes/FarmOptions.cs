using System.Globalization;
using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Scenes;

/// <summary>A time of day for the farmer's hours: a clock time, or sunrise or sunset on that day.</summary>
public readonly record struct DayTime(DayTime.Anchor From, double Hours = 0)
{
    public enum Anchor { Clock, Sunrise, Sunset }

    public static DayTime Sunrise => new(Anchor.Sunrise);
    public static DayTime Sunset => new(Anchor.Sunset);
    public static DayTime At(double hours) => new(Anchor.Clock, hours);

    /// <summary>The hour of the day this is on a day with <paramref name="sun"/>.</summary>
    public double On(SunTimes.Day sun) => From switch
    {
        Anchor.Sunrise => sun.Sunrise,
        Anchor.Sunset => sun.Sunset,
        _ => Hours,
    };

    /// <summary>"sunrise", "sunset" or a 24-hour clock time such as "6:45" or "18:30", ignoring case and spaces.</summary>
    public static bool TryParse(string? text, out DayTime value)
    {
        value = default;
        var t = text?.Trim() ?? "";
        if (t.Equals("sunrise", StringComparison.OrdinalIgnoreCase)) { value = Sunrise; return true; }
        if (t.Equals("sunset", StringComparison.OrdinalIgnoreCase)) { value = Sunset; return true; }
        if (!TimeOnly.TryParseExact(t, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
        value = At(time.ToTimeSpan().TotalHours);
        return true;
    }
}

/// <summary>
/// Where the farm is, for its sunrise and sunset, and the farmer's working hours. From the tile settings "latitude" and
/// "longitude" (degrees; default: the Windows region, see <see cref="HomeLocation"/>), "workStart" and "workEnd"
/// ("sunrise", "sunset" or "H:mm"; default sunrise to sunset).
/// </summary>
public sealed record FarmOptions(double Latitude, double Longitude, DayTime WorkStart, DayTime WorkEnd, TimeZoneInfo Zone)
{
    public const string LatitudeKey = "latitude", LongitudeKey = "longitude", WorkStartKey = "workStart", WorkEndKey = "workEnd";

    /// <summary>Central Europe, for when Windows has no region either.</summary>
    const double FallbackLatitude = 50, FallbackLongitude = 15;

    public static FarmOptions Default { get; } = new(FallbackLatitude, FallbackLongitude, DayTime.Sunrise, DayTime.Sunset, TimeZoneInfo.Local);

    /// <summary>Reads the settings; bad values are logged and the default used.</summary>
    public static FarmOptions FromSettings(JsonElement settings, ITileLog log, Func<(double, double)?>? region = null)
    {
        var (lat, lon) = (region ?? HomeLocation.FromWindowsRegion)() ?? (FallbackLatitude, FallbackLongitude);
        var latitude = Number(settings, LatitudeKey, -90, 90, log) ?? lat;
        var longitude = Number(settings, LongitudeKey, -180, 180, log) ?? lon;
        return new FarmOptions(latitude, longitude,
            Time(settings, WorkStartKey, DayTime.Sunrise, log), Time(settings, WorkEndKey, DayTime.Sunset, log), TimeZoneInfo.Local);
    }

    /// <summary>The sun on the day of <paramref name="now"/> (local time in <see cref="Zone"/>).</summary>
    public SunTimes.Day SunOn(DateTime now) =>
        SunTimes.For(DateOnly.FromDateTime(now), Latitude, Longitude, Zone.GetUtcOffset(now.Date.AddHours(12)));

    static double? Number(JsonElement settings, string key, double min, double max, ITileLog log)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        double number;
        if (v.ValueKind == JsonValueKind.Number) number = v.GetDouble();
        else if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;
        else if (v.ValueKind != JsonValueKind.String || !double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            number = double.NaN;
        if (number >= min && number <= max) return number;
        log.Warn($"\"{key}\" must be a number from {min} to {max}; using the Windows region instead.");
        return null;
    }

    static DayTime Time(JsonElement settings, string key, DayTime fallback, ITileLog log)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return fallback;
        if (v.ValueKind == JsonValueKind.String && DayTime.TryParse(v.GetString(), out var parsed)) return parsed;
        log.Warn($"\"{key}\" must be \"sunrise\", \"sunset\" or a time such as \"7:30\"; using {fallback.From.ToString().ToLowerInvariant()}.");
        return fallback;
    }
}
