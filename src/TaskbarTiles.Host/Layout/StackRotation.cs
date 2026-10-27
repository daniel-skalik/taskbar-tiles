namespace TaskbarTiles.Host.Layout;

/// <summary>
/// Which member of a stack is shown. The bar calls <see cref="Tick"/> every "rotate"; it advances unless the stack is
/// paused (menu), hovered or suspended (edit mode). A member that becomes urgent takes over: it is shown and ticks keep
/// returning to it until the user clicks it (<see cref="Acknowledge"/>); it takes over again only after it stopped being
/// urgent and became urgent anew. Pure state; UI thread only. Methods return true when <see cref="Index"/> changed.
/// </summary>
public sealed class StackRotation
{
    readonly bool[] _urgent;
    readonly HashSet<int> _acknowledged = [];

    public StackRotation(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _urgent = new bool[count];
    }

    public int Count => _urgent.Length;
    public int Index { get; private set; }

    /// <summary>"Pause rotation" from the tile menu. Kept for the life of the bar, not saved.</summary>
    public bool Paused { get; set; }

    /// <summary>The mouse is over the stack.</summary>
    public bool Hovered { get; set; }

    /// <summary>Edit mode: nothing moves on its own.</summary>
    public bool Suspended { get; set; }

    /// <summary>The urgent member that holds the stack, if any.</summary>
    public int? TakenOverBy { get; private set; }

    public bool Tick()
    {
        if (Suspended) return false;
        if (TakenOverBy is { } holder) return Show(holder);
        if (Paused || Hovered || Count < 2) return false;
        return Show((Index + 1) % Count);
    }

    /// <summary>Mouse wheel: +1 next, -1 previous. Works while paused or taken over (ticks then return to the urgent member).</summary>
    public bool Step(int delta) => Show(((Index + delta) % Count + Count) % Count);

    public bool SetUrgent(int member, bool urgent)
    {
        if (member < 0 || member >= Count || _urgent[member] == urgent) return false;
        _urgent[member] = urgent;
        if (urgent)
        {
            if (TakenOverBy is not null || _acknowledged.Contains(member)) return false;
            TakenOverBy = member;
            return Show(member);
        }

        _acknowledged.Remove(member);
        if (TakenOverBy != member) return false;
        TakenOverBy = NextUrgent();
        return TakenOverBy is { } next && Show(next);
    }

    /// <summary>The user clicked the stack: an urgent member it shows has been seen and lets go.</summary>
    public bool Acknowledge()
    {
        if (TakenOverBy is not { } holder || holder != Index) return false;
        _acknowledged.Add(holder);
        TakenOverBy = NextUrgent();
        return TakenOverBy is { } next && Show(next);
    }

    int? NextUrgent()
    {
        for (var i = 0; i < Count; i++)
            if (_urgent[i] && !_acknowledged.Contains(i)) return i;
        return null;
    }

    bool Show(int index)
    {
        if (index == Index) return false;
        Index = index;
        return true;
    }
}
