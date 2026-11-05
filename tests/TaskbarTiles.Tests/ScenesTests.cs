using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Scenes;

namespace TaskbarTiles.Tests;

public class ScenesTests
{
    static readonly DateTime Noon = new(2026, 6, 1, 12, 0, 0);
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>Prague without daylight saving, so tests give the same sun anywhere they run.</summary>
    static readonly FarmOptions Prague = new(50.08, 14.42, DayTime.Sunrise, DayTime.Sunset,
        TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1"));

    static readonly Dictionary<string, Func<Random, IScene>> Scenes = new()
    {
        ["Aquarium"] = rng => new AquariumScene(rng),
        ["Snake"] = rng => new SnakeScene(rng),
        ["Racers"] = rng => new RacersScene(rng),
        ["Cozy farm"] = rng => new FarmScene(rng, Prague),
    };
    public static TheoryData<string> SceneNames() => new(Scenes.Keys);

    static readonly Dictionary<string, Func<Random, int, IScene>> SizedScenes = new()
    {
        ["Aquarium"] = (rng, w) => new AquariumScene(rng, w),
        ["Snake"] = (rng, w) => new SnakeScene(rng, w / 2),
        ["Racers"] = (rng, w) => new RacersScene(rng, w),
        ["Cozy farm"] = (rng, w) => new FarmScene(rng, Prague, w),
    };

    public static TheoryData<string, int> ScenesAtEveryWidth()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in SizedScenes.Keys)
            foreach (var width in new[] { PixelCanvas.MinWidth, 24, 37, 55, 80, 100, 180, 260, PixelCanvas.MaxWidth }) data.Add(name, width);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenesAtEveryWidth))]
    public void Every_scene_runs_and_fills_its_frame_at_any_width(string name, int width)
    {
        var rng = new Random(width);
        var scene = SizedScenes[name](rng, width);
        var pixels = PixelCanvas.NewFrame(scene.Width);

        for (int step = 0; step < 600; step++)
        {
            scene.Step(Noon.AddMinutes(step * 3), rng); // through a whole day, for the farm
            if (step % 50 == 25) scene.OnClick(Noon, rng);
            Array.Clear(pixels);
            scene.Render(pixels);
        }
        Assert.InRange(scene.Width, width - 1, width); // the snake board is whole 2-pixel cells
        Assert.All(pixels, p => Assert.Equal(0xFF, (p >> 24) & 0xFF));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(200, 100)]
    [InlineData(302, 151)]
    [InlineData(48, 24)]
    [InlineData(10, 20)]
    [InlineData(5000, 320)]
    public void A_tile_width_maps_to_a_frame_width_of_2_dip_per_pixel(double tileWidth, int frameWidth) =>
        Assert.Equal(frameWidth, PixelCanvas.WidthFor(tileWidth));

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Every_scene_paints_every_pixel_of_every_frame(string name)
    {
        var rng = new Random(42);
        var scene = Scenes[name](rng);
        var pixels = PixelCanvas.NewFrame();

        for (int step = 0; step < 400; step++)
        {
            scene.Step(Noon.AddMilliseconds(step * 100), rng);
            if (step % 50 == 25) scene.OnClick(Noon, rng);
            Array.Clear(pixels);
            scene.Render(pixels);
            Assert.All(pixels, p => Assert.Equal(0xFF, (p >> 24) & 0xFF)); // opaque: nothing left unpainted
            Assert.False(string.IsNullOrEmpty(scene.Status));
        }
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Frames_change_over_time(string name)
    {
        var rng = new Random(1);
        var scene = Scenes[name](rng);
        var first = Frame(scene, rng, 40);
        var later = Frame(scene, rng, 40);

        Assert.NotEqual(first, later);
    }

    static int[] Frame(IScene scene, Random rng, int steps)
    {
        for (int i = 0; i < steps; i++) scene.Step(Noon, rng);
        var pixels = PixelCanvas.NewFrame();
        scene.Render(pixels);
        return pixels;
    }

    [Fact]
    public void Snake_never_moves_into_a_wall_or_itself_while_a_safe_move_exists()
    {
        var rng = new Random(7);
        var snake = new SnakeScene(rng, width: 12, height: 6, framesPerMove: 1);
        int deaths = 0, checkedMoves = 0;

        for (int step = 0; step < 5000; step++)
        {
            if (!snake.Alive) { snake.Step(Noon, rng); continue; }
            bool anySafe = Enumerable.Range(0, 4).Any(snake.IsSafe);
            int dir = snake.ChooseDirection();
            if (anySafe)
            {
                Assert.True(dir >= 0 && snake.IsSafe(dir), $"step {step}: chose {dir} although a safe move existed");
                checkedMoves++;
            }
            else Assert.Equal(-1, dir);

            int games = snake.Games;
            snake.Step(Noon, rng);
            if (!snake.Alive && snake.Games == games && snake.Length < snake.GridWidth * snake.GridHeight)
            {
                Assert.False(anySafe, $"step {step}: died with a safe move available");
                deaths++;
            }
        }
        Assert.True(checkedMoves > 1000);
    }

    [Fact]
    public void Snake_eats_and_its_best_score_is_kept()
    {
        var rng = new Random(3);
        var snake = new SnakeScene(rng, framesPerMove: 1);

        for (int i = 0; i < 400; i++) snake.Step(Noon, rng);

        Assert.True(snake.Score > 3, $"score {snake.Score}");
        Assert.Equal(snake.Score, snake.Best);
        Assert.Equal(3 + snake.Score, snake.Length);
        Assert.Contains($"score {snake.Score}", snake.Status);
    }

    [Fact]
    public void Snake_starts_over_after_dying_or_filling_the_board()
    {
        var rng = new Random(11);
        var snake = new SnakeScene(rng, width: 5, height: 3, framesPerMove: 1);

        int steps = 0;
        while (snake.Games < 2 && steps++ < 20_000) snake.Step(Noon, rng);

        Assert.Equal(2, snake.Games);
        Assert.True(snake.Alive);
        Assert.Equal(3, snake.Length);
        Assert.Equal(0, snake.Score);
        Assert.True(snake.Best > 0);
    }

    [Fact]
    public void Clicking_restarts_the_snake()
    {
        var rng = new Random(3);
        var snake = new SnakeScene(rng, framesPerMove: 1);
        for (int i = 0; i < 200; i++) snake.Step(Noon, rng);

        snake.OnClick(Noon, rng);

        Assert.Equal(2, snake.Games);
        Assert.Equal(0, snake.Score);
        Assert.True(snake.Best > 0);
    }

    [Fact]
    public void Racers_have_a_winner_then_line_up_for_the_next_race()
    {
        var rng = new Random(5);
        var racers = new RacersScene(rng);

        int steps = 0;
        while (racers.Races < 1 && steps++ < 3000) racers.Step(Noon, rng);

        Assert.Equal(RacersScene.RacePhase.Celebrating, racers.Phase);
        Assert.InRange(racers.Winner, 0, 3);
        Assert.Equal(1, racers.Wins.Sum());
        Assert.Equal(1, racers.Wins[racers.Winner]);
        Assert.Contains($"{RacersScene.CarNames[racers.Winner]} wins race 1!", racers.Status);

        steps = 0;
        while (racers.Phase != RacersScene.RacePhase.Countdown && steps++ < 500) racers.Step(Noon, rng);

        Assert.Equal(2, racers.RaceNumber);
        Assert.Equal(-1, racers.Winner);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(racers.CarX(0), racers.CarX(i)));
    }

    [Fact]
    public void Clicking_starts_a_new_race()
    {
        var rng = new Random(5);
        var racers = new RacersScene(rng);
        for (int i = 0; i < 100; i++) racers.Step(Noon, rng);

        racers.OnClick(Noon, rng);

        Assert.Equal(2, racers.RaceNumber);
        Assert.Equal(RacersScene.RacePhase.Countdown, racers.Phase);
    }

    [Fact]
    public void Farm_sky_follows_the_clock()
    {
        var noon = Frame(new FarmScene(new Random(4), Prague), Noon);
        var midnight = Frame(new FarmScene(new Random(4), Prague), Noon.Date);

        Assert.NotEqual(noon[50], midnight[50]);
        Assert.True(Brightness(noon, row: 0) > Brightness(midnight, row: 0) * 2);

        static int[] Frame(FarmScene scene, DateTime at)
        {
            var rng = new Random(4);
            for (int i = 0; i < 5; i++) scene.Step(at, rng);
            var pixels = PixelCanvas.NewFrame();
            scene.Render(pixels);
            return pixels;
        }
    }

    static int Brightness(int[] pixels, int row)
    {
        int sum = 0;
        for (int x = 0; x < PixelCanvas.DefaultWidth; x++)
        {
            int p = pixels[row * PixelCanvas.DefaultWidth + x];
            sum += ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255);
        }
        return sum;
    }

    [Fact]
    public void Farmer_sleeps_at_night_and_comes_out_in_the_morning()
    {
        var rng = new Random(4);
        var farm = new FarmScene(rng, Prague);

        farm.Step(Noon.Date.AddHours(2), rng);
        Assert.True(farm.FarmerInside);
        Assert.Contains("asleep", farm.Status);

        farm.Step(Noon.Date.AddHours(8), rng);
        Assert.False(farm.FarmerInside);
        Assert.StartsWith("morning", farm.Status);
    }

    [Theory]
    [InlineData(12, new[] { FarmSurprise.AppleShake, FarmSurprise.BloomWave, FarmSurprise.Hooray })]
    [InlineData(2, new[] { FarmSurprise.MeteorShower, FarmSurprise.FireflyDance })]
    public void Clicking_the_farm_plays_every_surprise_for_the_hour_never_the_same_twice_running(int hour, FarmSurprise[] expected)
    {
        var rng = new Random(9);
        var farm = new FarmScene(rng, Prague);
        var at = Noon.Date.AddHours(hour);
        farm.Step(at, rng);
        var seen = new List<FarmSurprise>();

        for (int click = 0; click < 40; click++)
        {
            farm.OnClick(at, rng);
            seen.Add(farm.LastSurprise!.Value);
            for (int i = 0; i < 5; i++) farm.Step(at, rng);
        }

        Assert.Equal(expected.Order(), seen.Distinct().Order());
        Assert.All(seen.Zip(seen.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Theory]
    [InlineData(FarmSurprise.AppleShake, 12)]
    [InlineData(FarmSurprise.BloomWave, 12)]
    [InlineData(FarmSurprise.Hooray, 12)]
    [InlineData(FarmSurprise.MeteorShower, 2)]
    [InlineData(FarmSurprise.FireflyDance, 2)]
    public void A_farm_surprise_is_plain_to_see_and_then_settles(FarmSurprise surprise, int hour)
    {
        // Two farms from the same seed; only one is surprised, so the pixels that differ are the surprise.
        var at = Noon.Date.AddHours(hour);
        Random calmRng = new(5), surprisedRng = new(5);
        FarmScene calm = new(calmRng, Prague), surprised = new(surprisedRng, Prague);
        for (int i = 0; i < 30; i++) { calm.Step(at, calmRng); surprised.Step(at, surprisedRng); }

        surprised.Play(surprise, new Random(1));
        Assert.True(surprised.Surprising);
        int mostChanged = 0;
        for (int i = 0; i < 15; i++)
        {
            calm.Step(at, calmRng);
            surprised.Step(at, surprisedRng);
            mostChanged = Math.Max(mostChanged, Render(calm).Zip(Render(surprised)).Count(p => p.First != p.Second));
        }
        Assert.True(mostChanged >= 10, $"only {mostChanged} pixels changed");

        for (int i = 0; i < 400; i++) surprised.Step(at, surprisedRng);
        Assert.False(surprised.Surprising);

        static int[] Render(FarmScene scene)
        {
            var pixels = PixelCanvas.NewFrame(scene.Width);
            scene.Render(pixels);
            return pixels;
        }
    }

    [Fact]
    public void Feeding_the_fish_drops_flakes_they_eat()
    {
        var rng = new Random(3);
        var tank = new AquariumScene(rng);

        tank.OnClick(Noon, rng);
        Assert.True(tank.FlakesFloating > 0);
        for (int i = 0; i < 400 && tank.Eaten == 0; i++) tank.Step(Noon, rng);

        Assert.True(tank.Eaten > 0);
        Assert.Contains("eaten", tank.Status);
    }

    [Fact]
    public void Canvas_blends_by_alpha_and_clips_at_the_edges()
    {
        var pixels = PixelCanvas.NewFrame();
        var canvas = new PixelCanvas(pixels);
        canvas.Clear(PixelCanvas.Rgb(0x000000));

        canvas.Set(0, 0, PixelCanvas.Rgba(0xFFFFFF, 128));
        canvas.Set(-1, 0, PixelCanvas.Rgb(0xFF0000));
        canvas.Set(PixelCanvas.DefaultWidth, PixelCanvas.Height - 1, PixelCanvas.Rgb(0xFF0000));
        canvas.Sprite(PixelCanvas.DefaultWidth - 2, 1, ["123"], [PixelCanvas.Rgb(0x0000FF), PixelCanvas.Rgb(0x00FF00), PixelCanvas.Rgb(0xFF0000)], flip: true);

        Assert.Equal(PixelCanvas.Rgb(0x808080), pixels[0]);
        Assert.Equal(PixelCanvas.Rgb(0xFF0000), canvas.Get(PixelCanvas.DefaultWidth - 2, 1)); // flipped: the last color comes first
        Assert.Equal(PixelCanvas.Rgb(0x00FF00), canvas.Get(PixelCanvas.DefaultWidth - 1, 1));
        Assert.Throws<ArgumentException>(() => new PixelCanvas(new int[10]));
    }

    sealed class Context : ITileContext
    {
        public string TileId => "scene";
        public JsonElement Settings { get; init; } = Json("{}");
        public string ConfigDirectory => "";
        readonly List<TileState> _updates = [];
        public TileState[] Updates() { lock (_updates) return _updates.ToArray(); }
        public void Update(TileState state) { lock (_updates) _updates.Add(state); }
        public void ReportError(string message, string? details = null) => throw new InvalidOperationException(message + " " + details);
        public event EventHandler? Resumed { add { } remove { } }
        public ITileLog Log { get; } = new CollectingLog();
    }

    [Fact]
    public async Task Connector_draws_frames_with_only_the_picture_and_a_tooltip()
    {
        var connector = new SnakeConnector(new Random(1), () => Noon);
        var ctx = new Context();

        await connector.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates().Length >= 3);
        await connector.DisposeAsync();
        await connector.DisposeAsync(); // twice is fine

        var state = ctx.Updates()[^1];
        Assert.Null(state.Title);
        Assert.Null(state.Subtitle);
        Assert.Null(state.Icon);
        Assert.Equal((100, 19, 1900), (state.Canvas!.Width, state.Canvas.Height, state.Canvas.Pixels.Length));
        Assert.StartsWith("Snake · score", state.Tooltip);
        Assert.NotSame(ctx.Updates()[0].Canvas!.Pixels, state.Canvas.Pixels); // a fresh buffer per frame
        Assert.Null(await connector.GetFlyoutAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_click_reaches_the_scene()
    {
        var connector = new RacersConnector(new Random(1), () => Noon);
        var ctx = new Context();
        await connector.StartAsync(ctx, CancellationToken.None);

        await connector.OnActionAsync(TileActions.Click, CancellationToken.None);
        await connector.OnActionAsync(TileActions.Refresh, CancellationToken.None); // ignored

        Assert.Equal(2, ((RacersScene)connector.Scene!).RaceNumber);
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task The_farm_connector_reads_its_place_and_hours_from_settings()
    {
        var connector = new FarmConnector(new Random(1), () => Noon, region: () => (49.75, 15.5));
        var ctx = new Context { Settings = Json("""{ "latitude": 60, "workStart": "7:30", "workEnd": "sunset" }""") };

        await connector.StartAsync(ctx, CancellationToken.None);
        await connector.DisposeAsync();

        Assert.IsType<FarmScene>(connector.Scene);
        Assert.Empty(((CollectingLog)ctx.Log).Snapshot());
    }

    [Fact]
    public void Farm_options_fall_back_to_the_windows_region_and_sunrise_to_sunset()
    {
        var log = new CollectingLog();

        var options = FarmOptions.FromSettings(Json("{}"), log, () => (49.75, 15.5));
        var nowhere = FarmOptions.FromSettings(Json("{}"), log, () => null);

        Assert.Equal((49.75, 15.5), (options.Latitude, options.Longitude));
        Assert.Equal((DayTime.Sunrise, DayTime.Sunset), (options.WorkStart, options.WorkEnd));
        Assert.Equal((50.0, 15.0), (nowhere.Latitude, nowhere.Longitude));
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("""{ "latitude": 95 }""", "latitude")]
    [InlineData("""{ "longitude": "east" }""", "longitude")]
    [InlineData("""{ "workStart": "noonish" }""", "workStart")]
    [InlineData("""{ "workEnd": 18 }""", "workEnd")]
    public void Bad_farm_settings_are_logged_and_the_default_used(string settings, string key)
    {
        var log = new CollectingLog();

        var options = FarmOptions.FromSettings(Json(settings), log, () => (49.75, 15.5));

        Assert.Equal(new FarmOptions(49.75, 15.5, DayTime.Sunrise, DayTime.Sunset, TimeZoneInfo.Local), options);
        Assert.Contains(log.Snapshot(), m => m.StartsWith("WARN") && m.Contains(key));
    }

    [Theory]
    [InlineData("sunrise", DayTime.Anchor.Sunrise, 0)]
    [InlineData(" Sunset ", DayTime.Anchor.Sunset, 0)]
    [InlineData("7:30", DayTime.Anchor.Clock, 7.5)]
    [InlineData("18:06", DayTime.Anchor.Clock, 18.1)]
    [InlineData("00:00", DayTime.Anchor.Clock, 0)]
    public void Work_times_are_sunrise_sunset_or_a_clock_time(string text, DayTime.Anchor anchor, double hours)
    {
        Assert.True(DayTime.TryParse(text, out var value));
        Assert.Equal(anchor, value.From);
        Assert.Equal(hours, value.Hours, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("7.30")]
    [InlineData("dawn")]
    public void Other_work_times_are_rejected(string text) => Assert.False(DayTime.TryParse(text, out _));

    [Theory]
    // Published times for Prague (50.08 N, 14.42 E), in CET (+1) or CEST (+2).
    [InlineData(2026, 11, 2, 1, "06:52", "16:38")]
    [InlineData(2026, 6, 21, 2, "04:52", "21:15")]
    [InlineData(2026, 12, 21, 1, "07:59", "16:02")]
    public void Sunrise_and_sunset_are_within_a_few_minutes_of_the_almanac(int year, int month, int day, int offset, string rise, string set)
    {
        var sun = SunTimes.For(new DateOnly(year, month, day), 50.08, 14.42, TimeSpan.FromHours(offset));

        Assert.InRange(sun.Sunrise - TimeOnly.Parse(rise).ToTimeSpan().TotalHours, -4 / 60.0, 4 / 60.0);
        Assert.InRange(sun.Sunset - TimeOnly.Parse(set).ToTimeSpan().TotalHours, -4 / 60.0, 4 / 60.0);
    }

    [Fact]
    public void Polar_days_and_nights_have_no_sunrise_or_sunset()
    {
        var winter = SunTimes.For(new DateOnly(2026, 12, 21), 78.2, 15.6, TimeSpan.FromHours(1)); // Svalbard
        var summer = SunTimes.For(new DateOnly(2026, 6, 21), 78.2, 15.6, TimeSpan.FromHours(2));

        Assert.False(winter.Rises);
        Assert.False(summer.Sets);
        Assert.Equal(24, summer.Sunset - summer.Sunrise, 6);
    }

    [Fact]
    public void Sky_hour_puts_sunrise_at_six_and_sunset_at_eight_and_keeps_its_order()
    {
        var november = new SunTimes.Day(6.87, 16.63);

        Assert.Equal(6f, FarmScene.SkyHour(6.87f, november), 3);
        Assert.Equal(20f, FarmScene.SkyHour(16.63f, november), 3);
        Assert.Equal(5f, FarmScene.SkyHour(5.87f, november), 3); // an hour of twilight before sunrise
        var previous = FarmScene.SkyHour(5.87f, november);
        for (var h = 5.9f; h < 5.87f + 24; h += 0.1f) // one full day from dawn: the sky hour only moves forward
        {
            var sky = FarmScene.SkyHour(h % 24, november);
            Assert.True((sky - previous + 24) % 24 < 1, $"{h}: {previous} -> {sky}");
            previous = sky;
        }
    }

    [Fact]
    public void The_farmer_keeps_the_working_hours_from_settings()
    {
        var rng = new Random(4);
        var farm = new FarmScene(rng, Prague with { WorkStart = DayTime.At(9), WorkEnd = DayTime.At(15) });

        farm.Step(Noon.Date.AddHours(8), rng); // after sunrise, but before work
        Assert.True(farm.FarmerInside);

        farm.Step(Noon.Date.AddHours(9.5), rng);
        Assert.False(farm.FarmerInside);

        for (var i = 0; i < 2000 && !farm.FarmerInside; i++) farm.Step(Noon.Date.AddHours(16), rng); // walks home
        Assert.True(farm.FarmerInside);
    }

    [Fact]
    public void In_november_the_farm_is_dark_by_six_in_the_evening()
    {
        var day = new DateTime(2026, 11, 2);
        var evening = Frame(new FarmScene(new Random(4), Prague), day.AddHours(18));
        var afternoon = Frame(new FarmScene(new Random(4), Prague), day.AddHours(13));

        Assert.True(Brightness(afternoon, row: 0) > Brightness(evening, row: 0) * 2);

        static int[] Frame(FarmScene scene, DateTime at)
        {
            var rng = new Random(4);
            for (int i = 0; i < 5; i++) scene.Step(at, rng);
            var pixels = PixelCanvas.NewFrame();
            scene.Render(pixels);
            return pixels;
        }
    }
}
