using TaskbarTiles.Plugins.Media;

namespace TaskbarTiles.Tests;

public class MediaTests
{
    static readonly DateTimeOffset Updated = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Song = TimeSpan.FromMinutes(4);

    static double? Progress(double positionSeconds, bool playing, double secondsLater) =>
        MediaConnector.Progress(TimeSpan.Zero, Song, TimeSpan.FromSeconds(positionSeconds), Updated, playing, Updated.AddSeconds(secondsLater));

    [Fact]
    public void Playing_moves_on_from_the_last_reported_position() => Assert.Equal(0.5, Progress(60, playing: true, secondsLater: 60));

    [Fact]
    public void Paused_stays_at_the_reported_position() => Assert.Equal(0.25, Progress(60, playing: false, secondsLater: 60));

    [Fact]
    public void Progress_stays_between_zero_and_one()
    {
        Assert.Equal(1, Progress(200, playing: true, secondsLater: 600));
        Assert.Equal(0, Progress(-5, playing: false, secondsLater: 0));
    }

    [Fact]
    public void No_length_means_no_bar() =>
        Assert.Null(MediaConnector.Progress(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, Updated, true, Updated));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.ZuneMusic")]
    [InlineData("C:/Program Files/VideoLAN/vlc.EXE", "vlc")]
    [InlineData("", "")]
    public void App_names_are_short_and_still_match_the_session(string aumid, string name)
    {
        Assert.Equal(name, MediaConnector.AppName(aumid));
        Assert.Contains(name, aumid, StringComparison.OrdinalIgnoreCase); // "app" matches by containment
    }

    [Theory]
    [InlineData("308046B0AF4A39CB", "Firefox", "firefox", true)]                 // by its Start menu name
    [InlineData("Spotify.exe", null, "Spotify", true)]                           // by its id, as before
    [InlineData("MSEdge", "Microsoft Edge", "Edge", true)]
    [InlineData("Chrome", "Google Chrome", "Firefox", false)]
    [InlineData("308046B0AF4A39CB", null, "Firefox", false)]                     // no name known: only the id matches
    [InlineData("Spotify.exe", null, "", false)]
    public void A_session_matches_by_id_or_by_name(string aumid, string? name, string app, bool matches) =>
        Assert.Equal(matches, AppNames.Matches(aumid, name, app));

    [Fact]
    public void An_id_the_shell_does_not_know_has_no_name() =>
        Assert.Null(AppNames.DisplayName("TaskbarTiles.NoSuchApp.4F2A"));

    [Theory]
    [InlineData("Spotify", false, new[] { "Spotify" })]
    [InlineData(" Spotify , firefox;Chrome ", false, new[] { "Spotify", "firefox", "Chrome" })]
    [InlineData("Any", true, new string[0])]
    [InlineData("Spotify, *", true, new string[0])]
    [InlineData("", true, new string[0])]
    public void The_app_setting_is_one_app_several_or_any(string setting, bool any, string[] apps)
    {
        var filter = AppFilter.Parse(setting);
        Assert.Equal(any, filter.Any);
        Assert.Equal(apps, filter.Apps);
    }

    static readonly SessionInfo Spotify = new("Spotify.exe", null, Playing: false, Current: false);
    static readonly SessionInfo Firefox = new("308046B0AF4A39CB", "Firefox", Playing: false, Current: false);
    static readonly SessionInfo Chrome = new("Chrome", "Google Chrome", Playing: false, Current: false);

    [Fact]
    public void Of_several_apps_the_one_playing_wins_then_the_one_listed_first()
    {
        var filter = AppFilter.Parse("Spotify, Firefox");

        Assert.Equal(0, filter.Choose([Spotify, Firefox]));                           // neither plays: listed first
        Assert.Equal(1, filter.Choose([Spotify, Firefox with { Playing = true }]));   // Firefox plays
        Assert.Equal(1, filter.Choose([Chrome with { Playing = true }, Spotify]));    // Chrome is not listed
        Assert.Equal(-1, filter.Choose([Chrome]));
        Assert.True(filter.Several);
        Assert.False(AppFilter.Parse("Spotify").Several);
    }

    [Fact]
    public void Any_follows_whatever_plays_else_the_current_session()
    {
        var any = AppFilter.Parse("Any");

        Assert.Equal(2, any.Choose([Spotify, Firefox, Chrome with { Playing = true }]));
        Assert.Equal(1, any.Choose([Spotify, Firefox with { Current = true }]));
        Assert.Equal(-1, any.Choose([]));
        Assert.Equal("Media", any.Label);
    }
}
