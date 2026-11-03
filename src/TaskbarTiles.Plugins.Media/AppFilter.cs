namespace TaskbarTiles.Plugins.Media;

/// <summary>
/// The "app" setting: one app, several separated by commas ("Spotify, Firefox"), or "Any" (also "*" or nothing) for
/// whatever app plays. Each name matches a session's id or Start menu name, ignoring case (<see cref="AppNames.Matches"/>).
/// </summary>
public sealed record AppFilter(bool Any, IReadOnlyList<string> Apps)
{
    public static AppFilter Parse(string? setting)
    {
        var apps = (setting ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var any = apps.Count == 0 || apps.Any(a => a.Equals("Any", StringComparison.OrdinalIgnoreCase) || a == "*");
        return new AppFilter(any, any ? [] : apps);
    }

    /// <summary>More than one app can match, so the tile looks again when another one starts playing.</summary>
    public bool Several => Any || Apps.Count > 1;

    /// <summary>What the tile shows while nothing matches: the app, the apps, or "Media".</summary>
    public string Label => Any ? "Media" : string.Join(", ", Apps);

    /// <summary>Where the app named first is listed, or -1 when the session matches none.</summary>
    public int Rank(string? aumid, string? displayName)
    {
        if (Any) return 0;
        for (int i = 0; i < Apps.Count; i++)
            if (AppNames.Matches(aumid, displayName, Apps[i])) return i;
        return -1;
    }

    /// <summary>
    /// The session to follow, as an index into <paramref name="sessions"/>, or -1 for none: of those that match, one that
    /// is playing beats one that is not; then the app listed first; with "Any", the session Windows calls current.
    /// </summary>
    public int Choose(IReadOnlyList<SessionInfo> sessions)
    {
        int best = -1;
        (int Playing, int Rank, int Current) bestKey = default;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var rank = Rank(s.Aumid, s.DisplayName);
            if (rank < 0) continue;
            var key = (s.Playing ? 0 : 1, rank, s.Current ? 0 : 1);
            if (best < 0 || key.CompareTo(bestKey) < 0) (best, bestKey) = (i, key);
        }
        return best;
    }
}

/// <summary>What <see cref="AppFilter.Choose"/> needs to know about a media session.</summary>
public readonly record struct SessionInfo(string Aumid, string? DisplayName, bool Playing, bool Current);
