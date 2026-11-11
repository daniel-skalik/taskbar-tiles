using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>Turns GitHub's GraphQL and REST answers into the model. Missing or odd fields are skipped, never thrown on.</summary>
public static partial class GitHubParse
{
    /// <summary>
    /// One GraphQL query: the viewer's login, pull requests waiting for their review, their own pull requests and the
    /// issues assigned to them, each with the search qualifiers in $review, $mine and $issues.
    /// </summary>
    public const string Query =
        "query($review: String!, $mine: String!, $issues: String!) { viewer { login } " +
        "review: search(query: $review, type: ISSUE, first: 30) { nodes { ...pr } } " +
        "mine: search(query: $mine, type: ISSUE, first: 30) { nodes { ...pr } } " +
        "issues: search(query: $issues, type: ISSUE, first: 30) { nodes { ... on Issue { number title url updatedAt repository { nameWithOwner } } } } } " +
        "fragment pr on PullRequest { number title url isDraft updatedAt reviewDecision mergeable mergeStateStatus " +
        "repository { nameWithOwner } commits(last: 1) { nodes { commit { statusCheckRollup { state } } } } }";

    /// <summary>The GraphQL answer's data, or the first error message.</summary>
    public static (string Login, IReadOnlyList<PullRequest> Reviews, IReadOnlyList<PullRequest> Mine, IReadOnlyList<Issue> Issues)? Search(
        string json, out string? error)
    {
        error = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            error = Str(errors[0], "message") ?? "GitHub refused the query";
            return null;
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            error = "GitHub sent no data";
            return null;
        }
        var login = data.TryGetProperty("viewer", out var viewer) ? Str(viewer, "login") ?? "" : "";
        return (login, Nodes(data, "review").Select(Pr).OfType<PullRequest>().ToList(),
            Nodes(data, "mine").Select(Pr).OfType<PullRequest>().ToList(),
            Nodes(data, "issues").Select(IssueOf).OfType<Issue>().ToList());
    }

    /// <summary>The "workflow_runs" of an Actions runs listing.</summary>
    public static IReadOnlyList<WorkflowRun> Runs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return [];
        var list = new List<WorkflowRun>();
        foreach (var r in runs.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var id) || !id.TryGetInt64(out var runId)) continue;
            var repo = r.TryGetProperty("repository", out var repository) ? Str(repository, "full_name") : null;
            if (repo is null || Str(r, "html_url") is not { } url || Date(r, "created_at") is not { } created) continue;
            var workflowId = r.TryGetProperty("workflow_id", out var w) ? w.ToString() : "";
            list.Add(new WorkflowRun(runId, repo, Str(r, "name") ?? "", workflowId, Str(r, "head_branch") ?? "", Str(r, "status") ?? "",
                Str(r, "conclusion"), url, created));
        }
        return list;
    }

    /// <summary>The notifications listing: a JSON array of threads.</summary>
    public static IReadOnlyList<Notification> Notifications(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var list = new List<Notification>();
        foreach (var n in doc.RootElement.EnumerateArray())
        {
            if (Str(n, "id") is not { } id || !n.TryGetProperty("subject", out var subject)) continue;
            var repo = n.TryGetProperty("repository", out var repository) ? Str(repository, "full_name") ?? "" : "";
            list.Add(new Notification(id, Str(n, "reason") ?? "", Str(subject, "title") ?? "", repo,
                PageOf(Str(subject, "url"), Str(subject, "type"), repo), Date(n, "updated_at") ?? DateTimeOffset.MinValue));
        }
        return list;
    }

    [GeneratedRegex(@"^https://api\.github\.com/repos/([\w.-]+/[\w.-]+)/(pulls|issues|commits)/([0-9a-f]+)$")]
    private static partial Regex ApiUrl();

    /// <summary>The page a notification is about: its pull request, issue or commit, Actions for CI, else the repository.</summary>
    public static string PageOf(string? api, string? type, string repo)
    {
        if (api is not null && ApiUrl().Match(api) is { Success: true } m)
        {
            var kind = m.Groups[2].Value switch { "pulls" => "pull", "commits" => "commit", _ => "issues" };
            return $"https://github.com/{m.Groups[1].Value}/{kind}/{m.Groups[3].Value}";
        }
        if (!GitHubSettings.RepoName().IsMatch(repo)) return "https://github.com/notifications";
        return type == "CheckSuite" ? $"https://github.com/{repo}/actions" : $"https://github.com/{repo}";
    }

    static IEnumerable<JsonElement> Nodes(JsonElement data, string name) =>
        data.TryGetProperty(name, out var s) && s.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray() : [];

    static PullRequest? Pr(JsonElement n)
    {
        if (Str(n, "url") is not { } url || Repo(n) is not { } repo || !n.TryGetProperty("number", out var num) || !num.TryGetInt32(out var number))
            return null;
        string? checks = null;
        if (n.TryGetProperty("commits", out var commits) && commits.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            && nodes.GetArrayLength() > 0 && nodes[0].TryGetProperty("commit", out var commit)
            && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object)
            checks = Str(rollup, "state");
        return new PullRequest(repo, number, Str(n, "title") ?? "", url, Date(n, "updatedAt") ?? DateTimeOffset.MinValue,
            n.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
            Str(n, "reviewDecision"), Str(n, "mergeable"), Str(n, "mergeStateStatus"), checks);
    }

    static Issue? IssueOf(JsonElement n) =>
        Str(n, "url") is { } url && Repo(n) is { } repo && n.TryGetProperty("number", out var num) && num.TryGetInt32(out var number)
            ? new Issue(repo, number, Str(n, "title") ?? "", url, Date(n, "updatedAt") ?? DateTimeOffset.MinValue)
            : null;

    static string? Repo(JsonElement n) => n.TryGetProperty("repository", out var r) ? Str(r, "nameWithOwner") : null;

    static string? Str(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? Date(JsonElement o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
