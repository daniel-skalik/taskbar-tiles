using System.IO;
using System.Globalization;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Calendar;

namespace TaskbarTiles.Tests;

public class CalendarTests
{
    const string Address = "https://calendar.google.com/calendar/ical/me%40example.com/private-0123456789abcdef/basic.ics";
    static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // Shaped like Google's export: a weekday standup with a Meet link, one day skipped, one moved and one cancelled;
    // an all-day event; a meeting in UTC; one the owner declined; tomorrow's planning.
    const string Ics = """
        BEGIN:VCALENDAR
        PRODID:-//Google Inc//Google Calendar 70.9054//EN
        VERSION:2.0
        X-WR-TIMEZONE:Europe/Prague
        BEGIN:VTIMEZONE
        TZID:Europe/Prague
        BEGIN:DAYLIGHT
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        DTSTART:19700329T020000
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        END:DAYLIGHT
        BEGIN:STANDARD
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        DTSTART:19701025T030000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        DTSTART;TZID=Europe/Prague:20261102T100000
        DTEND;TZID=Europe/Prague:20261102T101500
        RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR
        EXDATE;TZID=Europe/Prague:20261125T100000
        UID:standup@google.com
        SUMMARY:Standup
        X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;TZID=Europe/Prague:20261126T113000
        DTEND;TZID=Europe/Prague:20261126T114500
        RECURRENCE-ID;TZID=Europe/Prague:20261126T100000
        UID:standup@google.com
        SUMMARY:Standup (moved)
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;TZID=Europe/Prague:20261124T100000
        DTEND;TZID=Europe/Prague:20261124T101500
        RECURRENCE-ID;TZID=Europe/Prague:20261124T100000
        UID:standup@google.com
        SUMMARY:Standup
        X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;TZID=Europe/Prague:20261127T100000
        DTEND;TZID=Europe/Prague:20261127T101500
        RECURRENCE-ID;TZID=Europe/Prague:20261127T100000
        UID:standup@google.com
        STATUS:CANCELLED
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261124
        DTEND;VALUE=DATE:20261125
        UID:holiday@google.com
        SUMMARY:Company holiday
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261124T093000Z
        DTEND:20261124T101500Z
        UID:review@google.com
        SUMMARY:Design review
        DESCRIPTION:Join: https://acme.zoom.us/j/123456789?pwd=abc\nhttps://www.google.com/calendar/event?eid=xyz
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:me@example.com
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261124T103000Z
        DTEND:20261124T110000Z
        UID:petr@google.com
        SUMMARY:1:1 Petr
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261124T073000Z
        DTEND:20261124T080000Z
        UID:declined@google.com
        SUMMARY:Breakfast sync
        ATTENDEE;PARTSTAT=DECLINED:mailto:me@example.com
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;TZID=Europe/Prague:20261125T090000
        DTEND;TZID=Europe/Prague:20261125T100000
        UID:planning@google.com
        SUMMARY:Planning
        END:VEVENT
        END:VCALENDAR
        """;

    static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(1)); // Prague in November

    static Agenda Agenda(DateTimeOffset now)
    {
        var from = At(23, 0, 0);
        var events = CalendarParse.Parse(Ics, "me@example.com", from, At(27, 0, 0), Prague);
        return new Agenda(events, now, []);
    }

    static CalendarView View(DateTimeOffset now) => new(now, Prague, Culture);

    [Fact]
    public void Parse_expands_repeats_applies_skips_and_moves_and_drops_cancelled_and_declined()
    {
        var events = CalendarParse.Parse(Ics, "me@example.com", At(24, 0, 0), At(27, 0, 0), Prague);
        var names = events.Select(e => $"{e.Start.ToOffset(TimeSpan.FromHours(1)):dd HH:mm} {e.Title}").ToList();
        Assert.Equal(new[]
        {
            "24 00:00 Company holiday",
            "24 10:00 Standup",
            "24 10:30 Design review",
            "24 11:30 1:1 Petr",
            "25 09:00 Planning",
            "26 11:30 Standup (moved)",
        }, names);
        Assert.True(events[0].AllDay);
        Assert.Equal(At(25, 0, 0), events[0].End);
        Assert.Equal("https://meet.google.com/abc-defg-hij", events[1].Meet);
        Assert.Equal("https://acme.zoom.us/j/123456789?pwd=abc", events[2].Meet);
        Assert.Equal("https://www.google.com/calendar/event?eid=xyz", events[2].Link);
    }

    [Fact]
    public void Parse_keeps_an_event_declined_by_someone_else()
    {
        var events = CalendarParse.Parse(Ics, "other@example.com", At(24, 0, 0), At(25, 0, 0), Prague);
        Assert.Contains(events, e => e.Title == "Breakfast sync");
    }

    [Fact]
    public void Tile_counts_down_to_the_next_meeting()
    {
        var now = At(24, 9, 48);
        var tile = View(now).Tile(Agenda(now), null, null);
        Assert.Equal("Standup in 12 min", tile.Title);
        Assert.Equal("10:00–10:15 · 2 more today", tile.Subtitle);
        Assert.Equal(TileColor.Accent, tile.Icon!.Color);
        Assert.Null(tile.Progress);
    }

    [Fact]
    public void Tile_turns_orange_five_minutes_before()
    {
        var now = At(24, 9, 56);
        var tile = View(now).Tile(Agenda(now), null, null);
        Assert.Equal("Standup in 4 min", tile.Title);
        Assert.Equal(TileColor.Warn, tile.Icon!.Color);
    }

    [Fact]
    public void Tile_shows_the_meeting_in_progress_with_a_bar()
    {
        var now = At(24, 10, 40);
        var tile = View(now).Tile(Agenda(now), null, null);
        Assert.Equal("Design review · 35 min left", tile.Title);
        Assert.Equal("Next: 1:1 Petr at 11:30", tile.Subtitle);
        Assert.Equal(10.0 / 45, tile.Progress!.Value, 3);
    }

    [Fact]
    public void Tile_points_at_tomorrow_when_the_day_is_done()
    {
        var now = At(24, 12, 30);
        var tile = View(now).Tile(Agenda(now), null, null);
        Assert.Equal("No more events", tile.Title);
        Assert.Equal("Tomorrow 09:00 · Planning", tile.Subtitle);
        Assert.Equal(TileColor.Neutral, tile.Icon!.Color);
    }

    [Fact]
    public void Tile_shortens_a_long_title_but_keeps_the_countdown()
    {
        var now = At(24, 9, 0);
        var agenda = new Agenda([new CalendarEvent("Quarterly business review with the whole team", At(24, 9, 30), At(24, 10, 0), false)], now, []);
        var title = View(now).Tile(agenda, null, null).Title!;
        Assert.EndsWith("… in 30 min", title);
        Assert.True(title.Length <= 28, title);
    }

    [Fact]
    public void Tile_says_at_for_a_meeting_hours_away()
    {
        var now = At(24, 8, 0);
        var agenda = new Agenda([new CalendarEvent("Lunch", At(24, 12, 0), At(24, 13, 0), false)], now, []);
        Assert.Equal("Lunch at 12:00", View(now).Tile(agenda, null, null).Title);
    }

    [Theory]
    [InlineData(0.2, "1 min")]
    [InlineData(12, "12 min")]
    [InlineData(60, "1 h")]
    [InlineData(80, "1 h 20 min")]
    public void Span_reads_well(double minutes, string expected) => Assert.Equal(expected, CalendarView.Span(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Popup_lists_today_then_tomorrow_and_offers_to_join_the_next_call()
    {
        var now = At(24, 9, 48);
        var flyout = View(now).Flyout(Agenda(now), null, null);
        Assert.Equal("Today · Tue 24 Nov", flyout.Title);
        Assert.Equal("4 events today · updated 09:48", flyout.Subtitle);
        Assert.Equal(new[]
        {
            "Company holiday|All day",
            "Standup|10:00–10:15 · Meet",
            "Design review|10:30–11:15 · Zoom",
            "1:1 Petr|11:30–12:00",
            "Tomorrow|Wed 25 Nov",
            "Planning|09:00–10:00",
            "Add a calendar|",
        }, flyout.Rows.Select(r => $"{r.Text}|{r.Value}"));
        Assert.Equal(TileColor.Warn, flyout.Rows[1].Icon!.Color);
        Assert.Equal("open:https://meet.google.com/abc-defg-hij", flyout.Rows[1].Action);
        Assert.Equal("open:https://calendar.google.com/calendar/r/day/2026/11/25", flyout.Rows[5].Action);
        var join = Assert.Single(flyout.Actions, a => a.Primary);
        Assert.Equal("Join Standup", join.Label);
        Assert.Equal("open:https://meet.google.com/abc-defg-hij", join.Id);
    }

    [Fact]
    public void The_bundled_kind_ships_its_calendar_reader()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "tiles", "google-calendar");
        foreach (var file in new[] { "tile.json", "TaskbarTiles.Plugins.Calendar.dll", "TaskbarTiles.Plugins.Calendar.deps.json", "Ical.Net.dll", "NodaTime.dll" })
            Assert.True(File.Exists(Path.Combine(dir, file)), file);
        Assert.False(File.Exists(Path.Combine(dir, "TaskbarTiles.Contracts.dll")), "the host provides Contracts");
    }

    [Fact]
    public void Address_must_be_a_google_calendar_ical_address()
    {
        Assert.Equal(Address, CalendarAddress.Normalize($"  {Address}\n"));
        Assert.Null(CalendarAddress.Normalize("https://evil.example.com/calendar/ical/x/private-00/basic.ics"));
        Assert.Null(CalendarAddress.Normalize("http://calendar.google.com/calendar/ical/x/private-00/basic.ics"));
        Assert.Equal("me@example.com", CalendarAddress.Owner(Address));
    }

    sealed class MemoryStore(params string[] initial) : ICalendarStore
    {
        public List<string> Saved { get; } = [.. initial];
        public IReadOnlyList<string> Read() => Saved.ToList();
        public void Write(IReadOnlyList<string> addresses) { Saved.Clear(); Saved.AddRange(addresses); }
        public void Delete() => Saved.Clear();
    }

    sealed class FakeSource(Func<string, string> answer) : ICalendarSource
    {
        public List<string> Asked { get; } = [];
        public Task<string> GetAsync(string address, CancellationToken ct)
        {
            lock (Asked) Asked.Add(address);
            return Task.FromResult(answer(address));
        }
    }

    static (CalendarConnector Connector, TestTileContext Ctx, List<string> Opened) Start(MemoryStore store, ICalendarSource source, DateTimeOffset now)
    {
        var opened = new List<string>();
        var connector = new CalendarConnector(store, source, () => now, Prague, Culture, opened.Add);
        var ctx = new TestTileContext();
        connector.StartAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        return (connector, ctx, opened);
    }

    [Fact]
    public async Task Without_a_calendar_the_popup_asks_for_the_secret_address()
    {
        var (connector, ctx, _) = Start(new MemoryStore(), new FakeSource(_ => Ics), At(24, 9, 48));
        await using var _c = connector;
        Assert.Equal("Click to add a calendar", ctx.Updates[0].Subtitle);
        var flyout = (await connector.GetFlyoutAsync(CancellationToken.None))!;
        Assert.Equal("Add a calendar", flyout.Subtitle);
        Assert.True(flyout.Input!.Secret);
        Assert.Equal("add", flyout.Input.Action);
    }

    [Fact]
    public async Task Adding_a_calendar_checks_it_saves_it_and_shows_the_next_meeting()
    {
        var store = new MemoryStore();
        var (connector, ctx, _) = Start(store, new FakeSource(_ => Ics), At(24, 9, 48));
        await using var _c = connector;

        await connector.OnInputAsync("add", "https://example.com/not-a-calendar.ics", CancellationToken.None);
        Assert.Equal("That is not a Google Calendar iCal address", (await connector.GetFlyoutAsync(CancellationToken.None))!.Subtitle);
        Assert.Empty(store.Saved);

        await connector.OnInputAsync("add", Address, CancellationToken.None);
        Assert.Equal([Address], store.Saved);
        Assert.Equal("Standup in 12 min", ctx.Updates[^1].Title);
        Assert.Equal("Today · Tue 24 Nov", (await connector.GetFlyoutAsync(CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task A_calendar_that_cannot_be_read_is_not_saved_and_its_address_is_never_shown_or_logged()
    {
        var store = new MemoryStore();
        var source = new FakeSource(_ => throw new CalendarFetchException("not found (404); the address may have been reset"));
        var (connector, ctx, _) = Start(store, source, At(24, 9, 48));
        await using var _c = connector;

        await connector.OnInputAsync("add", Address, CancellationToken.None);
        var flyout = (await connector.GetFlyoutAsync(CancellationToken.None))!;
        Assert.Equal("Calendar 1: not found (404); the address may have been reset", flyout.Subtitle);
        Assert.Empty(store.Saved);

        var everything = string.Join("\n", ctx.Logged.Snapshot().Concat(ctx.Updates.Select(u => $"{u.Title} {u.Subtitle} {u.Tooltip}"))
            .Concat(ctx.Errors.Select(e => e.Message + e.Details)));
        Assert.DoesNotContain("private-", everything);
        Assert.DoesNotContain("0123456789abcdef", everything);
    }

    [Fact]
    public async Task One_calendar_failing_still_shows_the_others_and_says_which_failed()
    {
        const string second = "https://calendar.google.com/calendar/ical/team%40example.com/private-fedcba9876543210/basic.ics";
        var source = new FakeSource(a => a == second ? throw new CalendarFetchException("Google Calendar answered 500") : Ics);
        var (connector, ctx, _) = Start(new MemoryStore(Address, second), source, At(24, 9, 48));
        await using var _c = connector;
        await connector.FetchNowAsync(CancellationToken.None);
        var tile = ctx.Updates[^1];
        Assert.Equal("Standup in 12 min", tile.Title);
        Assert.Contains("Calendar 2: Google Calendar answered 500", tile.Tooltip);
    }

    [Fact]
    public async Task Forget_removes_the_calendars()
    {
        var store = new MemoryStore(Address);
        var (connector, ctx, _) = Start(store, new FakeSource(_ => Ics), At(24, 9, 48));
        await using var _c = connector;
        await connector.FetchNowAsync(CancellationToken.None);

        await connector.OnActionAsync("forget", CancellationToken.None);
        Assert.Empty(store.Saved);
        Assert.Equal("Click to add a calendar", ctx.Updates[^1].Subtitle);
    }

    [Fact]
    public async Task Opens_only_calendar_pages_and_call_links()
    {
        var (connector, _, opened) = Start(new MemoryStore(), new FakeSource(_ => Ics), At(24, 9, 48));
        await using var _c = connector;
        await connector.OnActionAsync("open:https://meet.google.com/abc-defg-hij", CancellationToken.None);
        await connector.OnActionAsync("open:https://acme.zoom.us/j/1", CancellationToken.None);
        await connector.OnActionAsync("open:https://evil.example.com/", CancellationToken.None);
        await connector.OnActionAsync("open:file:///C:/Windows/notepad.exe", CancellationToken.None);
        Assert.Equal(["https://meet.google.com/abc-defg-hij", "https://acme.zoom.us/j/1"], opened);
    }
}
