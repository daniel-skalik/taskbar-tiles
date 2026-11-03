namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// Four tiny cars race across a side-view track in front of a cheering grandstand. Start lights count down, the cars
/// speed up and ease off at random so the lead changes hands, kicking up dust, until one crosses the checkered line.
/// Confetti, a hop for the winner, then the next race. Wins per color are kept for the tooltip; a click starts over.
/// </summary>
public sealed class RacersScene : IScene
{
    public enum RacePhase { Countdown, Racing, Celebrating }

    const int H = PixelCanvas.Height;
    const int TrackTop = 7, LaneHeight = 3, CarWidth = 8, StartX = 2;
    readonly int W, FinishX; // the finish line is 10 pixels from the right edge
    const int CountdownFrames = 34, CelebrateFrames = 45, AfterWinFrames = 30;

    public static readonly string[] CarNames = ["red", "blue", "yellow", "green"];
    static readonly int[] CarColors = [Rgb(0xE84545), Rgb(0x3D7BE6), Rgb(0xF2C230), Rgb(0x46C46A)];

    // 1 paint, 2 tyre, 3 hub, 4 window, 5 headlight, 6 dark paint (spoiler and trim).
    static readonly string[] CarArt =
    [
        "6..144..",
        "61111115",
        ".23..23.",
    ];

    static readonly int[] ConfettiColors =
        [Rgb(0xFF5A5A), Rgb(0xFFD84A), Rgb(0x5AB8FF), Rgb(0x7EE07A), Rgb(0xFF8AE0), Rgb(0xFFFFFF)];

    static readonly int Sky = Rgb(0x8ED2FF), SkyLow = Rgb(0xD2F0FF);
    static readonly int Asphalt = Rgb(0x4A4D57), AsphaltAlt = Rgb(0x52555F), LaneMark = Rgb(0x7E828C);

    struct Car { public float X, Speed, Target; public int Retarget, Boost, Place; }
    struct Bit { public float X, Y, Vx, Vy; public int Life, Color; }

    readonly Car[] _cars = new Car[4];
    readonly int[][] _palettes = new int[4][];
    readonly int[] _wins = new int[4];
    readonly Bit[] _dust = new Bit[40];
    readonly Bit[] _confetti = new Bit[48];
    readonly int[] _crowd;
    RacePhase _phase;
    int _phaseFrame, _frame, _finished, _winner = -1, _race, _completed;
    string _status = "";

    public RacersScene(Random rng, int width = PixelCanvas.DefaultWidth)
    {
        W = width;
        FinishX = Math.Max(StartX + CarWidth + 4, W - 10);
        _crowd = new int[W * 2];
        for (int i = 0; i < 4; i++)
            _palettes[i] = [CarColors[i], Rgb(0x1A1A1E), Rgb(0x9A9AA6), Rgb(0xBFE8FF), Rgb(0xFFF2A0), PixelCanvas.Scale(CarColors[i], 150)];
        int[] shirts = [0xE84545, 0x3D7BE6, 0xF2C230, 0x46C46A, 0xFFFFFF, 0xC070E0, 0xFF8A3D];
        int[] skins = [0xF2C9A0, 0xD9A070, 0x9C6A44, 0xF7DCC0];
        for (int x = 0; x < W; x++)
        {
            _crowd[x] = Rgb(skins[rng.Next(skins.Length)]);
            _crowd[W + x] = Rgb(shirts[rng.Next(shirts.Length)]);
        }
        NewRace(rng);
    }

    public string Name => "Racers";
    public int Width => W;
    public string Status => _status;

    public RacePhase Phase => _phase;
    /// <summary>Races finished so far.</summary>
    public int Races => _completed;
    /// <summary>The number of the race being run or celebrated.</summary>
    public int RaceNumber => _race;
    public int Winner => _winner;
    public IReadOnlyList<int> Wins => _wins;
    public float CarX(int i) => _cars[i].X;

    public void OnClick(DateTime now, Random rng) => NewRace(rng);

    void NewRace(Random rng)
    {
        _race++;
        _phase = RacePhase.Countdown;
        _phaseFrame = 0;
        _finished = 0;
        _winner = -1;
        for (int i = 0; i < _cars.Length; i++)
            _cars[i] = new Car { X = StartX, Speed = 0, Target = 0, Retarget = 0, Place = 0 };
        for (int i = 0; i < _confetti.Length; i++) _confetti[i].Life = 0;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var tally = $"wins: red {_wins[0]}, blue {_wins[1]}, yellow {_wins[2]}, green {_wins[3]}";
        _status = _phase switch
        {
            RacePhase.Countdown => $"race {_race} · get ready! · {tally}",
            RacePhase.Racing when _winner < 0 => $"race {_race} · {tally}",
            _ => $"{CarNames[_winner]} wins race {_race}! · {tally}",
        };
    }

    public void Step(DateTime now, Random rng)
    {
        _frame++;
        _phaseFrame++;
        switch (_phase)
        {
            case RacePhase.Countdown:
                if (_phaseFrame >= CountdownFrames) { _phase = RacePhase.Racing; _phaseFrame = 0; UpdateStatus(); }
                break;
            case RacePhase.Racing:
                for (int i = 0; i < _cars.Length; i++) Drive(i, rng);
                // Celebrate when everyone is in, or a little after the winner if a straggler is far behind.
                if (_finished == _cars.Length || (_winner >= 0 && _phaseFrame >= AfterWinFrames))
                {
                    _phase = RacePhase.Celebrating;
                    _phaseFrame = 0;
                    _completed++;
                    LaunchConfetti(rng);
                }
                break;
            case RacePhase.Celebrating:
                if (_phaseFrame >= CelebrateFrames) { NewRace(rng); return; }
                if (_phaseFrame % 10 == 0) LaunchConfetti(rng);
                break;
        }
        StepBits(_dust, gravity: -0.004f);
        StepBits(_confetti, gravity: 0.01f);
        foreach (ref var bit in _confetti.AsSpan())
            if (bit.Life > 0) bit.Vx = MathF.Sin((_frame + bit.Color) * 0.3f) * 0.15f; // flutter
    }

    void Drive(int i, Random rng)
    {
        ref var car = ref _cars[i];
        if (car.Place > 0)
        {
            // Past the line: coast to a stop before the edge.
            car.Speed = MathF.Max(0, car.Speed - 0.02f);
            car.X = MathF.Min(car.X + car.Speed, W - CarWidth);
            return;
        }
        if (--car.Retarget <= 0)
        {
            car.Target = 0.22f + (float)rng.NextDouble() * 0.32f;
            car.Retarget = 8 + rng.Next(22);
            if (rng.Next(9) == 0) car.Boost = 10 + rng.Next(10);
        }
        float target = car.Boost > 0 ? 0.75f : car.Target;
        if (car.Boost > 0) car.Boost--;
        bool speedingUp = target > car.Speed + 0.05f;
        car.Speed += (target - car.Speed) * 0.08f;
        car.X += car.Speed;
        if (speedingUp && _frame % 3 == 0 || car.Boost > 0 && _frame % 2 == 0)
            Spawn(_dust, car.X - 1, TrackTop + i * LaneHeight + 1 + (float)rng.NextDouble(), -0.05f - (float)rng.NextDouble() * 0.1f, -0.02f, 9 + rng.Next(5), Rgb(0xCDBB98));

        if (car.X + CarWidth - 1 >= FinishX)
        {
            car.Place = ++_finished;
            if (car.Place == 1)
            {
                _winner = i;
                _wins[i]++;
                UpdateStatus();
            }
        }
    }

    void LaunchConfetti(Random rng)
    {
        for (int k = 0; k < 12; k++)
            Spawn(_confetti, rng.Next(W), -rng.Next(4), 0, 0.12f + (float)rng.NextDouble() * 0.12f, 60 + rng.Next(40), ConfettiColors[rng.Next(ConfettiColors.Length)]);
    }

    static void Spawn(Bit[] pool, float x, float y, float vx, float vy, int life, int color)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Life > 0) continue;
            pool[i] = new Bit { X = x, Y = y, Vx = vx, Vy = vy, Life = life, Color = color };
            return;
        }
    }

    static void StepBits(Bit[] pool, float gravity)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            ref var b = ref pool[i];
            if (b.Life <= 0) continue;
            b.Life--;
            b.X += b.Vx;
            b.Y += b.Vy;
            b.Vy += gravity;
            if (b.Y > H) b.Life = 0;
        }
    }

    public void Render(int[] pixels)
    {
        var c = new PixelCanvas(pixels);
        c.VerticalGradient(0, TrackTop - 1, Sky, SkyLow);
        DrawStands(c);
        DrawTrack(c);
        DrawFlag(c);
        DrawLights(c);

        foreach (var d in _dust)
            if (d.Life > 0) c.Set((int)MathF.Round(d.X), (int)MathF.Round(d.Y), PixelCanvas.Rgba(d.Color, d.Life * 20));

        for (int i = 0; i < _cars.Length; i++)
        {
            ref var car = ref _cars[i];
            int y = TrackTop + i * LaneHeight;
            bool hop = _phase == RacePhase.Celebrating && i == _winner && _phaseFrame % 8 < 3;
            // Hubs flicker with distance travelled, so the wheels seem to turn.
            _palettes[i][2] = ((int)(car.X * 2) & 1) == 0 ? Rgb(0x9A9AA6) : Rgb(0x50505A);
            c.Sprite((int)MathF.Round(car.X), y - (hop ? 1 : 0), CarArt, _palettes[i]);
        }

        foreach (var b in _confetti)
            if (b.Life > 0) c.Set((int)MathF.Round(b.X), (int)MathF.Round(b.Y), b.Color);
    }

    void DrawStands(PixelCanvas c)
    {
        bool cheering = _phase == RacePhase.Celebrating || (_phase == RacePhase.Racing && _winner >= 0);
        // A striped awning over the crowd, then heads and shirts, then the barrier wall.
        for (int x = 0; x < W; x++)
        {
            c.Set(x, 1, (x / 3 & 1) == 0 ? Rgb(0xE84545) : Rgb(0xF5F5F5));
            c.Set(x, 2, Rgb(0x5E6270));
            c.Set(x, 3, Rgb(0x6A6E7C));
            c.Set(x, 4, Rgb(0x5E6270));
            if (x % 2 == 1) continue;
            int jump = cheering && ((x / 2 + _frame / 2) % 3 == 0) ? 1 : (_frame / 6 + x) % 17 == 0 ? 1 : 0;
            c.Set(x, 3 - jump, _crowd[x]);
            c.Set(x, 4 - jump, _crowd[W + x]);
        }
        for (int x = 0; x < W; x++)
        {
            c.Set(x, 5, Rgb(0x3C404A));
            c.Set(x, 6, (x / 2 & 1) == 0 ? Rgb(0xF5F5F5) : Rgb(0xD83A3A)); // kerb
        }
    }

    void DrawTrack(PixelCanvas c)
    {
        for (int lane = 0; lane < 4; lane++)
        {
            int y0 = TrackTop + lane * LaneHeight;
            c.Fill(0, y0, W, LaneHeight, lane % 2 == 0 ? Asphalt : AsphaltAlt);
            if (lane > 0)
                for (int x = 0; x < W; x++)
                    if (x % 6 < 3) c.Set(x, y0, LaneMark);
        }
        for (int y = TrackTop; y < H; y++)
        {
            c.Set(StartX + CarWidth, y, Rgba(0xFFFFFF, 160));
            c.Set(FinishX, y, (y & 1) == 0 ? Rgb(0xF5F5F5) : Rgb(0x1A1A1A));
            c.Set(FinishX + 1, y, (y & 1) == 0 ? Rgb(0x1A1A1A) : Rgb(0xF5F5F5));
        }
    }

    void DrawFlag(PixelCanvas c)
    {
        int pole = FinishX + 3;
        for (int y = 0; y < TrackTop; y++) c.Set(pole, y, Rgb(0x2A2A30));
        int speed = _phase == RacePhase.Celebrating ? 2 : 5;
        for (int x = 0; x < 5; x++)
        {
            int wave = ((x + _frame / speed) % 4) < 2 ? 0 : 1;
            for (int y = 0; y < 3; y++)
                c.Set(pole + 1 + x, y + wave, ((x + y) & 1) == 0 ? Rgb(0xF5F5F5) : Rgb(0x1A1A1A));
        }
    }

    void DrawLights(PixelCanvas c)
    {
        // A little gantry: three reds light one by one, then all turn green for a moment as the cars go.
        bool green = _phase == RacePhase.Racing && _phaseFrame < 15;
        if (_phase != RacePhase.Countdown && !green) return;
        c.Fill(1, 0, 9, 3, Rgb(0x1E1E24));
        for (int k = 0; k < 3; k++)
        {
            bool on = green || _phaseFrame >= (k + 1) * CountdownFrames / 4;
            int color = green ? Rgb(0x5CFF7A) : on ? Rgb(0xFF4040) : Rgb(0x4A2A2A);
            c.Set(2 + k * 3, 1, color);
            c.Set(3 + k * 3, 1, color);
        }
    }

    static int Rgb(int rgb) => PixelCanvas.Rgb(rgb);
    static int Rgba(int rgb, int a) => PixelCanvas.Rgba(rgb, a);
}
