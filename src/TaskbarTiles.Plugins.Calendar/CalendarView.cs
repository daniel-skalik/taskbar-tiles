using System.Globalization;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Calendar;

/// <summary>Turns the fetched events into the tile and its popup, for a given moment, time zone and culture.</summary>
public sealed class CalendarView(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
{
    public const string Title = "Google Calendar";
    public const string SettingsPage = "https://calendar.google.com/calendar/r/settings";
    const int TitleRoom = 28; // characters that fit on the title line of a 240 DIP tile
    static readonly TimeSpan Soon = TimeSpan.FromMinutes(5);
    static readonly TimeSpan CountdownLimit = TimeSpan.FromHours(3); // later than this: "at 15:00" instead of a countdown

    DateTime Today => Local(now).Date;

    DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).DateTime;
    string Time(DateTimeOffset t) => Local(t).ToString("t", culture);
    string Range(CalendarEvent e) => $"{Time(e.Start)}–{Time(e.End)}";
    bool On(CalendarEvent e, DateTime day) => e.AllDay
        ? Local(e.Start).Date <= day && Local(e.End).Date > day
        : Local(e.Start).Date == day;

    /// <summary>Events that have not ended, with a time, by start.</summary>
    IEnumerable<CalendarEvent> Timed(Agenda a) => a.Events.Where(e => !e.AllDay && e.End > now).OrderBy(e => e.Start);

    /// <summary>The meeting you are in: of those running now, the one that started last.</summary>
    public CalendarEvent? Current(Agenda a) => Timed(a).Where(e => e.Start <= now).OrderByDescending(e => e.Start).FirstOrDefault();

    /// <summary>The next meeting today.</summary>
    public CalendarEvent? Next(Agenda a) => Timed(a).FirstOrDefault(e => e.Start > now && Local(e.Start).Date == Today);

    public TileState Tile(Agenda a, byte[]? logo, string? stale)
    {
        var current = Current(a);
        var next = Next(a);
        string title, subtitle;
        var color = TileColor.Accent;
        double? progress = null;

        if (current is not null)
        {
            title = Fit(current.Title, $" · {Span(current.End - now)} left");
            subtitle = next is not null ? $"Next: {next.Title} at {Time(next.Start)}" : $"Until {Time(current.End)}";
            var length = (current.End - current.Start).TotalSeconds;
            progress = length > 0 ? Math.Clamp((now - current.Start).TotalSeconds / length, 0, 1) : null;
        }
        else if (next is not null)
        {
            var wait = next.Start - now;
            title = Fit(next.Title, wait < TimeSpan.FromMinutes(1) ? " now" : wait > CountdownLimit ? $" at {Time(next.Start)}" : $" in {Span(wait)}");
            var more = Timed(a).Count(e => e.Start > next.Start && Local(e.Start).Date == Today);
            subtitle = Range(next) + (more > 0 ? $" · {more} more today" : next.Meet is not null ? " · " + CallName(next.Meet) : "");
            if (wait <= Soon) color = TileColor.Warn;
        }
        else
        {
            title = "No more events";
            var tomorrow = Timed(a).FirstOrDefault(e => Local(e.Start).Date == Today.AddDays(1));
            subtitle = tomorrow is not null ? $"Tomorrow {Time(tomorrow.Start)} · {tomorrow.Title}" : "Nothing tomorrow";
            color = TileColor.Neutral;
        }

        var notes = a.Problems.ToList();
        if (stale is not null) notes.Insert(0, stale);
        return new TileState
        {
            Title = title,
            Subtitle = subtitle,
            Icon = new TileIcon("clock", color),
            Image = logo,
            ImageColor = logo is null ? null : color,
            Progress = progress,
            Tooltip = notes.Count > 0 ? string.Join("\n", notes) : $"{title}\n{subtitle}",
        };
    }

    public FlyoutState Flyout(Agenda a, byte[]? logo, string? stale)
    {
        var rows = new List<FlyoutRow>();
        var current = Current(a);
        var next = Next(a);

        FlyoutRow Row(CalendarEvent e)
        {
            var color = e.AllDay ? TileColor.Accent
                : e.End <= now ? TileColor.Neutral
                : e == current ? TileColor.Ok
                : e == next ? TileColor.Warn
                : TileColor.Accent;
            var value = e.AllDay ? "All day" : Range(e);
            if (e.Meet is not null && e.End > now) value += " · " + CallName(e.Meet);
            return new FlyoutRow(e.Title, value, new TileIcon("dot", color), "open:" + (e.Meet is not null && e.End > now ? e.Meet : e.Link ?? DayPage(e.Start)));
        }

        IEnumerable<CalendarEvent> Day(DateTime day) => a.Events.Where(e => On(e, day)).OrderBy(e => !e.AllDay).ThenBy(e => e.Start);

        var today = Day(Today).ToList();
        rows.AddRange(today.Select(Row));
        if (today.Count == 0) rows.Add(new FlyoutRow("Nothing today"));
        var tomorrow = Day(Today.AddDays(1)).ToList();
        rows.Add(new FlyoutRow("Tomorrow", Today.AddDays(1).ToString("ddd d MMM", culture)));
        rows.AddRange(tomorrow.Select(Row));
        if (tomorrow.Count == 0) rows.Add(new FlyoutRow("Nothing tomorrow"));

        var subtitle = stale ?? a.Problems.FirstOrDefault()
            ?? $"{Count(today.Count)} · updated {Time(a.Updated)}";

        var actions = new List<FlyoutAction>();
        var call = current?.Meet is not null ? current : next?.Meet is not null ? next : null;
        if (call is not null) actions.Add(new FlyoutAction("open:" + call.Meet, Fit("Join " + call.Title, "", 24), Primary: true));
        actions.Add(new FlyoutAction("open:" + DayPage(now), "Open calendar"));

        return new FlyoutState($"Today · {Today.ToString("ddd d MMM", culture)}", subtitle, rows, actions, Image: logo);
    }

    /// <summary>The tile before a calendar is added, or after Settings › Add calendar until the address is pasted.</summary>
    public static TileState SetupTile(byte[]? logo, bool another = false) => new()
    {
        Title = Title,
        Subtitle = another ? "Click to add another calendar" : "Click to add a calendar",
        Icon = new TileIcon("clock"),
        Image = logo,
        ImageColor = logo is null ? null : TileColor.Neutral,
    };

    /// <summary>The popup that takes a calendar's secret address; <paramref name="message"/> says why the last one was refused.</summary>
    public static FlyoutState SetupFlyout(byte[]? logo, string? message, bool hasCalendars) => new(
        Title,
        message ?? (hasCalendars ? "Add another calendar" : "Add a calendar"),
        [
            new FlyoutRow("1. Open Google Calendar settings", Action: "open:" + SettingsPage),
            new FlyoutRow("2. Pick your calendar › Integrate calendar"),
            new FlyoutRow("3. Copy \"Secret address in iCal format\""),
            new FlyoutRow("4. Paste it below and press Enter"),
        ],
        hasCalendars ? [new FlyoutAction("cancel", "Cancel")] : [],
        Image: logo,
        Input: new FlyoutInput("add", "Secret address in iCal format", Secret: true),
        StayOpen: true);

    static string Count(int n) => n switch { 0 => "No events today", 1 => "1 event today", _ => $"{n} events today" };

    static string CallName(string url) =>
        url.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) ? "Zoom"
        : url.Contains("teams.microsoft.com", StringComparison.OrdinalIgnoreCase) ? "Teams"
        : "Meet";

    string DayPage(DateTimeOffset t)
    {
        var day = Local(t);
        return $"https://calendar.google.com/calendar/r/day/{day.Year}/{day.Month}/{day.Day}";
    }

    /// <summary>"8 min", "1 h 20 min", "2 h"; under a minute counts as 1 min.</summary>
    public static string Span(TimeSpan t)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(t.TotalMinutes - 0.001));
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>The event title shortened with "…" so that <paramref name="suffix"/> still fits on the line.</summary>
    static string Fit(string text, string suffix, int room = TitleRoom)
    {
        var left = Math.Max(8, room - suffix.Length);
        return (text.Length > left ? text[..(left - 1)].TrimEnd() + "…" : text) + suffix;
    }
}
