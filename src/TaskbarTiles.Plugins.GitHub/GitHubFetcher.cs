using System.Text.Json;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>A fetch's outcome: data, or why there is none (gh missing, not signed in, or an error message).</summary>
public sealed record FetchResult(GitHubData? Data, SetupNeed Setup = SetupNeed.None, string? Error = null);

public enum SetupNeed { None, InstallGh, SignIn }

/// <summary>
/// Asks GitHub, through gh, for everything the tile shows: one GraphQL search for pull requests and issues, then, in
/// parallel, the unread notifications and the recent workflow runs of each repository it follows.
/// </summary>
public sealed class GitHubFetcher(IGh gh, Func<DateTimeOffset> now)
{
    /// <summary>Runs started before this long ago are not fetched.</summary>
    public static readonly TimeSpan RunWindow = TimeSpan.FromHours(24);
    const int MaxRunRepos = 10;

    public async Task<FetchResult> FetchAsync(GitHubSettings settings, CancellationToken ct)
    {
        if (!gh.Installed) return new FetchResult(null, SetupNeed.InstallGh, "Needs the GitHub CLI");

        var pr = $"is:pr is:open archived:false {settings.Filter}".Trim();
        var search = await gh.RunAsync(["api", "graphql",
            "-f", "query=" + GitHubParse.Query,
            "-f", $"review={pr} review-requested:@me",
            "-f", $"mine={pr} author:@me",
            "-f", "issues=" + $"is:issue is:open archived:false assignee:@me {settings.Filter}".Trim()], ct);
        if (search.NotSignedIn) return new FetchResult(null, SetupNeed.SignIn, "Not signed in to GitHub");
        // gh exits non-zero on GraphQL errors too, with the answer still on stdout.
        if (search.ExitCode != 0 && !search.Output.TrimStart().StartsWith('{')) return new FetchResult(null, Error: "GitHub: " + search.FirstLine);

        string? error;
        (string Login, IReadOnlyList<PullRequest> Reviews, IReadOnlyList<PullRequest> Mine, IReadOnlyList<Issue> Issues)? found;
        try { found = GitHubParse.Search(search.Output, out error); }
        catch (JsonException) { return new FetchResult(null, Error: "GitHub sent an answer that is not JSON"); }
        if (found is not { } f) return new FetchResult(null, Error: "GitHub: " + error);

        var problems = new List<string>();
        var notes = settings.Shows(Sections.Notifications) ? NotificationsAsync(problems, ct) : Task.FromResult<IReadOnlyList<Notification>>([]);
        var runs = settings.Shows(Sections.Actions) ? RunsAsync(settings, f.Login, f.Mine, problems, ct) : Task.FromResult<IReadOnlyList<WorkflowRun>>([]);
        await Task.WhenAll(notes, runs);

        return new FetchResult(new GitHubData(f.Login, f.Reviews, f.Mine, f.Issues, await runs, await notes, problems));
    }

    async Task<IReadOnlyList<Notification>> NotificationsAsync(List<string> problems, CancellationToken ct)
    {
        var result = await gh.RunAsync(["api", "notifications?per_page=30"], ct); // unread only, the API's default
        if (result.ExitCode != 0) { lock (problems) problems.Add("notifications: " + result.FirstLine); return []; }
        try { return GitHubParse.Notifications(result.Output); }
        catch (JsonException) { lock (problems) problems.Add("notifications: not JSON"); return []; }
    }

    /// <summary>Anyone's runs in the watched repositories; the viewer's own in the repositories of their open pull requests.</summary>
    async Task<IReadOnlyList<WorkflowRun>> RunsAsync(GitHubSettings settings, string login, IReadOnlyList<PullRequest> mine,
        List<string> problems, CancellationToken ct)
    {
        var since = now().UtcDateTime.Subtract(RunWindow).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var asks = settings.Watched.Select(r => (Repo: r, Url: $"repos/{r}/actions/runs?per_page=30&created=%3E%3D{since}")).ToList();
        if (login.Length > 0)
            asks.AddRange(mine.Select(p => p.Repo).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => GitHubSettings.RepoName().IsMatch(r) && !settings.Watched.Contains(r, StringComparer.OrdinalIgnoreCase))
                .Select(r => (Repo: r, Url: $"repos/{r}/actions/runs?per_page=30&created=%3E%3D{since}&actor={Uri.EscapeDataString(login)}")));

        var lists = await Task.WhenAll(asks.Take(MaxRunRepos * 2).Select(async ask =>
        {
            var result = await gh.RunAsync(["api", ask.Url], ct);
            if (result.ExitCode != 0) { lock (problems) problems.Add($"{ask.Repo}: {result.FirstLine}"); return (IReadOnlyList<WorkflowRun>)[]; }
            try { return GitHubParse.Runs(result.Output); }
            catch (JsonException) { lock (problems) problems.Add($"{ask.Repo}: not JSON"); return []; }
        }));
        return lists.SelectMany(l => l).ToList();
    }
}
