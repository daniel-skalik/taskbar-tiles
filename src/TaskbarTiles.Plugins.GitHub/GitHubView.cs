using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>What one of the viewer's pull requests or a workflow run waits on, and how it is drawn.</summary>
public sealed record Status(string Group, string Label, TileColor Color, string Glyph = "ring", int Order = 0)
{
    public const string Blocked = "blocked", Action = "action", Waiting = "waiting", Draft = "draft",
        Failed = "failed", Running = "running", Passed = "passed", Cancelled = "cancelled";
}

/// <summary>
/// Everything the tile and popup show, worked out once from a fetch: the sections that are switched off are empty.
/// "Need you" counts the viewer's blocked pull requests and those waiting on their action, plus failed runs and runs
/// waiting for approval.
/// </summary>
public sealed class Board
{
    public IReadOnlyList<PullRequest> Reviews { get; }
    public IReadOnlyList<(PullRequest Pr, Status Status)> Blocked { get; }
    public IReadOnlyList<(PullRequest Pr, Status Status)> Action { get; }
    public IReadOnlyList<(PullRequest Pr, Status Status)> Waiting { get; }
    public IReadOnlyList<Issue> Issues { get; }
    public IReadOnlyList<Notification> Notifications { get; }
    public IReadOnlyList<(WorkflowRun Run, Status Status)> Runs { get; }
    public IReadOnlyList<string> Problems { get; }

    public IEnumerable<(WorkflowRun Run, Status Status)> FailedRuns => Runs.Where(r => r.Status.Group == Status.Failed);
    public IEnumerable<(WorkflowRun Run, Status Status)> ApprovalRuns => Runs.Where(r => r.Status.Group == Status.Action);
    public int NeedYou => Blocked.Count + Action.Count + FailedRuns.Count() + ApprovalRuns.Count();

    public Board(GitHubData data, GitHubSettings settings, DateTimeOffset now)
    {
        Reviews = settings.Shows(Sections.Reviews) ? data.Reviews.OrderByDescending(p => p.Updated).ToList() : [];
        var mine = settings.Shows(Sections.Mine) ? data.Mine.OrderByDescending(p => p.Updated).Select(p => (Pr: p, Status: GitHubView.MyStatus(p))).ToList() : [];
        Blocked = mine.Where(m => m.Status.Group == Status.Blocked).ToList();
        Action = mine.Where(m => m.Status.Group == Status.Action).ToList();
        Waiting = mine.Where(m => m.Status.Group is Status.Waiting or Status.Draft).OrderBy(m => m.Status.Group == Status.Draft).ToList();
        Issues = settings.Shows(Sections.Issues) ? data.Issues.OrderByDescending(i => i.Updated).ToList() : [];
        Notifications = settings.Shows(Sections.Notifications) ? data.Notifications.OrderByDescending(n => n.Updated).ToList() : [];
        Runs = settings.Shows(Sections.Actions) ? GitHubView.LatestRuns(data.Runs, now) : [];
        Problems = data.Problems;
    }
}

/// <summary>The GitHub tile's look: pure functions of the fetched data, so they can be tested without gh.</summary>
public static class GitHubView
{
    public const string Title = "GitHub";
    public const string PullsPage = "https://github.com/pulls";
    static readonly string Dot = "·";

    /// <summary>The first rule that matches wins: drafts, conflicts, failing checks, changes requested, behind, ...</summary>
    public static Status MyStatus(PullRequest pr)
    {
        if (pr.IsDraft) return new(Status.Draft, "Draft", TileColor.Neutral);
        if (pr.Mergeable == "CONFLICTING") return new(Status.Blocked, "Conflicts", TileColor.Bad, "alert");
        if (pr.Checks is "FAILURE" or "ERROR") return new(Status.Blocked, "Checks failing", TileColor.Bad, "alert");
        if (pr.ReviewDecision == "CHANGES_REQUESTED") return new(Status.Action, "Changes requested", TileColor.Warn);
        if (pr.MergeState == "BEHIND") return new(Status.Action, "Behind base", TileColor.Warn);
        if (pr.Checks is "PENDING" or "EXPECTED") return new(Status.Waiting, "Checks running", TileColor.Neutral);
        if (pr.ReviewDecision == "REVIEW_REQUIRED") return new(Status.Waiting, "Awaiting review", TileColor.Neutral);
        if (pr.MergeState is "CLEAN" or "HAS_HOOKS" or "UNSTABLE") return new(Status.Action, "Ready to merge", TileColor.Ok);
        if (pr.MergeState == "BLOCKED") return new(Status.Blocked, "Blocked", TileColor.Bad, "alert");
        return new(Status.Waiting, "Open", TileColor.Neutral);
    }

    /// <summary>A run's state; null for runs left out (skipped, neutral).</summary>
    public static Status? RunStatus(WorkflowRun run)
    {
        if (run.Status != "completed")
            return run.Status is "waiting" or "action_required"
                ? new(Status.Action, "Needs approval", TileColor.Warn, "ring", 1)
                : new(Status.Running, "Running", TileColor.Accent, "half-ring", 2);
        return run.Conclusion switch
        {
            "success" => new(Status.Passed, "Passed", TileColor.Ok, "ring", 3),
            "failure" or "startup_failure" => new(Status.Failed, "Failed", TileColor.Bad, "alert", 0),
            "timed_out" => new(Status.Failed, "Timed out", TileColor.Bad, "alert", 0),
            "action_required" => new(Status.Action, "Needs approval", TileColor.Warn, "ring", 1),
            "cancelled" => new(Status.Cancelled, "Cancelled", TileColor.Neutral, "dashes", 4),
            _ => null,
        };
    }

    /// <summary>
    /// The latest run of each workflow on each branch from the last day, most pressing first: a later run that passed
    /// hides an earlier failure.
    /// </summary>
    public static IReadOnlyList<(WorkflowRun Run, Status Status)> LatestRuns(IEnumerable<WorkflowRun> runs, DateTimeOffset now)
    {
        var cutoff = now - GitHubFetcher.RunWindow;
        return runs.Where(r => r.Created >= cutoff)
            .GroupBy(r => (Repo: r.Repo.ToLowerInvariant(), r.WorkflowId, r.Branch))
            .Select(g => g.MaxBy(r => r.Created)!)
            .Select(r => (Run: r, Status: RunStatus(r)))
            .Where(r => r.Status is not null)
            .Select(r => (r.Run, Status: r.Status!))
            .OrderBy(r => r.Status.Order).ThenByDescending(r => r.Run.Created)
            .ToList();
    }

    public static string Short(string repo) => repo[(repo.LastIndexOf('/') + 1)..];
    // Popup rows sit under their repository's row, so they leave the repository out and keep the room for the title.
    public static string Name(PullRequest pr) => $"#{pr.Number} {pr.Title}";
    public static string Name(Issue i) => $"#{i.Number} {i.Title}";
    public static string RunName(WorkflowRun r) => $"{r.Workflow} {Dot} {r.Branch}";

    /// <summary>Why a notification came, in a few words.</summary>
    public static string ReasonLabel(string reason) => reason switch
    {
        "review_requested" => "Review requested", "mention" => "Mention", "team_mention" => "Team mention",
        "assign" => "Assigned", "author" => "Your thread", "comment" => "Comment", "ci_activity" => "CI",
        "state_change" => "State change", "subscribed" => "Watching", "manual" => "Subscribed",
        "security_alert" => "Security", "approval_requested" => "Approval", "invitation" => "Invitation",
        _ => reason.Replace('_', ' '),
    };

    /// <summary>What there is, most pressing first, as (count, long wording, short wording).</summary>
    static List<(int N, string Long, string Short)> Counts(Board b)
    {
        var issues = b.Issues.Count == 1 ? "issue" : "issues";
        return new List<(int N, string Long, string Short)>
        {
            (b.NeedYou, "need you", "need you"), (b.Reviews.Count, "to review", "review"), (b.Notifications.Count, "unread", "unread"),
            (b.Issues.Count, issues, issues), (b.Waiting.Count, "waiting", "waiting"),
        }.Where(c => c.N > 0).ToList();
    }

    public static string Summary(Board b)
    {
        var counts = Counts(b);
        return counts.Count > 0 ? string.Join($" {Dot} ", counts.Select(c => $"{c.N} {c.Long}")) : "All clear";
    }

    /// <summary>Red when something of the viewer's is blocked or failed, amber when it needs their action, blue for reviews or notifications.</summary>
    public static TileColor Color(Board b) =>
        b.Blocked.Count > 0 || b.FailedRuns.Any() ? TileColor.Bad
        : b.Action.Count > 0 || b.ApprovalRuns.Any() ? TileColor.Warn
        : b.Reviews.Count > 0 || b.Notifications.Count > 0 ? TileColor.Accent
        : b.Issues.Count > 0 || b.Waiting.Count > 0 ? TileColor.Neutral
        : TileColor.Ok;

    /// <summary>One line per item, most urgent first: the tile's subtitle is the first.</summary>
    static IEnumerable<string> Top(Board b)
    {
        static string Pr(PullRequest p) => $"#{p.Number} {p.Title}";
        static string Run((WorkflowRun Run, Status Status) r) => $"{r.Run.Workflow}: {r.Status.Label.ToLowerInvariant()} on {r.Run.Branch}";
        return b.Blocked.Select(m => Pr(m.Pr)).Concat(b.FailedRuns.Select(Run)).Concat(b.Action.Select(m => Pr(m.Pr)))
            .Concat(b.ApprovalRuns.Select(Run)).Concat(b.Reviews.Select(Pr)).Concat(b.Notifications.Select(n => n.Title))
            .Concat(b.Issues.Select(i => $"#{i.Number} {i.Title}")).Concat(b.Waiting.Select(m => Pr(m.Pr)));
    }

    public static TileState Tile(Board b, byte[]? logo, string? stale = null)
    {
        var counts = Counts(b);
        var title = counts.Count >= 2 ? $"{counts[0].N} {counts[0].Short} {Dot} {counts[1].N} {counts[1].Short}"
            : counts.Count == 1 ? $"{counts[0].N} {counts[0].Long}" : "All clear";
        var top = Top(b).ToList();
        var running = b.Runs.Count(r => r.Status.Group == Status.Running);
        var tip = new List<string> { Summary(b) };
        tip.AddRange(top.Take(6));
        if (running > 0) tip.Add($"{running} workflow runs in progress");
        if (b.Problems.Count > 0) tip.Add("Not read: " + string.Join("; ", b.Problems));
        if (stale is not null) tip.Add("Not updated: " + stale);
        var color = Color(b);
        return new TileState
        {
            Title = title,
            Subtitle = top.Count > 0 ? top[0] : running > 0 ? $"{running} runs in progress" : "Nothing open",
            Icon = new TileIcon("sync", color),
            Image = logo,
            ImageColor = logo is null ? null : color, // the mark in the state color
            Tooltip = string.Join("\n", tip),
        };
    }

    public static FlyoutState Flyout(Board b, byte[]? logo, string? stale = null)
    {
        var rows = new List<FlyoutRow>();
        // A heading with its count, then each repository (in the order it first comes) with its items below it.
        void Group<T>(string heading, IReadOnlyList<T> items, Func<T, string> repo, Func<T, FlyoutRow> row,
            string? headerAction = null, string? headerValue = null)
        {
            if (items.Count == 0) return;
            rows.Add(new FlyoutRow($"{heading} ({items.Count})", headerValue, Action: headerAction));
            foreach (var group in items.GroupBy(repo, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new FlyoutRow(Short(group.Key), Action: GitHubSettings.RepoName().IsMatch(group.Key) ? $"open:https://github.com/{group.Key}" : null, Indent: 1));
                rows.AddRange(group.Select(i => row(i) with { Indent = 2 }));
            }
        }
        FlyoutRow Mine((PullRequest Pr, Status Status) m) =>
            new(Name(m.Pr), m.Status.Label, new TileIcon(m.Status.Glyph, m.Status.Color), $"open:{m.Pr.Url}");

        Group("Waiting for your review", b.Reviews, p => p.Repo, p => new FlyoutRow(Name(p), "Review", new TileIcon("ring", TileColor.Accent), $"open:{p.Url}"));
        Group("Blocked", b.Blocked, m => m.Pr.Repo, Mine);
        Group("Needs your action", b.Action, m => m.Pr.Repo, Mine);
        Group("Notifications", b.Notifications, n => n.Repo, n => new FlyoutRow(n.Title, ReasonLabel(n.Reason),
            new TileIcon("ring", TileColor.Accent), $"note:{n.Id}"), "readall", "Mark all read");
        Group("Issues assigned to you", b.Issues, i => i.Repo, i => new FlyoutRow(Name(i), null, new TileIcon("ring", TileColor.Neutral), $"open:{i.Url}"));
        Group("Waiting for others", b.Waiting, m => m.Pr.Repo, Mine);
        Group("Actions", b.Runs.Take(10).ToList(), r => r.Run.Repo, r => new FlyoutRow(RunName(r.Run), r.Status.Label, new TileIcon(r.Status.Glyph, r.Status.Color),
            $"open:{r.Run.Url}"));
        if (rows.Count == 0) rows.Add(new FlyoutRow("Nothing for you right now"));

        return new FlyoutState(Title, stale is null ? Summary(b) : "Not updated: " + stale, rows,
            [new FlyoutAction("reload", "Refresh"), new FlyoutAction("open:" + PullsPage, "Open GitHub", Primary: true)],
            Image: logo, StayOpen: true); // Refresh redraws in place; a link moves focus to the browser, which closes it anyway
    }

    public static TileState SetupTile(SetupNeed need, byte[]? logo) => new()
    {
        Title = Title,
        Subtitle = need == SetupNeed.InstallGh ? "Click to install gh" : "Click to sign in",
        Icon = new TileIcon("sync", TileColor.Neutral),
        Image = logo,
        ImageColor = logo is null ? null : TileColor.Neutral,
    };

    public static FlyoutState SetupFlyout(SetupNeed need, byte[]? logo) => need == SetupNeed.InstallGh
        ? new FlyoutState(Title, "Needs the GitHub CLI (gh)",
            [new FlyoutRow("This tile reads GitHub through gh and its sign-in."), new FlyoutRow("Install opens a window that installs it with winget, then signs you in.")],
            [new FlyoutAction("open:https://cli.github.com/", "About gh"), new FlyoutAction("install", "Install gh", Primary: true)], Image: logo)
        : new FlyoutState(Title, "Not signed in to GitHub",
            [new FlyoutRow("Sign in opens a window that runs gh auth login in your browser.")],
            [new FlyoutAction("signin", "Sign in", Primary: true)], Image: logo);
}
