using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>The last commit's checks of a pull request, as GitHub sums them up; null when it has none.</summary>
public sealed record PullRequest(string Repo, int Number, string Title, string Url, DateTimeOffset Updated, bool IsDraft,
    string? ReviewDecision, string? Mergeable, string? MergeState, string? Checks);

public sealed record Issue(string Repo, int Number, string Title, string Url, DateTimeOffset Updated);

public sealed record WorkflowRun(long Id, string Repo, string Workflow, string WorkflowId, string Branch, string Status,
    string? Conclusion, string Url, DateTimeOffset Created);

/// <summary>An unread notification; <paramref name="Url"/> is the page it is about.</summary>
public sealed record Notification(string Id, string Reason, string Title, string Repo, string Url, DateTimeOffset Updated);

/// <summary>
/// One fetch: pull requests waiting for the viewer's review, the viewer's own, issues assigned to them, recent workflow
/// runs and unread notifications. <paramref name="Problems"/> lists what could not be read (a repository's runs, the
/// notifications), shown in the tooltip; the rest is still shown.
/// </summary>
public sealed record GitHubData(string Login, IReadOnlyList<PullRequest> Reviews, IReadOnlyList<PullRequest> Mine,
    IReadOnlyList<Issue> Issues, IReadOnlyList<WorkflowRun> Runs, IReadOnlyList<Notification> Notifications,
    IReadOnlyList<string> Problems)
{
    public static readonly GitHubData Empty = new("", [], [], [], [], [], []);
}

/// <summary>The sections a tile can show, as the "sections" setting names them.</summary>
public static class Sections
{
    public const string Reviews = "Reviews", Mine = "My pull requests", Actions = "Actions", Issues = "Issues", Notifications = "Notifications";
    public static readonly IReadOnlyList<string> All = [Reviews, Mine, Actions, Issues, Notifications];
}

/// <summary>
/// The tile's settings: how often to fetch ("refresh", 1 to 30 minutes, default 2), extra search qualifiers ("filter"),
/// repositories whose runs to show from anyone ("repos", owner/name, up to 10), and the sections to show ("sections";
/// missing means all).
/// </summary>
public sealed partial record GitHubSettings(TimeSpan Refresh, string Filter, IReadOnlyList<string> Watched, IReadOnlySet<string> Sections)
{
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(2), MinRefresh = TimeSpan.FromMinutes(1), MaxRefresh = TimeSpan.FromMinutes(30);
    const int MaxWatched = 10;

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    public static partial Regex RepoName();

    public bool Shows(string section) => Sections.Contains(section);

    public static GitHubSettings Read(JsonElement settings, ITileLog? log = null)
    {
        var o = settings.ValueKind == JsonValueKind.Object ? settings : default;
        string? Text(string key) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var refresh = DefaultRefresh;
        if (Text("refresh") is { Length: > 0 } r)
        {
            if (Duration.TryParse(r, out var parsed)) refresh = parsed < MinRefresh ? MinRefresh : parsed > MaxRefresh ? MaxRefresh : parsed;
            else log?.Warn($"\"refresh\" must be a duration such as \"2m\"; using {DefaultRefresh.TotalMinutes:0}m.");
        }

        // Quotes and line breaks could end the search query early; spaces are the separators GitHub expects.
        var filter = string.Join(' ', (Text("filter") ?? "").Split(['\r', '\n', '"', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

        var watched = new List<string>();
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("repos", out var repos))
        {
            IEnumerable<string> entries = repos.ValueKind switch
            {
                JsonValueKind.String => [repos.GetString()!],
                JsonValueKind.Array => repos.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
                _ => [],
            };
            foreach (var entry in entries.SelectMany(e => e.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
            {
                var name = entry.Trim().Trim('/');
                if (!RepoName().IsMatch(name)) { log?.Warn($"\"repos\": \"{name}\" is not owner/name; left out."); continue; }
                if (!watched.Contains(name, StringComparer.OrdinalIgnoreCase) && watched.Count < MaxWatched) watched.Add(name);
            }
        }

        IReadOnlySet<string> sections = new HashSet<string>(GitHub.Sections.All);
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("sections", out var s) && s.ValueKind == JsonValueKind.Array)
            sections = s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                .Where(GitHub.Sections.All.Contains).ToHashSet();

        return new GitHubSettings(refresh, filter, watched, sections);
    }
}
