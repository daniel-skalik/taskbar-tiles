namespace TaskbarTiles.Host.UI;

/// <summary>Milestone 1 fitting: keep tiles in order until the next one would not fit.</summary>
public static class FitTiles
{
    public static int Count(IReadOnlyList<double> widths, double available, double gap)
    {
        double used = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            used += widths[i] + (i > 0 ? gap : 0);
            if (used > available) return i;
        }
        return widths.Count;
    }
}
