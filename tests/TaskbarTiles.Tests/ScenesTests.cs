using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Scenes;

namespace TaskbarTiles.Tests;

public class ScenesTests
{
    static readonly DateTime Noon = new(2026, 6, 1, 12, 0, 0);
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static readonly string[] AllScenes = [SceneConnector.Aquarium, SceneConnector.Snake, SceneConnector.Racers, SceneConnector.CozyFarm, SceneConnector.Shuffle];
    public static TheoryData<string> SceneNames() => new(AllScenes);

    [Theory]
    [InlineData("{}", "Cozy farm", false)]
    [InlineData("""{ "scene": "Snake" }""", "Snake", false)]
    [InlineData("""{ "scene": "  racers " }""", "Racers", false)] // hand-edited config may differ in case and spacing
    [InlineData("""{ "scene": "Shuffle" }""", "Shuffle", false)]
    [InlineData("""{ "scene": "Pong" }""", "Cozy farm", true)]
    [InlineData("""{ "scene": 3 }""", "Cozy farm", true)]
    public void Scene_comes_from_settings_with_a_logged_fallback(string settings, string expected, bool warns)
    {
        var log = new CollectingLog();

        Assert.Equal(expected, SceneConnector.SceneName(Json(settings), log));
        Assert.Equal(warns, log.Snapshot().Any(m => m.StartsWith("WARN")));
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Every_scene_paints_every_pixel_of_every_frame(string name)
    {
        var rng = new Random(42);
        var scene = SceneConnector.Create(name, rng);
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
        var scene = SceneConnector.Create(name, rng);
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
        var noon = Frame(new FarmScene(new Random(4)), Noon);
        var midnight = Frame(new FarmScene(new Random(4)), Noon.Date);

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
        for (int x = 0; x < PixelCanvas.Width; x++)
        {
            int p = pixels[row * PixelCanvas.Width + x];
            sum += ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255);
        }
        return sum;
    }

    [Fact]
    public void Farmer_sleeps_at_night_and_comes_out_in_the_morning()
    {
        var rng = new Random(4);
        var farm = new FarmScene(rng);

        farm.Step(Noon.Date.AddHours(2), rng);
        Assert.True(farm.FarmerInside);
        Assert.Contains("asleep", farm.Status);

        farm.Step(Noon.Date.AddHours(8), rng);
        Assert.False(farm.FarmerInside);
        Assert.StartsWith("morning", farm.Status);
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
    public void Shuffle_switches_every_ten_minutes_and_never_repeats_a_scene_twice_in_a_row()
    {
        var rng = new Random(9);
        var shuffle = new ShuffleScene([() => new Named("a"), () => new Named("b"), () => new Named("c"), () => new Named("d")]);
        var now = Noon;

        shuffle.Step(now, rng);
        var shown = new List<string> { shuffle.Name };
        shuffle.Step(now.AddMinutes(9), rng);
        Assert.Equal(shown[0], shuffle.Name); // not before the ten minutes are up

        for (int i = 1; i <= 300; i++)
        {
            shuffle.Step(now.AddMinutes(10 * i), rng);
            shown.Add(shuffle.Name);
        }

        for (int i = 1; i < shown.Count; i++) Assert.NotEqual(shown[i - 1], shown[i]);
        Assert.Equal(4, shown.Distinct().Count());
    }

    sealed class Named(string name) : IScene
    {
        public string Name => name;
        public string Status => "fine";
        public void Step(DateTime now, Random rng) { }
        public void Render(int[] pixels) => new PixelCanvas(pixels).Clear(PixelCanvas.Rgb(0));
    }

    [Fact]
    public void Canvas_blends_by_alpha_and_clips_at_the_edges()
    {
        var pixels = PixelCanvas.NewFrame();
        var canvas = new PixelCanvas(pixels);
        canvas.Clear(PixelCanvas.Rgb(0x000000));

        canvas.Set(0, 0, PixelCanvas.Rgba(0xFFFFFF, 128));
        canvas.Set(-1, 0, PixelCanvas.Rgb(0xFF0000));
        canvas.Set(PixelCanvas.Width, PixelCanvas.Height - 1, PixelCanvas.Rgb(0xFF0000));
        canvas.Sprite(PixelCanvas.Width - 2, 1, ["123"], [PixelCanvas.Rgb(0x0000FF), PixelCanvas.Rgb(0x00FF00), PixelCanvas.Rgb(0xFF0000)], flip: true);

        Assert.Equal(PixelCanvas.Rgb(0x808080), pixels[0]);
        Assert.Equal(PixelCanvas.Rgb(0xFF0000), canvas.Get(PixelCanvas.Width - 2, 1)); // flipped: the last color comes first
        Assert.Equal(PixelCanvas.Rgb(0x00FF00), canvas.Get(PixelCanvas.Width - 1, 1));
        Assert.Throws<ArgumentException>(() => new PixelCanvas(new int[10]));
    }

    sealed class Context : ITileContext
    {
        public string TileId => "pixel-scene";
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
        var connector = new SceneConnector(new Random(1), () => Noon);
        var ctx = new Context { Settings = Json("""{ "scene": "Snake" }""") };

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
        var connector = new SceneConnector(new Random(1), () => Noon);
        var ctx = new Context { Settings = Json("""{ "scene": "Racers" }""") };
        await connector.StartAsync(ctx, CancellationToken.None);

        await connector.OnActionAsync(TileActions.Click, CancellationToken.None);
        await connector.OnActionAsync(TileActions.Refresh, CancellationToken.None); // ignored

        Assert.Equal(2, ((RacersScene)connector.Scene!).RaceNumber);
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_scene_falls_back_to_the_farm()
    {
        var connector = new SceneConnector(new Random(1), () => Noon);
        var ctx = new Context { Settings = Json("""{ "scene": "Moon base" }""") };

        await connector.StartAsync(ctx, CancellationToken.None);
        await connector.DisposeAsync();

        Assert.IsType<FarmScene>(connector.Scene);
        Assert.Contains(((CollectingLog)ctx.Log).Snapshot(), m => m.StartsWith("WARN") && m.Contains("scene"));
    }
}
