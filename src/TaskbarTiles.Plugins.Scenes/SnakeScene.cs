namespace TaskbarTiles.Plugins.Scenes;

/// <summary>
/// A snake that plays itself on a grid of 2 × 2 pixel cells (50 × 9 on the tile). It heads for the apple along the
/// shortest path when it can still reach its tail afterwards; otherwise it stalls by chasing its tail, and it only
/// dies when no move is safe. After a short flash it starts over. Score and best live in memory.
/// </summary>
public sealed class SnakeScene : IScene
{
    const int Cell = 2, StartLength = 3, DeathFrames = 16;
    static readonly int[] Dx = [1, 0, -1, 0], Dy = [0, 1, 0, -1];

    static readonly int BoardA = PixelCanvas.Rgb(0x25472F), BoardB = PixelCanvas.Rgb(0x2C5237), Border = PixelCanvas.Rgb(0x1A3322);
    static readonly int Head = PixelCanvas.Rgb(0xC4F56A), BodyNear = PixelCanvas.Rgb(0x86DB57), BodyFar = PixelCanvas.Rgb(0x2E8A4C);
    static readonly int Eye = PixelCanvas.Rgb(0x10240F), Tongue = PixelCanvas.Rgb(0xFF5A6E);
    static readonly int Apple = PixelCanvas.Rgb(0xEC4A3F), AppleDark = PixelCanvas.Rgb(0xB82B2E), Leaf = PixelCanvas.Rgb(0x7AD443);
    static readonly int Flash = PixelCanvas.Rgb(0xFFFFFF), Hurt = PixelCanvas.Rgb(0xFF4D5E);

    readonly int _w, _h, _framesPerMove;
    readonly int[] _body;   // ring buffer of cell indices; _head is the newest
    readonly byte[] _occ;   // 1 where the snake is
    readonly int[] _queue, _prev, _seen;
    int _seenGen;
    int _head, _length, _dir, _food = -1, _frame, _dying, _games, _score, _best, _hungry;
    bool _won;
    string _status = "";

    /// <summary>A 50 × 9 board that moves every second frame. Tests use small boards and one move per step.</summary>
    public SnakeScene(Random rng, int width = PixelCanvas.Width / Cell, int height = PixelCanvas.Height / Cell, int framesPerMove = 2)
    {
        if (width < 4 || height < 2) throw new ArgumentOutOfRangeException(nameof(width), "The board is too small.");
        _w = width;
        _h = height;
        _framesPerMove = Math.Max(1, framesPerMove);
        int n = width * height;
        _body = new int[n];
        _occ = new byte[n];
        _queue = new int[n];
        _prev = new int[n];
        _seen = new int[n];
        Restart(rng);
    }

    public string Name => "Snake";
    public string Status => _status;

    public int GridWidth => _w;
    public int GridHeight => _h;
    public bool Alive => _dying == 0;
    public int Score => _score;
    public int Best => _best;
    /// <summary>Games started, counting the first one.</summary>
    public int Games => _games;
    public int Length => _length;
    public int HeadCell => _body[_head];
    public int FoodCell => _food;

    /// <summary>Whether the cell is the snake's, for tests.</summary>
    public bool IsBody(int cell) => _occ[cell] != 0;

    /// <summary>The cell the tail is on; it moves away on the next step unless the snake eats.</summary>
    public int TailCell => _body[Tail];

    int Tail => (_head - _length + 1 + _body.Length) % _body.Length;

    public void Step(DateTime now, Random rng)
    {
        _frame++;
        if (_dying > 0)
        {
            if (--_dying == 0) Restart(rng);
            return;
        }
        if (_frame % _framesPerMove != 0) return;

        int dir = ChooseDirection();
        if (dir < 0) { Die(); return; }
        Move(dir, rng);
    }

    public void OnClick(DateTime now, Random rng) => Restart(rng);

    void Restart(Random rng)
    {
        Array.Clear(_occ);
        _length = 0;
        _head = -1;
        int y = _h / 2;
        for (int i = 0; i < StartLength; i++)
        {
            _head++;
            _body[_head] = y * _w + 1 + i;
            _occ[_body[_head]] = 1;
            _length++;
        }
        _dir = 0;
        _dying = 0;
        _won = false;
        _hungry = 0;
        _games++;
        _score = 0;
        PlaceFood(rng);
        UpdateStatus();
    }

    void Die()
    {
        _dying = DeathFrames;
        UpdateStatus();
    }

    void Move(int dir, Random rng)
    {
        int head = _body[_head];
        int next = (head / _w + Dy[dir]) * _w + head % _w + Dx[dir];
        bool eats = next == _food;
        if (!eats)
        {
            _occ[_body[Tail]] = 0; // the tail moves first, so following it closely is fine
            _length--;
        }
        _head = (_head + 1) % _body.Length;
        _body[_head] = next;
        _occ[next] = 1;
        _length++;
        _dir = dir;
        _hungry++;
        if (!eats) return;

        _hungry = 0;
        _score++;
        if (_score > _best) _best = _score;
        PlaceFood(rng);
        if (_food < 0)
        {
            // The board is full: a perfect game. Celebrate with the same flash, then start over.
            _won = true;
            _dying = DeathFrames;
        }
        UpdateStatus();
    }

    void PlaceFood(Random rng)
    {
        int free = _occ.Length - _length;
        if (free <= 0) { _food = -1; return; }
        int pick = rng.Next(free);
        for (int c = 0; c < _occ.Length; c++)
        {
            if (_occ[c] != 0) continue;
            if (pick-- == 0) { _food = c; return; }
        }
        _food = -1;
    }

    void UpdateStatus() => _status = _dying > 0
        ? (_won ? $"full board! score {_score} · best {_best}" : $"oops! score {_score} · best {_best}")
        : $"score {_score} · best {_best}";

    int Neighbor(int cell, int dir)
    {
        int x = cell % _w + Dx[dir], y = cell / _w + Dy[dir];
        return (uint)x < (uint)_w && (uint)y < (uint)_h ? y * _w + x : -1;
    }

    /// <summary>A move that does not hit a wall or the body. The tail's cell is fine unless the snake is about to eat.</summary>
    public bool IsSafe(int dir)
    {
        int n = Neighbor(_body[_head], dir);
        if (n < 0) return false;
        return _occ[n] == 0 || (n == _body[Tail] && n != _food && _length > 1);
    }

    /// <summary>
    /// The next direction, or -1 when every move is deadly. Prefers the first step of the shortest path to the apple if
    /// the tail is still reachable after taking it (or the snake has gone hungry too long); else the safe move from which
    /// the tail is reachable and farthest (stalling while the body moves out of the way); else the safe move with the
    /// most room.
    /// </summary>
    public int ChooseDirection()
    {
        int head = _body[_head];
        if (_food >= 0 && Bfs(head, _food, ignoreTail: true))
        {
            int step = _food;
            while (_prev[step] != head) step = _prev[step];
            int dir = DirectionTo(head, step);
            // Tail chasing can loop forever when the apple is never safely reachable; after a long time without food
            // the snake gets greedy and goes for it anyway, which ends the game sooner or later.
            if (TailReachableAfter(dir) >= 0 || _hungry > _occ.Length * 2) return dir;
        }

        int best = -1, bestScore = int.MinValue;
        for (int dir = 0; dir < 4; dir++)
        {
            if (!IsSafe(dir)) continue;
            int tailDistance = TailReachableAfter(dir);
            int score = tailDistance >= 0 ? 100_000 + tailDistance : Room(dir);
            if (score > bestScore) { bestScore = score; best = dir; }
        }
        return best;
    }

    int DirectionTo(int from, int to)
    {
        for (int dir = 0; dir < 4; dir++) if (Neighbor(from, dir) == to) return dir;
        return -1;
    }

    /// <summary>Plays the move on the grid, measures the path from the new head to the new tail (-1: cut off), undoes it.</summary>
    int TailReachableAfter(int dir)
    {
        if (!IsSafe(dir)) return -1;
        int n = Neighbor(_body[_head], dir);
        bool eats = n == _food;
        int oldTail = _body[Tail];
        int newTail = eats ? oldTail : _body[(Tail + 1) % _body.Length];
        if (!eats) _occ[oldTail] = 0;
        byte wasN = _occ[n];
        _occ[n] = 1;
        int distance = -1;
        if (n == newTail) distance = 0;
        else if (Bfs(n, newTail, ignoreTail: false))
        {
            distance = 0;
            for (int c = newTail; c != n; c = _prev[c]) distance++;
        }
        _occ[n] = wasN;
        if (!eats) _occ[oldTail] = 1;
        return distance;
    }

    /// <summary>How many cells the head could reach after the move, for when the snake is already cut off from its tail.</summary>
    int Room(int dir)
    {
        int n = Neighbor(_body[_head], dir);
        int oldTail = _body[Tail];
        bool eats = n == _food;
        if (!eats) _occ[oldTail] = 0;
        _occ[n] = 1;
        Bfs(n, -1, ignoreTail: false);
        int count = 0;
        for (int c = 0; c < _seen.Length; c++) if (_seen[c] == _seenGen) count++;
        _occ[n] = 0;
        if (!eats) _occ[oldTail] = 1;
        return count;
    }

    /// <summary>Breadth-first search over free cells; the goal itself may be occupied (it is the tail). Fills _prev.</summary>
    bool Bfs(int start, int goal, bool ignoreTail)
    {
        _seenGen++;
        int tail = ignoreTail ? _body[Tail] : -1;
        int read = 0, write = 0;
        _queue[write++] = start;
        _seen[start] = _seenGen;
        while (read < write)
        {
            int c = _queue[read++];
            if (c == goal) return true;
            for (int dir = 0; dir < 4; dir++)
            {
                int n = Neighbor(c, dir);
                if (n < 0 || _seen[n] == _seenGen) continue;
                if (_occ[n] != 0 && n != goal && n != tail) continue;
                _seen[n] = _seenGen;
                _prev[n] = c;
                _queue[write++] = n;
            }
        }
        return false;
    }

    public void Render(int[] pixels)
    {
        var canvas = new PixelCanvas(pixels);
        canvas.Clear(Border);
        int ox = (PixelCanvas.Width - _w * Cell) / 2, oy = 0;
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
                canvas.Fill(ox + x * Cell, oy + y * Cell, Cell, Cell, ((x + y) & 1) == 0 ? BoardA : BoardB);

        if (_food >= 0)
        {
            int fx = ox + _food % _w * Cell, fy = oy + _food / _w * Cell;
            bool glint = _frame / 4 % 6 == 0;
            canvas.Set(fx, fy, glint ? PixelCanvas.Rgb(0xFF9A8A) : Apple);
            canvas.Set(fx + 1, fy, Leaf);
            canvas.Set(fx, fy + 1, AppleDark);
            canvas.Set(fx + 1, fy + 1, Apple);
        }

        bool flashing = _dying > 0;
        for (int i = 0; i < _length; i++)
        {
            int cell = _body[(_head - i + _body.Length) % _body.Length];
            int x = ox + cell % _w * Cell, y = oy + cell / _w * Cell;
            int color;
            if (flashing) color = _won ? Rainbow(i + _frame) : (_dying / 2 % 2 == 0 ? Flash : Hurt);
            else if (i == 0) color = Head;
            else
            {
                color = PixelCanvas.Lerp(BodyNear, BodyFar, i * 256 / Math.Max(1, _length));
                if (i % 2 == 1) color = PixelCanvas.Scale(color, 230); // faint stripes read as scales
            }
            canvas.Fill(x, y, Cell, Cell, color);
        }
        if (!flashing) DrawFace(canvas, ox, oy);
    }

    void DrawFace(PixelCanvas canvas, int ox, int oy)
    {
        int cell = _body[_head];
        int x = ox + cell % _w * Cell, y = oy + cell / _w * Cell;
        // Eyes on the leading edge of the head, one on each side of the direction of travel.
        switch (_dir)
        {
            case 0: canvas.Set(x + 1, y, Eye); canvas.Set(x + 1, y + 1, Eye); break;
            case 2: canvas.Set(x, y, Eye); canvas.Set(x, y + 1, Eye); break;
            case 1: canvas.Set(x, y + 1, Eye); canvas.Set(x + 1, y + 1, Eye); break;
            default: canvas.Set(x, y, Eye); canvas.Set(x + 1, y, Eye); break;
        }
        if (_frame % 24 < 3) // a quick tongue flick now and then
        {
            int tx = x + (_dir == 0 ? 2 : _dir == 2 ? -1 : 0), ty = y + (_dir == 1 ? 2 : _dir == 3 ? -1 : 0);
            if (_dir is 0 or 2) ty += _frame % 2;
            else tx += _frame % 2;
            canvas.Set(tx, ty, Tongue);
        }
    }

    static int Rainbow(int i) => (i % 6) switch
    {
        0 => PixelCanvas.Rgb(0xFF5A5A),
        1 => PixelCanvas.Rgb(0xFFA94D),
        2 => PixelCanvas.Rgb(0xFFE45C),
        3 => PixelCanvas.Rgb(0x7EE07A),
        4 => PixelCanvas.Rgb(0x5AB8FF),
        _ => PixelCanvas.Rgb(0xC18CFF),
    };
}
