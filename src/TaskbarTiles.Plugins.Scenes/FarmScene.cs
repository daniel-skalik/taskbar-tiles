namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// A cozy farm that follows the real local time and the real sun: the sky goes from dawn at sunrise to blue day to a
/// golden sunset and a starry night, the sun and moon cross it, and everything on the ground is lit to match. A farmer
/// walks the flower field and waters the flowers during working hours (sunrise to sunset unless set otherwise), then
/// goes home (the window lights up after dark, the house sleeps from 23:00 to 5:30) and comes out again next morning.
/// Smoke curls from the chimney; birds and butterflies pass by day, fireflies glow at night. A click makes the farmer
/// wave and a bird fly by, or at night sends a shooting star.
/// </summary>
public sealed class FarmScene : IScene
{
    enum Doing { Inside, Walking, Watering, Resting, Waving, GoingHome }

    const int H = PixelCanvas.Height;
    const int HouseY = 6, PathY = 17, FieldLeft = 14, TreeX = 1, TreeY = 4;
    readonly int W, HouseX, DoorX, FieldRight; // the house stands 27 pixels from the right edge, the field ends before it
    const float SleepHour = 23f, RiseHour = 5.5f;

    // Sky colors through the day: sky hour (see SkyHour: sunrise is 6:00 and sunset 20:00), top, bottom, daylight
    // (0..256), warmth (0..256).
    static readonly float[] KeyHour = [0f, 5.0f, 6.0f, 7.0f, 9.0f, 16.5f, 18.5f, 19.5f, 20.5f, 24f];
    static readonly int[] KeyTop = [0x0A0F2C, 0x0A0F2C, 0x2B3470, 0x5A8FD8, 0x4FA3EE, 0x4FA3EE, 0x5A5CA8, 0x2E2A66, 0x111538, 0x0A0F2C];
    static readonly int[] KeyBottom = [0x1A2350, 0x1A2350, 0xE88A6A, 0xFFD3A0, 0xBDE6FF, 0xBDE6FF, 0xFFB25E, 0xE0708A, 0x2A2A5E, 0x1A2350];
    static readonly int[] KeyLight = [40, 40, 110, 205, 256, 256, 200, 120, 55, 40];
    static readonly int[] KeyWarm = [0, 0, 150, 90, 0, 0, 230, 170, 30, 0];

    // 1 roof, 2 eave, 3 wall, 4 door, 5 window, 6 chimney, 7 chimney cap, 8 window box, 9 window flowers.
    static readonly string[] House =
    [
        "..........77..",
        "......11..66..",
        ".....1111.66..",
        "....11111166..",
        "...11111111...",
        "..1111111111..",
        "22222222222222",
        ".333333333333.",
        ".334433555333.",
        ".334433555333.",
        ".334433898333.",
    ];

    // 1 dark leaves, 2 light leaves, 3 trunk, 4 apples.
    static readonly string[] TreeTop =
    [
        "...1111...",
        ".11212111.",
        "1121114211",
        "1112121111",
        "1411112121",
        ".11211111.",
    ];
    static readonly string[] TreeTrunk =
    [
        "...1331...",
        "....33....",
        "....33....",
        "....33....",
    ];

    // 1 hat, 2 hat band, 3 skin, 4 shirt, 5 overalls, 6 boots, 7 eye. Facing right.
    static readonly string[] FarmerStand = [".11.", "1111", ".37.", "4554", ".55.", ".66."];
    static readonly string[] FarmerStepA = [".11.", "1111", ".37.", "4554", ".55.", "6..6"];
    static readonly string[] FarmerStepB = [".11.", "1111", ".37.", "4554", ".55.", ".6.6"];

    static readonly string[] BirdUp = ["1.1", ".1."];
    static readonly string[] BirdDown = [".1.", "1.1"];
    static readonly string[] Sun = [".11.", "1111", "1111", ".11."];
    static readonly string[] Moon = [".111", "11..", "11..", ".111"];
    static readonly string[][] Clouds =
    [
        ["..111....", ".11111.1.", "122222221"],
        ["...11..", ".11111.", "1222221"],
        [".11.111..", "11111111.", "2222222.."],
    ];

    static readonly int[] FlowerColors = [Rgb(0xF27BA8), Rgb(0xF7D84A), Rgb(0xF8F4EC), Rgb(0xB07CE8), Rgb(0xFF6A5A), Rgb(0x7EC8FF)];
    static readonly int Grass = Rgb(0x62B04E), GrassDark = Rgb(0x4E9A3E), HillFar = Rgb(0x8CC48A), HillMid = Rgb(0x74B86A);
    static readonly int Dirt = Rgb(0xC9A26B), DirtDark = Rgb(0xA8844E), Stem = Rgb(0x3E7F32);

    struct Mote { public float X, Y, Vx, Vy; public int Life, Phase; }

    readonly int[] _housePalette = new int[9], _treePalette = new int[4], _farmerPalette = new int[7], _birdPalette = new int[1];
    readonly int[] _sunPalette = new int[1], _moonPalette = [Rgb(0xF6F2DA)], _cloudPalette = new int[2];
    readonly int[] _near, _mid, _far;
    readonly int[] _flowerX, _flowerBase, _flowerHeight, _flowerColor;
    readonly int[] _starX, _starY;
    readonly float[] _cloudX;
    readonly int[] _cloudY;
    readonly Mote[] _smoke = new Mote[14], _fireflies = new Mote[9], _butterflies = new Mote[2], _drops = new Mote[8], _zs = new Mote[3];
    Mote _bird, _shootingStar;
    Doing _doing = Doing.Resting;
    readonly FarmOptions _options;
    float _farmerX = 30, _targetX = 30, _hour, _clock;
    DateOnly _sunDay;
    SunTimes.Day _sun;
    float _workStart, _workEnd;
    int _facing = 1, _timer = 20, _frame, _doorOpen, _light, _warm, _sky0, _sky1, _waveBack;
    int _statusTime = -1, _statusDoing = -1, _watered;
    bool _started;
    string _status = "";

    public FarmScene(Random rng, FarmOptions? options = null, int width = PixelCanvas.DefaultWidth)
    {
        W = width;
        HouseX = W - 27;
        DoorX = HouseX + 3;
        FieldRight = Math.Max(FieldLeft + 8, HouseX - 13);
        _near = new int[W];
        _mid = new int[W];
        _far = new int[W];
        _options = options ?? FarmOptions.Default;
        for (int x = 0; x < W; x++)
        {
            _far[x] = 8 + (int)MathF.Round(MathF.Sin(x * 0.06f + 2) * 2 + MathF.Sin(x * 0.15f) * 0.8f);
            _mid[x] = 11 + (int)MathF.Round(MathF.Sin(x * 0.09f + 4) * 1.3f);
            _near[x] = 14 + (int)MathF.Round(MathF.Sin(x * 0.045f + 0.5f) * 0.8f + MathF.Sin(x * 0.13f) * 0.4f);
        }

        int flowers = Math.Max(8, 34 * (FieldRight - FieldLeft) / 46); // as dense as on the default tile
        _flowerX = new int[flowers];
        _flowerBase = new int[flowers];
        _flowerHeight = new int[flowers];
        _flowerColor = new int[flowers];
        for (int i = 0; i < flowers; i++)
        {
            _flowerX[i] = FieldLeft + i * (FieldRight - FieldLeft) / flowers + rng.Next(2);
            _flowerBase[i] = 15 + rng.Next(2);
            _flowerHeight[i] = 1 + rng.Next(2);
            _flowerColor[i] = FlowerColors[rng.Next(FlowerColors.Length)];
        }

        int stars = Math.Max(8, 30 * W / PixelCanvas.DefaultWidth);
        _starX = new int[stars];
        _starY = new int[stars];
        for (int i = 0; i < stars; i++)
        {
            _starX[i] = rng.Next(W);
            _starY[i] = rng.Next(9);
        }

        int clouds = Math.Max(2, 3 * W / PixelCanvas.DefaultWidth);
        _cloudX = new float[clouds];
        _cloudY = new int[clouds];
        for (int i = 0; i < clouds; i++)
        {
            _cloudX[i] = rng.Next(W);
            _cloudY[i] = (i % 3) switch { 0 => 1 + rng.Next(2), 1 => 3 + rng.Next(2), _ => rng.Next(2) };
        }
        for (int i = 0; i < _fireflies.Length; i++)
            _fireflies[i] = new Mote { X = 5 + rng.Next(Math.Max(1, W - 30)), Y = 8 + rng.Next(8), Phase = rng.Next(60) };
        for (int i = 0; i < _butterflies.Length; i++)
            _butterflies[i] = new Mote { X = FieldLeft + rng.Next(FieldRight - FieldLeft), Y = 11 + rng.Next(3), Phase = i * 3 };
    }

    public string Name => "Cozy farm";
    public int Width => W;
    public string Status => _status;

    /// <summary>Whether the farmer is in the house, for tests.</summary>
    public bool FarmerInside => _doing == Doing.Inside;
    public int FlowersWatered => _watered;

    public void Step(DateTime now, Random rng)
    {
        _frame++;
        var today = DateOnly.FromDateTime(now);
        if (today != _sunDay)
        {
            _sunDay = today;
            _sun = _options.SunOn(now);
            _workStart = (float)_options.WorkStart.On(_sun);
            _workEnd = (float)_options.WorkEnd.On(_sun);
        }
        _clock = (float)now.TimeOfDay.TotalHours;
        _hour = SkyHour(_clock, _sun);
        Light();
        if (!_started)
        {
            // Start in the right place: indoors at night, among the flowers by day.
            _started = true;
            if (!Awake()) _doing = Doing.Inside;
        }
        StepFarmer(rng);
        StepSky(rng);
        StepCritters(rng);
        UpdateStatus();
    }

    public void OnClick(DateTime now, Random rng)
    {
        if (Awake() || _light > 120)
        {
            if (_doing != Doing.Inside && _doing != Doing.GoingHome)
            {
                _waveBack = (int)_doing;
                _doing = Doing.Waving;
                _timer = 24;
            }
            SendBird(rng);
        }
        else if (_shootingStar.Life <= 0)
            _shootingStar = new Mote { X = W * 2 / 5 + rng.Next(Math.Max(1, W * 11 / 20)), Y = rng.Next(3), Vx = -0.9f, Vy = 0.35f, Life = 16 };
    }

    /// <summary>Within the farmer's working hours (which may run past midnight when the end is before the start).</summary>
    bool Awake() => _workStart <= _workEnd ? _clock >= _workStart && _clock < _workEnd : _clock >= _workStart || _clock < _workEnd;

    /// <summary>
    /// Maps the clock onto the "sky hour" the sky was drawn for, where sunrise is 6:00 and sunset 20:00: an hour of
    /// twilight either side of sunrise (5 to 7), half an hour either side of sunset (19.5 to 20.5), and the rest of the
    /// day and the night stretched or squeezed in between. Days are kept between 2 and 22 hours long, so polar nights
    /// get a short dim day and polar days a short night.
    /// </summary>
    public static float SkyHour(float clock, SunTimes.Day sun)
    {
        double length = Math.Clamp(sun.Sunset - sun.Sunrise, 2, 22), noon = sun.Noon;
        double rise = noon - length / 2, set = noon + length / 2;
        double dawn = rise - 1, morning = rise + 1, evening = set - 0.5, dusk = set + 0.5;
        double h = ((clock - dawn) % 24 + 24) % 24 + dawn; // the same time of day, in [dawn, dawn + 24)
        double sky = h < morning ? 5 + (h - dawn)
            : h < evening ? 7 + (h - morning) / (evening - morning) * 12.5
            : h < dusk ? 19.5 + (h - evening)
            : 20.5 + (h - dusk) / (dawn + 24 - dusk) * 8.5;
        return (float)(sky % 24);
    }

    void Light()
    {
        int k = 0;
        while (k < KeyHour.Length - 2 && _hour >= KeyHour[k + 1]) k++;
        int t = (int)((_hour - KeyHour[k]) / (KeyHour[k + 1] - KeyHour[k]) * 256);
        _sky0 = PixelCanvas.Lerp(Rgb(KeyTop[k]), Rgb(KeyTop[k + 1]), t);
        _sky1 = PixelCanvas.Lerp(Rgb(KeyBottom[k]), Rgb(KeyBottom[k + 1]), t);
        _light = KeyLight[k] + (KeyLight[k + 1] - KeyLight[k]) * t / 256;
        _warm = KeyWarm[k] + (KeyWarm[k + 1] - KeyWarm[k]) * t / 256;
    }

    /// <summary>A daytime color as the current light shows it: blue and dim at night, golden near sunset.</summary>
    int Lit(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int night = Rgb((r * 45 >> 8) << 16 | (g * 62 >> 8) << 8 | Math.Min(255, (b * 100 >> 8) + 20));
        int warm = Rgb(Math.Min(255, r + (_warm * 40 >> 8)) << 16 | Math.Min(255, g + (_warm * 8 >> 8)) << 8 | Math.Max(0, b - (_warm * 50 >> 8)));
        return PixelCanvas.Lerp(night, warm, _light);
    }

    void StepFarmer(Random rng)
    {
        if (_doorOpen > 0) _doorOpen--;
        bool awake = Awake();
        switch (_doing)
        {
            case Doing.Inside:
                if (awake)
                {
                    _doing = Doing.Resting;
                    _farmerX = DoorX - 1;
                    _timer = 15;
                    _doorOpen = 10;
                }
                return;
            case Doing.GoingHome:
                if (WalkTo(DoorX - 1))
                {
                    _doing = Doing.Inside;
                    _doorOpen = 10;
                }
                return;
        }
        if (!awake && _doing != Doing.Waving)
        {
            _doing = Doing.GoingHome;
            return;
        }

        switch (_doing)
        {
            case Doing.Walking:
                if (WalkTo(_targetX))
                {
                    _doing = Doing.Watering;
                    _timer = 40 + rng.Next(30);
                    _facing = _targetX < FieldRight - 4 && rng.Next(2) == 0 ? 1 : -1;
                    _watered++;
                }
                break;
            case Doing.Watering:
                if (_timer % 4 == 0)
                    Spawn(_drops, _farmerX + (_facing > 0 ? 6 : -3), 15, 0, 0.25f, 6);
                if (--_timer <= 0) { _doing = Doing.Resting; _timer = 20 + rng.Next(40); }
                break;
            case Doing.Resting:
                if (--_timer <= 0)
                {
                    _doing = Doing.Walking;
                    // Mostly off to another patch of flowers; sometimes a stroll back towards the house.
                    _targetX = rng.Next(5) == 0 ? HouseX - 9 + rng.Next(6) : FieldLeft + rng.Next(FieldRight - FieldLeft - 4);
                }
                break;
            case Doing.Waving:
                if (--_timer <= 0) { _doing = (Doing)_waveBack == Doing.Walking ? Doing.Walking : Doing.Resting; _timer = 15; }
                break;
        }
    }

    /// <summary>Takes one step towards x; true when there.</summary>
    bool WalkTo(float x)
    {
        float d = x - _farmerX;
        if (MathF.Abs(d) <= 0.2f) { _farmerX = x; return true; }
        _facing = d > 0 ? 1 : -1;
        _farmerX += _facing * 0.2f;
        return false;
    }

    void StepSky(Random rng)
    {
        for (int i = 0; i < _cloudX.Length; i++)
        {
            _cloudX[i] += 0.015f + i * 0.006f;
            if (_cloudX[i] > W + 2) { _cloudX[i] = -10; _cloudY[i] = rng.Next(5); }
        }
        if (_frame % 12 == 0) Spawn(_smoke, HouseX + 10.5f, HouseY - 1, 0.03f, -0.07f, 60);
        if (Asleep() && _frame % 30 == 0) Spawn(_zs, HouseX + 9, HouseY + 7, 0.04f, -0.06f, 50);
        if (_shootingStar.Life > 0)
        {
            _shootingStar.X += _shootingStar.Vx;
            _shootingStar.Y += _shootingStar.Vy;
            _shootingStar.Life--;
        }
        Move(_smoke, wind: true);
        Move(_zs, wind: true);
        Move(_drops, wind: false);
    }

    bool Asleep() => _doing == Doing.Inside && BedTime();

    bool BedTime() => _clock >= SleepHour || _clock < RiseHour;

    void StepCritters(Random rng)
    {
        if (_bird.Life > 0)
        {
            _bird.X += _bird.Vx;
            _bird.Y += MathF.Sin(_frame * 0.2f) * 0.08f;
            if (_bird.X < -4 || _bird.X > W + 4) _bird.Life = 0;
        }
        else if (_light > 150 && rng.Next(500) == 0) SendBird(rng);

        if (_light > 170)
            for (int i = 0; i < _butterflies.Length; i++)
            {
                ref var b = ref _butterflies[i];
                if (rng.Next(25) == 0) { b.Vx = ((float)rng.NextDouble() - 0.5f) * 0.3f; b.Vy = ((float)rng.NextDouble() - 0.5f) * 0.2f; }
                b.X = Math.Clamp(b.X + b.Vx, FieldLeft - 4, FieldRight + 4);
                b.Y = Math.Clamp(b.Y + b.Vy + MathF.Sin((_frame + b.Phase * 7) * 0.4f) * 0.1f, 9, 14);
            }

        for (int i = 0; i < _fireflies.Length; i++)
        {
            ref var f = ref _fireflies[i];
            if (rng.Next(30) == 0) { f.Vx = ((float)rng.NextDouble() - 0.5f) * 0.12f; f.Vy = ((float)rng.NextDouble() - 0.5f) * 0.08f; }
            f.X = Math.Clamp(f.X + f.Vx, 2, Math.Max(3, HouseX - 2));
            f.Y = Math.Clamp(f.Y + f.Vy, 7, 16);
        }
    }

    void SendBird(Random rng)
    {
        bool fromLeft = rng.Next(2) == 0;
        _bird = new Mote { X = fromLeft ? -3 : W + 2, Y = 2 + rng.Next(5), Vx = (fromLeft ? 1 : -1) * (0.35f + (float)rng.NextDouble() * 0.2f), Life = 1 };
    }

    static void Spawn(Mote[] pool, float x, float y, float vx, float vy, int life)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Life > 0) continue;
            pool[i] = new Mote { X = x, Y = y, Vx = vx, Vy = vy, Life = life, Phase = life };
            return;
        }
    }

    void Move(Mote[] pool, bool wind)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            ref var m = ref pool[i];
            if (m.Life <= 0) continue;
            m.Life--;
            m.X += m.Vx + (wind ? MathF.Sin((_frame + i * 9) * 0.1f) * 0.03f : 0);
            m.Y += m.Vy;
        }
    }

    void UpdateStatus()
    {
        // Dawn to dusk follow the sun; after dark it is evening until bedtime, then night.
        int time = _hour switch { < 5f => 0, < 7f => 1, < 11.5f => 2, < 14f => 3, < 17.5f => 4, < 19.5f => 5, < 20.5f => 6, _ => 7 };
        if (time is 0 or 7) time = BedTime() || _hour < 5f && _clock < 12 ? 0 : 7;
        int doing = _doing == Doing.Inside ? (Asleep() ? 10 : 11) : (int)_doing;
        if (time == _statusTime && doing == _statusDoing) return;
        _statusTime = time;
        _statusDoing = doing;
        string when = time switch
        {
            0 => "night", 1 => "dawn", 2 => "morning", 3 => "midday", 4 => "afternoon", 5 => "golden hour", 6 => "dusk", _ => "evening",
        };
        string what = doing switch
        {
            10 => "the farmer is fast asleep",
            11 => "the farmer is home, lights on",
            (int)Doing.Walking => "the farmer is out for a stroll",
            (int)Doing.Watering => "the farmer is watering the flowers",
            (int)Doing.Waving => "the farmer waves hello!",
            (int)Doing.GoingHome => "the farmer heads home",
            _ => "the farmer takes a breather",
        };
        _status = $"{when} · {what}";
    }

    public void Render(int[] pixels)
    {
        var c = new PixelCanvas(pixels);
        c.VerticalGradient(0, 13, _sky0, _sky1);
        for (int y = 14; y < H; y++) for (int x = 0; x < W; x++) c.Set(x, y, _sky1);
        DrawStars(c);
        DrawSunAndMoon(c);
        DrawClouds(c);
        DrawLand(c);
        DrawTree(c);
        DrawHouse(c);
        DrawFlowers(c);
        DrawFarmer(c);
        DrawCritters(c);
    }

    void DrawStars(PixelCanvas c)
    {
        int alpha = Math.Clamp((150 - _light) * 2, 0, 230);
        if (alpha == 0) return;
        for (int i = 0; i < _starX.Length; i++)
        {
            bool twinkle = (_frame / 2 + i * 7) % 47 < 3;
            int a = twinkle ? alpha / 3 : (i % 3 == 0 ? alpha : alpha * 2 / 3);
            c.Set(_starX[i], _starY[i], Rgba(0xFFF8E0, a));
        }
        if (_shootingStar.Life > 0)
            for (int k = 0; k < 5; k++)
                c.Set((int)(_shootingStar.X - _shootingStar.Vx * k), (int)(_shootingStar.Y - _shootingStar.Vy * k), Rgba(0xFFFFFF, 255 - k * 50));
    }

    void DrawSunAndMoon(PixelCanvas c)
    {
        // The sun crosses from sky hour 6:00 to 20:00 (sunrise to sunset) and the moon from 19:30 to 6:30. The square root makes the arc climb
        // quickly, so the sun clears the hills soon after sunrise instead of hiding behind them half the morning.
        float sunT = (_hour - 6f) / 14f;
        if (sunT is > 0 and < 1)
        {
            int x = (int)(2 + sunT * (W - 8)), y = (int)(13 - MathF.Sqrt(MathF.Sin(sunT * MathF.PI)) * 12);
            int glow = Rgba(0xFFF2B0, 70);
            for (int dy = -1; dy <= 4; dy++) for (int dx = -1; dx <= 4; dx++)
                if ((dx is -1 or 4 && dy is >= 0 and <= 3) || (dy is -1 or 4 && dx is >= 0 and <= 3)) c.Set(x + dx, y + dy, glow);
            _sunPalette[0] = PixelCanvas.Lerp(Rgb(0xFFE65C), Rgb(0xFF8A3C), _warm);
            c.Sprite(x, y, Sun, _sunPalette);
        }
        float moonHour = _hour < 12 ? _hour + 24 : _hour;
        float moonT = (moonHour - 19.5f) / 11f;
        if (moonT is > 0 and < 1)
        {
            int x = (int)(2 + moonT * (W - 8)), y = (int)(13 - MathF.Sqrt(MathF.Sin(moonT * MathF.PI)) * 11);
            c.Fill(x - 1, y - 1, 6, 6, Rgba(0xF6F2DA, 18)); // a faint halo
            c.Fill(x, y, 4, 4, Rgba(0xF6F2DA, 18));
            c.Sprite(x, y, Moon, _moonPalette);
        }
    }

    void DrawClouds(PixelCanvas c)
    {
        int body = PixelCanvas.Lerp(Rgb(0x2E345A), Rgb(0xFFFFFF), _light);
        body = PixelCanvas.Lerp(body, Rgb(0xFFB89A), _warm * 3 / 4);
        _cloudPalette[0] = PixelCanvas.Rgba(body, 230);
        _cloudPalette[1] = PixelCanvas.Rgba(PixelCanvas.Scale(body, 222), 230);
        for (int i = 0; i < _cloudX.Length; i++) c.Sprite((int)_cloudX[i], _cloudY[i], Clouds[i % Clouds.Length], _cloudPalette);
    }

    void DrawLand(PixelCanvas c)
    {
        int far = Lit(HillFar), mid = Lit(HillMid), grass = Lit(Grass), grassDark = Lit(GrassDark), dirt = Lit(Dirt), dirtDark = Lit(DirtDark);
        // Distant hills fade toward the sky color, which reads as haze.
        far = PixelCanvas.Lerp(far, _sky1, 70);
        for (int x = 0; x < W; x++)
        {
            for (int y = _far[x]; y < H; y++) c.Set(x, y, far);
            for (int y = _mid[x]; y < H; y++) c.Set(x, y, mid);
            for (int y = _near[x]; y < H; y++) c.Set(x, y, (x * 5 + y * 3) % 7 == 0 ? grassDark : grass);
            if (x >= FieldLeft - 6)
                for (int y = PathY; y < H; y++) c.Set(x, y, (x * 3 + y) % 5 == 0 ? dirtDark : dirt);
        }
    }

    void DrawTree(PixelCanvas c)
    {
        _treePalette[0] = Lit(Rgb(0x3E8A3A));
        _treePalette[1] = Lit(Rgb(0x6CC25A));
        _treePalette[2] = Lit(Rgb(0x7A5232));
        _treePalette[3] = Lit(Rgb(0xE84A4A));
        // The canopy rustles: every few seconds it shifts a pixel, as if in a breeze.
        int sway = (_frame / 25) % 8 == 0 ? 1 : 0;
        c.Sprite(TreeX, TreeY + TreeTop.Length, TreeTrunk, _treePalette);
        c.Sprite(TreeX + sway, TreeY, TreeTop, _treePalette);
    }

    void DrawHouse(PixelCanvas c)
    {
        bool lit = WindowLit();
        _housePalette[0] = Lit(Rgb(0xC2553F));
        _housePalette[1] = Lit(Rgb(0x8E3A2C));
        _housePalette[2] = Lit(Rgb(0xF2E2C0));
        _housePalette[3] = _doorOpen > 0 ? Rgb(0x2A1A10) : Lit(Rgb(0x7A4A2A));
        _housePalette[4] = lit ? Rgb(0xFFD66B) : Lit(Rgb(0x6A8FB8));
        _housePalette[5] = Lit(Rgb(0x9A5A48));
        _housePalette[6] = Lit(Rgb(0x5A3A30));
        _housePalette[7] = Lit(Rgb(0x4E9A3E));
        _housePalette[8] = Lit(Rgb(0xFF7FAF));
        c.Sprite(HouseX, HouseY, House, _housePalette);
        if (lit)
        {
            // Warm light spilling from the window onto the wall and the path.
            for (int x = HouseX + 6; x <= HouseX + 10; x++)
            {
                c.Set(x, HouseY + 7, Rgba(0xFFD66B, 60));
                c.Set(x, PathY, Rgba(0xFFD66B, 50));
            }
        }

        int smoke = Lit(Rgb(0xE8E8EC));
        foreach (var m in _smoke)
            if (m.Life > 0) c.Set((int)MathF.Round(m.X), (int)MathF.Round(m.Y), PixelCanvas.Rgba(smoke, Math.Min(210, m.Life * 4)));
        foreach (var z in _zs)
            if (z.Life > 0) c.Set((int)MathF.Round(z.X), (int)MathF.Round(z.Y), Rgba(0xE8EEFF, Math.Min(230, z.Life * 6)));
    }

    /// <summary>Lit after dark while the household is up, and in the early morning; dark once they are asleep.</summary>
    bool WindowLit() => _light < 150 && !BedTime();

    void DrawFlowers(PixelCanvas c)
    {
        int stem = Lit(Stem);
        for (int i = 0; i < _flowerX.Length; i++)
        {
            int x = _flowerX[i], b = _flowerBase[i], h = _flowerHeight[i];
            // A breeze rolls across the field from left to right, nudging the heads over a pixel.
            int sway = MathF.Sin(_frame * 0.09f - x * 0.25f) > 0.55f ? 1 : 0;
            for (int k = 0; k < h; k++) c.Set(x, b - k, stem);
            int head = Lit(_flowerColor[i]);
            int hx = x + sway, hy = b - h;
            c.Set(hx, hy, head);
            if (i % 3 == 0) { c.Set(hx - 1, hy, head); c.Set(hx + 1, hy, head); c.Set(hx, hy - 1, head); } // a few big ones
        }
        foreach (var d in _drops)
            if (d.Life > 0) c.Set((int)MathF.Round(d.X), (int)MathF.Round(d.Y), Rgba(0x7EC8FF, 220));
    }

    void DrawFarmer(PixelCanvas c)
    {
        if (_doing == Doing.Inside) return;
        // After dark the farmer carries a lantern (as the house lights its window), which lights them up warm.
        int lantern = Math.Clamp((150 - _light) * 3, 0, 256);
        _farmerPalette[0] = FarmerLit(Rgb(0xF5CF5A), lantern);
        _farmerPalette[1] = FarmerLit(Rgb(0xB0342C), lantern);
        _farmerPalette[2] = FarmerLit(Rgb(0xF2C29B), lantern);
        _farmerPalette[3] = FarmerLit(Rgb(0xE8463F), lantern);
        _farmerPalette[4] = FarmerLit(Rgb(0x2F62C8), lantern);
        _farmerPalette[5] = FarmerLit(Rgb(0x3A2418), lantern);
        _farmerPalette[6] = Rgb(0x2A1A10);
        bool walking = _doing is Doing.Walking or Doing.GoingHome;
        var art = walking ? ((_frame / 4) % 2 == 0 ? FarmerStepA : FarmerStepB) : FarmerStand;
        int x = (int)MathF.Round(_farmerX), y = PathY - 5 - (walking && (_frame / 4) % 2 == 0 ? 1 : 0) + 0;
        bool flip = _facing < 0;
        int front = flip ? x - 1 : x + 4, back = flip ? x + 4 : x - 1;
        int dir = flip ? -1 : 1;

        if (lantern > 0)
        {
            // A flickering pool of light around the lantern in the back hand, drawn under the farmer.
            int flicker = 220 + (int)((uint)(_frame * 2654435761u) >> 27); // 220..251
            for (int dy = -6; dy <= 6; dy++)
                for (int dx = -6; dx <= 6; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 <= 36) c.Set(back + dx, y + 4 + dy, Rgba(0xFFB848, (36 - d2) * 5 * lantern / 256 * flicker / 256));
                }
        }
        // A dark rim keeps the small farmer readable against the flowers, the grass and the path.
        Outline(c, x, y, art, flip, Rgba(0x1A120C, 150));
        c.Sprite(x, y, art, _farmerPalette, flip);
        if (lantern > 0)
        {
            c.Set(back, y + 3, Rgb(0x2A1A10));
            c.Set(back, y + 4, (_frame / 3) % 5 == 0 ? Rgb(0xFFE080) : Rgb(0xFFF8D0));
            c.Set(back, y + 5, Rgb(0xFFB040));
        }

        if (_doing == Doing.Watering)
        {
            int can = Lit(Rgb(0x8FA4B8));
            c.Set(front, y + 3, can);
            c.Set(front + dir, y + 3, can);
            c.Set(front + dir, y + 4, can);
            c.Set(front + 2 * dir, y + 2, can); // the spout
        }
        else if (_doing == Doing.Waving)
        {
            int hand = _farmerPalette[2];
            int up = (_timer / 3) % 2;
            c.Set(front, y + 2, _farmerPalette[3]);
            c.Set(front + dir * up, y + 1, hand);
        }
    }

    /// <summary>The farmer's color as the light shows it, warmed and brightened by their lantern (0..256).</summary>
    int FarmerLit(int color, int lantern) =>
        PixelCanvas.Lerp(Lit(color), PixelCanvas.Lerp(color, Rgb(0xFFB050), 60), lantern * 170 / 256);

    /// <summary>Colors the pixels next to <paramref name="art"/>'s shape (above, below and beside it) that it leaves empty.</summary>
    static void Outline(PixelCanvas c, int x, int y, string[] art, bool flip, int color)
    {
        int w = art[0].Length;
        bool Solid(int col, int row) => row >= 0 && row < art.Length && col >= 0 && col < w && art[row][flip ? w - 1 - col : col] != '.';
        for (int row = -1; row <= art.Length; row++)
            for (int col = -1; col <= w; col++)
                if (!Solid(col, row) && (Solid(col - 1, row) || Solid(col + 1, row) || Solid(col, row - 1) || Solid(col, row + 1)))
                    c.Set(x + col, y + row, color);
    }

    void DrawCritters(PixelCanvas c)
    {
        if (_bird.Life > 0)
        {
            _birdPalette[0] = PixelCanvas.Lerp(Rgb(0x14141E), Rgb(0x3A3A4A), _light);
            c.Sprite((int)_bird.X, (int)_bird.Y, (_frame / 3) % 2 == 0 ? BirdUp : BirdDown, _birdPalette);
        }
        if (_light > 170)
            for (int i = 0; i < _butterflies.Length; i++)
            {
                var b = _butterflies[i];
                int color = i == 0 ? Rgb(0xFFF07A) : Rgb(0xFF9AD5);
                int x = (int)MathF.Round(b.X), y = (int)MathF.Round(b.Y);
                c.Set(x, y, color);
                if ((_frame + i) % 4 < 2) c.Set(x + 1, y, color); // wings open and shut
            }
        int glow = Math.Clamp((140 - _light) * 2, 0, 255);
        if (glow > 0)
            for (int i = 0; i < _fireflies.Length; i++)
            {
                var f = _fireflies[i];
                int phase = (_frame + f.Phase) % 50;
                int a = phase < 20 ? glow * (20 - Math.Abs(10 - phase) * 2 + 0) / 20 : 0;
                c.Set((int)MathF.Round(f.X), (int)MathF.Round(f.Y), Rgba(0xE6FF7A, a));
            }
    }

    static int Rgb(int rgb) => PixelCanvas.Rgb(rgb);
    static int Rgba(int rgb, int a) => PixelCanvas.Rgba(rgb, a);
}
