using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Calendar;

/// <summary>
/// One occurrence of an event. All-day events start and end at midnight in the local time zone.
/// <paramref name="Meet"/> is the video call link (Google Meet, Zoom, Teams); <paramref name="Link"/> the event's own page,
/// when the calendar gives one.
/// </summary>
public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string? Meet = null, string? Link = null);

/// <summary>
/// The last fetch: every calendar's events from yesterday to the end of tomorrow, and what could not be read
/// ("Calendar 2: not found (404)"), shown in the tooltip and the popup while the rest still shows.
/// </summary>
public sealed record Agenda(IReadOnlyList<CalendarEvent> Events, DateTimeOffset Updated, IReadOnlyList<string> Problems)
{
    public static readonly Agenda Empty = new([], DateTimeOffset.MinValue, []);
}

/// <summary>How often to fetch ("refresh", 1 to 60 minutes, default 5).</summary>
public sealed record CalendarSettings(TimeSpan Refresh)
{
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(5), MinRefresh = TimeSpan.FromMinutes(1), MaxRefresh = TimeSpan.FromMinutes(60);

    public static CalendarSettings Read(JsonElement settings, ITileLog? log = null)
    {
        var refresh = DefaultRefresh;
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.String)
        {
            if (Duration.TryParse(r.GetString(), out var parsed)) refresh = parsed < MinRefresh ? MinRefresh : parsed > MaxRefresh ? MaxRefresh : parsed;
            else log?.Warn($"\"refresh\" must be a duration such as \"5m\"; using {DefaultRefresh.TotalMinutes:0}m.");
        }
        return new CalendarSettings(refresh);
    }
}

/// <summary>Google Calendar's iCal addresses: .../calendar/ical/&lt;calendar id&gt;/private-&lt;key&gt;/basic.ics (or public/basic.ics).</summary>
public static partial class CalendarAddress
{
    [GeneratedRegex(@"^https://calendar\.google\.com/calendar/ical/([^/\s?#]+)/(?:private-[0-9a-f]+|public)/basic\.ics$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>The address as typed, trimmed; null when it is not a Google Calendar iCal address.</summary>
    public static string? Normalize(string text)
    {
        var trimmed = text.Trim().Trim('"', '\'', '<', '>');
        return Pattern().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>The calendar's id, which for your own calendar is your email: the attendee whose answer counts.</summary>
    public static string? Owner(string address) =>
        Pattern().Match(address) is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
}
