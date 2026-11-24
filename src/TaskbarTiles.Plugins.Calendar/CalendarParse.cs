using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace TaskbarTiles.Plugins.Calendar;

/// <summary>
/// Reads an iCalendar file into the occurrences in a time range, with repeating events expanded, skipped and moved
/// occurrences applied, and cancelled events and events <paramref name="owner"/> declined left out.
/// </summary>
public static partial class CalendarParse
{
    [GeneratedRegex(@"https://(?:meet\.google\.com/[a-z0-9-]+|[\w.-]*zoom\.us/j/[^\s""<>\\]+|teams\.microsoft\.com/l/meetup-join/[^\s""<>\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CallLink();

    [GeneratedRegex(@"https://(?:www|calendar)\.google\.com/calendar/event\?[^\s""<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex EventLink();

    /// <summary>The occurrences that overlap [<paramref name="from"/>, <paramref name="to"/>), by start. Throws on a file it cannot read.</summary>
    public static IReadOnlyList<CalendarEvent> Parse(string ics, string? owner, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var calendar = IcalCalendar.Load(ics) ?? throw new FormatException("Not an iCalendar file.");
        // Occurrences are found by their start, so start a day early to catch ones still running at "from".
        var start = new CalDateTime(from.UtcDateTime.AddDays(-1), "UTC", true);
        var end = new CalDateTime(to.UtcDateTime, "UTC", true);
        var events = new List<CalendarEvent>();
        foreach (var occurrence in calendar.GetOccurrences(start, null).TakeWhileBefore(end))
        {
            if (occurrence.Source is not Ical.Net.CalendarComponents.CalendarEvent e) continue;
            if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            if (Declined(e, owner)) continue;

            var period = occurrence.Period;
            var allDay = !period.StartTime.HasTime;
            var (s, f) = allDay
                ? (Midnight(period.StartTime.Date, zone), Midnight(period.EffectiveEndTime?.Date ?? period.StartTime.Date.AddDays(1), zone))
                : (new DateTimeOffset(period.StartTime.AsUtc, TimeSpan.Zero), new DateTimeOffset((period.EffectiveEndTime ?? period.StartTime).AsUtc, TimeSpan.Zero));
            if (f <= from || s >= to) continue;

            var text = $"{e.Location} {e.Description}";
            var meet = e.Properties["X-GOOGLE-CONFERENCE"]?.Value?.ToString() is { Length: > 0 } conference && CallLink().IsMatch(conference)
                ? conference
                : CallLink().Match(text) is { Success: true } call ? call.Value : null;
            var link = e.Url?.ToString() is { } url && EventLink().IsMatch(url) ? url
                : EventLink().Match(text) is { Success: true } found ? found.Value : null;
            events.Add(new CalendarEvent(string.IsNullOrWhiteSpace(e.Summary) ? "(No title)" : e.Summary.Trim(), s, f, allDay, meet, link));
        }
        return events.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
    }

    static bool Declined(Ical.Net.CalendarComponents.CalendarEvent e, string? owner) =>
        owner is not null && e.Attendees.Any(a =>
            string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Value?.ToString().Replace("mailto:", "", StringComparison.OrdinalIgnoreCase), owner, StringComparison.OrdinalIgnoreCase));

    static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
