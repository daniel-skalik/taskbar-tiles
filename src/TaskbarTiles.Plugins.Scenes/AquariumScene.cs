namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// A small tank: water that darkens with depth, sand with pebbles and a bubbling chest, swaying seaweed, fish that
/// wobble along and turn at the glass, rising bubbles and now and then a shaft of light. A click sprinkles food flakes;
/// the nearest fish hurry over and eat them.
/// </summary>
public sealed class AquariumScene : IScene
{
    const int W = PixelCanvas.Width, H = PixelCanvas.Height, SandTop = 16, ChestX = 80;

    // Sprites face right. 1 body, 2 fins and tail, 3 eye, 4 belly, 5 stripe.
    static readonly string[][] FishArt =
    [
        [
            "2.11.",
            "22131",
            "2.44.",
        ],
        [
            "...2...",
            "2.1111.",
            "2211131",
            "2.4444.",
        ],
        [
            "....2...",
            "2..1511.",
            "22115131",
            "2..4544.",
            ".....2..",
        ],
    ];

    static readonly int[][] Palettes =
    [
        [Rgb(0xFF8A2B), Rgb(0xE0521C), Rgb(0x1A1A2E), Rgb(0xFFD08A), Rgb(0xFFFFFF)], // orange with white bands
        [Rgb(0x3B7BE6), Rgb(0xF4D13B), Rgb(0x10183A), Rgb(0x9CC6FF), Rgb(0x1E3F99)], // blue with a yellow tail
        [Rgb(0xF6D84A), Rgb(0xE59B2A), Rgb(0x2A1E10), Rgb(0xFFF1A8), Rgb(0xE59B2A)], // sunny yellow
        [Rgb(0xE680CF), Rgb(0x9C4AC0), Rgb(0x2A1030), Rgb(0xFFC2EE), Rgb(0xFFFFFF)], // pink and purple
        [Rgb(0x6CD9A0), Rgb(0x2E9C6A), Rgb(0x0E2A1C), Rgb(0xC8F7DD), Rgb(0x2E9C6A)], // mint
        [Rgb(0xEE5050), Rgb(0xA82A3A), Rgb(0x2A0E10), Rgb(0xFFB0A0), Rgb(0xFFFFFF)], // red
    ];

    static readonly string[] ChestShut =
    [
        ".111111.",
        "12222221",
        "13333331",
        "14445444",
        "14444441",
    ];

    static readonly string[] ChestOpen =
    [
        "..1111..",
        ".122221.",
        "1.6666.1",
        "14445444",
        "14444441",
    ];

    static readonly int[] ChestPalette =
        [Rgb(0x4A2A14), Rgb(0x8A5A2E), Rgb(0xD8A93A), Rgb(0x6E4422), Rgb(0xFFE070), Rgb(0xFFD84A)];

    static readonly int WaterTop = Rgb(0x4FB8D8), WaterBottom = Rgb(0x14507E);
    static readonly int Sand = Rgb(0xE4CC8A), SandDark = Rgb(0xCCAF6C), SandLight = Rgb(0xF2E0A8);
    static readonly int WeedDark = Rgb(0x2C8A52), WeedLight = Rgb(0x49B86A);
    static readonly int BubbleColor = Rgba(0xDDF7FF, 200), FlakeColor = Rgb(0xE8A050);

    struct Fish
    {
        public float X, Y, TargetY, Speed, Phase;
        public int Dir, Kind, Palette, Turn;
    }

    struct Bubble { public float X, Y, Phase; public bool Alive; }
    struct Flake { public float X, Y; public int Life; public bool Alive; }
    struct Ray { public float X; public int Age, Life; }

    readonly Fish[] _fish;
    readonly Bubble[] _bubbles = new Bubble[28];
    readonly Flake[] _flakes = new Flake[16];
    readonly Ray[] _rays = new Ray[2];
    readonly int[] _weedX, _weedH;
    readonly float[] _weedPhase;
    readonly int[] _pebbleX, _pebbleY, _pebbleColor;
    readonly int[] _sandTop = new int[W];
    int _frame, _eaten, _chestOpen;
    string _status = "";

    public AquariumScene(Random rng)
    {
        int count = 3 + rng.Next(4);
        _fish = new Fish[count];
        for (int i = 0; i < count; i++)
        {
            int kind = i == 0 ? 2 : rng.Next(i < 3 ? 2 : 3); // one big fish, the rest mostly small and medium
            _fish[i] = new Fish
            {
                Kind = kind,
                Palette = (i + rng.Next(Palettes.Length)) % Palettes.Length,
                X = 4 + rng.Next(W - 14),
                Y = 1 + rng.Next(SandTop - 6),
                Dir = rng.Next(2) == 0 ? -1 : 1,
                Speed = 0.12f + (float)rng.NextDouble() * 0.18f + (2 - kind) * 0.04f,
                Phase = (float)rng.NextDouble() * 6.28f,
            };
            _fish[i].TargetY = _fish[i].Y;
        }

        for (int x = 0; x < W; x++)
            _sandTop[x] = SandTop + (Math.Sin(x * 0.11) + Math.Sin(x * 0.29 + 1) * 0.5 > 0.9 ? -1 : 0);

        int weeds = 5;
        _weedX = new int[weeds];
        _weedH = new int[weeds];
        _weedPhase = new float[weeds];
        int[] spots = [6, 21, 47, 63, 92];
        for (int i = 0; i < weeds; i++)
        {
            _weedX[i] = spots[i] + rng.Next(3) - 1;
            _weedH[i] = 6 + rng.Next(7);
            _weedPhase[i] = (float)rng.NextDouble() * 6.28f;
        }

        int pebbles = 10;
        _pebbleX = new int[pebbles];
        _pebbleY = new int[pebbles];
        _pebbleColor = new int[pebbles];
        int[] stones = [Rgb(0x8C8C9A), Rgb(0xA89880), Rgb(0x6E7B8B), Rgb(0xF2B8C6), Rgb(0xB8B0A0)];
        for (int i = 0; i < pebbles; i++)
        {
            _pebbleX[i] = rng.Next(W);
            _pebbleY[i] = SandTop + 1 + rng.Next(2);
            _pebbleColor[i] = stones[rng.Next(stones.Length)];
        }
        UpdateStatus();
    }

    public string Name => "Aquarium";
    public string Status => _status;

    public int FishCount => _fish.Length;
    public int Eaten => _eaten;
    public int FlakesFloating { get { int n = 0; foreach (var f in _flakes) if (f.Alive) n++; return n; } }

    void UpdateStatus() => _status = _eaten == 0
        ? $"{_fish.Length} fish · click to feed them"
        : $"{_fish.Length} fish · {_eaten} {(_eaten == 1 ? "flake" : "flakes")} eaten";

    public void OnClick(DateTime now, Random rng)
    {
        int x = 10 + rng.Next(W - 20);
        int added = 0;
        for (int i = 0; i < _flakes.Length && added < 6; i++)
        {
            if (_flakes[i].Alive) continue;
            _flakes[i] = new Flake { X = x + rng.Next(9) - 4, Y = rng.Next(2), Alive = true, Life = 400 };
            added++;
        }
    }

    public void Step(DateTime now, Random rng)
    {
        _frame++;
        StepFlakes();
        for (int i = 0; i < _fish.Length; i++) StepFish(ref _fish[i], rng);
        StepBubbles(rng);
        StepRays(rng);
    }

    void StepFlakes()
    {
        for (int i = 0; i < _flakes.Length; i++)
        {
            ref var f = ref _flakes[i];
            if (!f.Alive) continue;
            if (f.Y < SandTop - 0.5f)
            {
                f.Y += 0.06f;
                f.X += MathF.Sin((_frame + i * 13) * 0.15f) * 0.08f;
            }
            if (--f.Life <= 0) f.Alive = false; // flakes nobody ate dissolve into the sand
        }
    }

    void StepFish(ref Fish f, Random rng)
    {
        int w = FishArt[f.Kind][0].Length, h = FishArt[f.Kind].Length;
        f.Phase += 0.12f;

        // Hungry fish go for the nearest flake; a fish that is turning around finishes the turn first.
        int target = -1;
        float best = 30;
        for (int i = 0; i < _flakes.Length; i++)
        {
            if (!_flakes[i].Alive) continue;
            float d = MathF.Abs(_flakes[i].X - (f.X + w / 2f)) + MathF.Abs(_flakes[i].Y - (f.Y + h / 2f)) * 2;
            if (d < best) { best = d; target = i; }
        }

        float speed = f.Speed;
        if (target >= 0)
        {
            ref var flake = ref _flakes[target];
            float mouthX = f.Dir > 0 ? f.X + w - 1 : f.X;
            float mouthY = f.Y + h / 2;
            if (MathF.Abs(flake.X - mouthX) < 1.5f && MathF.Abs(flake.Y - mouthY) < 1.5f)
            {
                flake.Alive = false;
                _eaten++;
                UpdateStatus();
                SpawnBubble(mouthX + f.Dir, mouthY - 1);
            }
            else
            {
                // Steer by the fish's middle, not its mouth: the mouth swaps ends on a turn, which would make a
                // fish with the flake beside its body flip back and forth forever.
                float behind = (flake.X - (f.X + w / 2f)) * f.Dir;
                if (behind < -1) f.Dir = -f.Dir;
                f.TargetY = Math.Clamp(flake.Y - h / 2, 0, SandTop - h);
                speed *= 2.2f;
            }
        }
        else if (rng.Next(160) == 0) f.TargetY = rng.Next(SandTop - h);
        else if (rng.Next(500) == 0) f.Dir = -f.Dir; // changes its mind mid-tank now and then

        f.X += f.Dir * speed * (0.8f + 0.2f * MathF.Sin(f.Phase * 0.5f));
        f.Y += (f.TargetY - f.Y) * 0.04f;
        if (f.X <= 0) { f.X = 0; f.Dir = 1; }
        if (f.X >= W - w) { f.X = W - w; f.Dir = -1; }
        if (rng.Next(400) == 0) SpawnBubble(f.Dir > 0 ? f.X + w : f.X - 1, f.Y);
    }

    void StepBubbles(Random rng)
    {
        // The chest burps a stream of bubbles every few seconds, lid open while it does.
        if (_chestOpen > 0) _chestOpen--;
        if (_frame % 70 == 0 && rng.Next(3) > 0) _chestOpen = 18;
        if (_chestOpen > 4 && _chestOpen % 3 == 0) SpawnBubble(ChestX + 3 + rng.Next(2), SandTop - 3);
        if (rng.Next(45) == 0) SpawnBubble(rng.Next(W), SandTop);

        for (int i = 0; i < _bubbles.Length; i++)
        {
            ref var b = ref _bubbles[i];
            if (!b.Alive) continue;
            b.Y -= 0.22f;
            b.Phase += 0.25f;
            b.X += MathF.Sin(b.Phase) * 0.15f;
            if (b.Y < 0) b.Alive = false;
        }
    }

    void SpawnBubble(float x, float y)
    {
        for (int i = 0; i < _bubbles.Length; i++)
        {
            if (_bubbles[i].Alive) continue;
            _bubbles[i] = new Bubble { X = x, Y = y, Phase = i, Alive = true };
            return;
        }
    }

    void StepRays(Random rng)
    {
        for (int i = 0; i < _rays.Length; i++)
        {
            ref var r = ref _rays[i];
            if (r.Life > 0)
            {
                r.Age++;
                r.X += 0.03f;
                if (r.Age >= r.Life) r.Life = 0;
            }
            else if (rng.Next(250) == 0) r = new Ray { X = rng.Next(W - 10), Age = 0, Life = 80 + rng.Next(80) };
        }
    }

    public void Render(int[] pixels)
    {
        var c = new PixelCanvas(pixels);
        c.VerticalGradient(0, SandTop, WaterTop, WaterBottom);

        // Ripples at the surface drift slowly to the right.
        for (int x = 0; x < W; x++)
            if (((x + _frame / 3) % 9) < 3) c.Set(x, 0, Rgba(0xBFF0FF, 90));

        foreach (var r in _rays)
        {
            if (r.Life == 0) continue;
            int fade = Math.Min(r.Age, r.Life - r.Age);
            int alpha = Math.Min(46, fade * 2);
            for (int y = 0; y < SandTop; y++)
            {
                int a = alpha * (SandTop - y) / SandTop;
                int x0 = (int)r.X + y / 2;
                for (int dx = 0; dx < 3; dx++) c.Set(x0 + dx, y, Rgba(0xF0FFF0, dx == 1 ? a : a / 2));
            }
        }

        DrawSand(c);
        DrawWeeds(c, back: true);
        c.Sprite(ChestX, SandTop - 4, _chestOpen > 0 ? ChestOpen : ChestShut, ChestPalette);

        foreach (var f in _flakes)
            if (f.Alive) c.Set((int)MathF.Round(f.X), (int)f.Y, f.Life < 30 && f.Life % 4 < 2 ? Rgba(0xE8A050, 120) : FlakeColor);

        for (int i = 0; i < _fish.Length; i++)
        {
            ref var f = ref _fish[i];
            int wobble = (int)MathF.Round(MathF.Sin(f.Phase) * 0.6f);
            c.Sprite((int)MathF.Round(f.X), (int)MathF.Round(f.Y) + wobble, FishArt[f.Kind], Palettes[f.Palette], flip: f.Dir < 0);
        }

        DrawWeeds(c, back: false);
        foreach (var b in _bubbles)
            if (b.Alive) c.Set((int)MathF.Round(b.X), (int)b.Y, BubbleColor);
    }

    void DrawSand(PixelCanvas c)
    {
        for (int x = 0; x < W; x++)
            for (int y = _sandTop[x]; y < H; y++)
            {
                int color = y == _sandTop[x] ? SandLight : ((x * 7 + y * 3) % 5 == 0 ? SandDark : Sand);
                c.Set(x, y, color);
            }
        for (int i = 0; i < _pebbleX.Length; i++)
        {
            c.Set(_pebbleX[i], _pebbleY[i], _pebbleColor[i]);
            if (i % 3 == 0) c.Set(_pebbleX[i] + 1, _pebbleY[i], PixelCanvas.Scale(_pebbleColor[i], 200));
        }
    }

    /// <summary>Odd-numbered weeds grow in front of the fish so they swim through the plants.</summary>
    void DrawWeeds(PixelCanvas c, bool back)
    {
        for (int i = 0; i < _weedX.Length; i++)
        {
            if ((i % 2 == 1) == back) continue;
            int h = _weedH[i];
            for (int j = 0; j < h; j++)
            {
                float sway = MathF.Sin(_frame * 0.07f + _weedPhase[i] + j * 0.45f) * (j * 1.6f / h);
                int x = _weedX[i] + (int)MathF.Round(sway), y = SandTop - j;
                int color = j % 2 == 0 ? WeedDark : WeedLight;
                c.Set(x, y, color);
                if (j % 3 == 2) c.Set(x + (j % 6 == 2 ? 1 : -1), y, WeedLight); // little leaves on alternating sides
            }
        }
    }

    static int Rgb(int rgb) => PixelCanvas.Rgb(rgb);
    static int Rgba(int rgb, int a) => PixelCanvas.Rgba(rgb, a);
}
