using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.GitHub;
using Xunit;

namespace TaskbarTiles.Tests;

public class GitHubTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    static GitHubSettings All => GitHubSettings.Read(Json("{}"));
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static PullRequest Pr(int number, string title, int hour, string? decision = "REVIEW_REQUIRED", string? mergeable = "MERGEABLE",
        string? state = "BLOCKED", string? checks = "SUCCESS", bool draft = false) =>
        new("acme/api", number, title, $"https://github.com/acme/api/pull/{number}", Now.AddHours(-24 + hour), draft, decision, mergeable, state, checks);

    static WorkflowRun Run(long id, string workflow, string branch, double hoursAgo, string status = "completed", string? conclusion = "success") =>
        new(id, "acme/api", workflow, workflow, branch, status, conclusion, $"https://github.com/acme/api/actions/runs/{id}", Now.AddHours(-hoursAgo));

    static readonly GitHubData Pulls = GitHubData.Empty with
    {
        Reviews = [Pr(10, "Their change", 10)],
        Mine =
        [
            Pr(1, "Conflicting", 9, mergeable: "CONFLICTING", state: "DIRTY"),
            Pr(2, "Red checks", 8, checks: "FAILURE"),
            Pr(3, "Fix asked", 7, decision: "CHANGES_REQUESTED"),
            Pr(4, "Approved", 6, decision: "APPROVED", state: "CLEAN"),
            Pr(6, "Sketch", 11, draft: true, state: "DRAFT"),
            Pr(5, "In review", 5),
        ],
    };

    static readonly GitHubData Inbox = GitHubData.Empty with
    {
        Issues = [new Issue("acme/api", 7, "Crash on start", "https://github.com/acme/api/issues/7", Now)],
        Notifications =
        [
            new Notification("501", "mention", "Who owns this?", "acme/web", "https://github.com/acme/web/issues/3", Now.AddHours(-3)),
            new Notification("502", "review_requested", "Add retries", "acme/api", "https://github.com/acme/api/pull/9", Now.AddHours(-1)),
        ],
    };

    static readonly GitHubData Runs = GitHubData.Empty with
    {
        Runs =
        [
            Run(1, "CI", "main", 3), Run(2, "CI", "main", 1, conclusion: "failure"),
            Run(3, "CI", "feat", 2, conclusion: "failure"), Run(4, "CI", "feat", 0.5),
            Run(5, "Deploy", "main", 0.2, status: "in_progress", conclusion: null),
            Run(6, "Nightly", "main", 30, conclusion: "failure"), Run(7, "Lint", "main", 1, conclusion: "skipped"),
        ],
    };

    static Board Board(GitHubData data, GitHubSettings? settings = null) => new(data, settings ?? All, Now);

    // Settings

    [Fact]
    public void Settings_default_to_every_section_every_two_minutes()
    {
        var s = GitHubSettings.Read(Json("{}"));
        Assert.Equal(TimeSpan.FromMinutes(2), s.Refresh);
        Assert.Equal(Sections.All.Order(), s.Sections.Order());
        Assert.Empty(s.Watched);
    }

    [Fact]
    public void Settings_clamp_the_refresh_and_clean_the_filter_and_repos()
    {
        var log = new CollectingLog();
        var s = GitHubSettings.Read(Json("""
            { "refresh": "10s", "filter": "org:acme \"x\"\nlabel:bug", "repos": "acme/api, acme/web;bad repo/x/y acme/api", "sections": ["Issues", "Nope"] }
            """), log);

        Assert.Equal(TimeSpan.FromMinutes(1), s.Refresh);
        Assert.Equal("org:acme x label:bug", s.Filter);
        Assert.Equal(new[] { "acme/api", "acme/web" }, s.Watched);
        Assert.Equal(new[] { "Issues" }, s.Sections);
    }

    // Parsing

    [Fact]
    public void Notifications_parse_from_the_bare_array_gh_prints()
    {
        var notes = GitHubParse.Notifications("""
            [ { "id": "1", "reason": "mention", "updated_at": "2026-11-11T15:15:40Z", "repository": { "full_name": "acme/web" },
                "subject": { "title": "One", "type": "Issue", "url": "https://api.github.com/repos/acme/web/issues/1" } },
              { "id": "2", "reason": "ci_activity", "updated_at": "2026-11-11T15:14:55Z", "repository": { "full_name": "acme/web" },
                "subject": { "title": "Two", "type": "CheckSuite", "url": null } } ]
            """);

        Assert.Equal(2, notes.Count);
        Assert.Equal(("1", "https://github.com/acme/web/issues/1"), (notes[0].Id, notes[0].Url));
        Assert.Equal("https://github.com/acme/web/actions", notes[1].Url);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/acme/api/pulls/9", "PullRequest", "https://github.com/acme/api/pull/9")]
    [InlineData("https://api.github.com/repos/acme/api/commits/abc123", "Commit", "https://github.com/acme/api/commit/abc123")]
    [InlineData("https://api.github.com/repos/acme/api/releases/5", "Release", "https://github.com/acme/api")]
    [InlineData(null, "Discussion", "https://github.com/acme/api")]
    public void A_notification_opens_the_page_it_is_about(string? api, string type, string expected) =>
        Assert.Equal(expected, GitHubParse.PageOf(api, type, "acme/api"));

    [Fact]
    public void Search_reads_pull_requests_issues_and_the_login()
    {
        var found = GitHubParse.Search("""
            { "data": { "viewer": { "login": "me" },
              "review": { "nodes": [ { "number": 3, "title": "T", "url": "https://github.com/a/b/pull/3", "isDraft": false, "updatedAt": "2026-11-11T10:00:00Z",
                "reviewDecision": "REVIEW_REQUIRED", "mergeable": "MERGEABLE", "mergeStateStatus": "BLOCKED", "repository": { "nameWithOwner": "a/b" },
                "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "state": "FAILURE" } } } ] } } ] },
              "mine": { "nodes": [ {} ] },
              "issues": { "nodes": [ { "number": 4, "title": "I", "url": "https://github.com/a/b/issues/4", "updatedAt": "2026-11-11T10:00:00Z", "repository": { "nameWithOwner": "a/b" } } ] } } }
            """, out var error)!.Value;

        Assert.Null(error);
        Assert.Equal("me", found.Login);
        Assert.Equal("FAILURE", Assert.Single(found.Reviews).Checks);
        Assert.Empty(found.Mine); // a node without the fields is skipped
        Assert.Equal(4, Assert.Single(found.Issues).Number);
    }

    [Fact]
    public void Search_errors_come_back_as_the_message() =>
        Assert.Null(GitHubParse.Search("""{ "errors": [ { "message": "Something broke" } ] }""", out var error)) ;

    // The tile and popup

    [Fact]
    public void The_tile_counts_what_needs_you_and_shows_the_most_urgent()
    {
        var tile = GitHubView.Tile(Board(Pulls), [1]);

        Assert.Equal("4 need you · 1 review", tile.Title);
        Assert.Equal("#1 Conflicting", tile.Subtitle);
        Assert.Equal(TileColor.Bad, tile.ImageColor);
    }

    [Fact]
    public void The_popup_groups_pull_requests_by_what_they_wait_on()
    {
        var f = GitHubView.Flyout(Board(Pulls), null);

        Assert.Equal("GitHub", f.Title);
        Assert.Equal("4 need you · 1 to review · 2 waiting", f.Subtitle);
        Assert.Equal(new[]
        {
            "Waiting for your review (1)", "api", "#10 Their change",
            "Blocked (2)", "api", "#1 Conflicting", "#2 Red checks",
            "Needs your action (2)", "api", "#3 Fix asked", "#4 Approved",
            "Waiting for others (2)", "api", "#5 In review", "#6 Sketch",
        }, f.Rows.Select(r => r.Text));
        Assert.Equal(new string?[] { null, null, "Review", null, null, "Conflicts", "Checks failing", null, null, "Changes requested", "Ready to merge", null, null, "Awaiting review", "Draft" },
            f.Rows.Select(r => r.Value));
        Assert.Equal(("open:https://github.com/acme/api", 1), (f.Rows[1].Action, f.Rows[1].Indent)); // the repository, once per section
        Assert.Equal(("open:https://github.com/acme/api/pull/10", 2), (f.Rows[2].Action, f.Rows[2].Indent));
        Assert.Equal(new TileIcon("alert", TileColor.Bad), f.Rows[5].Icon);
        Assert.Equal(new[] { "reload", "open:https://github.com/pulls" }, f.Actions.Select(a => a.Id));
        Assert.True(f.StayOpen);
    }

    [Fact]
    public void A_failed_run_needs_you_and_only_the_latest_run_of_each_workflow_and_branch_shows()
    {
        var b = Board(Runs);
        Assert.Equal(("1 need you", "CI: failed on main"), (GitHubView.Tile(b, null).Title, GitHubView.Tile(b, null).Subtitle));

        var f = GitHubView.Flyout(b, null);
        // A later pass hides feat's failure; the day-old failure and the skipped run are left out.
        Assert.Equal(new[] { "Actions (3)", "api", "CI · main", "Deploy · main", "CI · feat" }, f.Rows.Select(r => r.Text));
        Assert.Equal(new string?[] { null, null, "Failed", "Running", "Passed" }, f.Rows.Select(r => r.Value));
    }

    [Fact]
    public void Notifications_and_issues_show_with_mark_all_read()
    {
        var b = Board(Inbox);
        var tile = GitHubView.Tile(b, [1]);
        Assert.Equal(("2 unread · 1 issue", "Add retries"), (tile.Title, tile.Subtitle));
        Assert.Equal(TileColor.Accent, tile.ImageColor);

        var f = GitHubView.Flyout(b, null);
        Assert.Equal(new[] { "Notifications (2)", "api", "Add retries", "web", "Who owns this?", "Issues assigned to you (1)", "api", "#7 Crash on start" },
            f.Rows.Select(r => r.Text));
        Assert.Equal(new string?[] { "Mark all read", null, "Review requested", null, "Mention", null, null, null }, f.Rows.Select(r => r.Value));
        Assert.Equal(new string?[] { "readall", "open:https://github.com/acme/api", "note:502", "open:https://github.com/acme/web", "note:501", null,
            "open:https://github.com/acme/api", "open:https://github.com/acme/api/issues/7" }, f.Rows.Select(r => r.Action));
    }

    [Fact]
    public void Only_the_ticked_sections_show_or_count()
    {
        var onlyIssues = GitHubSettings.Read(Json("""{ "sections": ["Issues"] }"""));
        var b = Board(Inbox with { Reviews = Pulls.Reviews, Mine = Pulls.Mine }, onlyIssues);

        Assert.Equal("1 issue", GitHubView.Tile(b, null).Title);
        Assert.Equal(new[] { "Issues assigned to you (1)", "api", "#7 Crash on start" }, GitHubView.Flyout(b, null).Rows.Select(r => r.Text));
    }

    [Fact]
    public void Nothing_open_is_all_clear()
    {
        var b = Board(GitHubData.Empty);
        var tile = GitHubView.Tile(b, [1]);
        Assert.Equal(("All clear", "Nothing open"), (tile.Title, tile.Subtitle));
        Assert.Equal(TileColor.Ok, tile.ImageColor);
        Assert.Equal("Nothing for you right now", Assert.Single(GitHubView.Flyout(b, null).Rows).Text);
    }

    [Fact]
    public void A_stale_fetch_says_so()
    {
        var b = Board(Pulls);
        Assert.Contains("Not updated: offline", GitHubView.Tile(b, null, "offline").Tooltip);
        Assert.Equal("Not updated: offline", GitHubView.Flyout(b, null, "offline").Subtitle);
    }

    // The connector, with gh and the shell stood in for

    sealed class FakeGh : IGh
    {
        public bool Installed { get; set; } = true;
        public string Search { get; set; } = """{ "data": { "viewer": { "login": "me" }, "review": { "nodes": [] }, "mine": { "nodes": [] }, "issues": { "nodes": [] } } }""";
        public GhResult? SearchResult { get; set; }
        public string Notes { get; set; } = "[]";
        public List<string> Calls { get; } = [];

        public Task<GhResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var line = string.Join(' ', arguments);
            lock (Calls) Calls.Add(line);
            if (arguments.Count > 1 && arguments[1] == "graphql") return Task.FromResult(SearchResult ?? new GhResult(0, Search, ""));
            if (arguments.Count > 1 && arguments[1].StartsWith("notifications?", StringComparison.Ordinal)) return Task.FromResult(new GhResult(0, Notes, ""));
            if (arguments.Count > 1 && arguments[1].StartsWith("repos/", StringComparison.Ordinal)) return Task.FromResult(new GhResult(0, """{ "workflow_runs": [] }""", ""));
            return Task.FromResult(new GhResult(0, "", ""));
        }
    }

    sealed class FakeShell : IShell
    {
        public List<string> Opened { get; } = [];
        public List<string> Consoles { get; } = [];
        public bool HasWinget { get; set; } = true;
        public void Open(string url) => Opened.Add(url);
        public void Console(string command) => Consoles.Add(command);
    }

    const string TwoNotes = """
        [ { "id": "501", "reason": "mention", "updated_at": "2026-11-11T10:00:00Z", "repository": { "full_name": "acme/web" },
            "subject": { "title": "Who owns this?", "type": "Issue", "url": "https://api.github.com/repos/acme/web/issues/3" } },
          { "id": "502", "reason": "comment", "updated_at": "2026-11-11T11:00:00Z", "repository": { "full_name": "acme/web" },
            "subject": { "title": "Later", "type": "Issue", "url": "https://api.github.com/repos/acme/web/issues/4" } } ]
        """;

    static async Task<(GitHubConnector Connector, TestTileContext Ctx)> Started(FakeGh gh, FakeShell shell, string settings = "{}")
    {
        var connector = new GitHubConnector(gh, shell, () => Now);
        var ctx = new TestTileContext("gh", settings);
        await connector.StartAsync(ctx, CancellationToken.None);
        await connector.FetchNowAsync(CancellationToken.None); // waits for the start's fetch, then fetches once more
        return (connector, ctx);
    }

    [Fact]
    public async Task Without_gh_the_tile_offers_to_install_it()
    {
        var shell = new FakeShell();
        var (connector, ctx) = await Started(new FakeGh { Installed = false }, shell);
        await using var _ = connector;

        Assert.Equal(("GitHub", "Click to install gh"), (ctx.Updates[^1].Title, ctx.Updates[^1].Subtitle));
        var popup = (await connector.GetFlyoutAsync(CancellationToken.None))!;
        Assert.Equal("Needs the GitHub CLI (gh)", popup.Subtitle);
        Assert.Equal(new[] { "open:https://cli.github.com/", "install" }, popup.Actions.Select(a => a.Id));

        await connector.OnActionAsync("install", CancellationToken.None);
        Assert.Contains("winget install --id GitHub.cli", Assert.Single(shell.Consoles));
    }

    [Fact]
    public async Task When_gh_is_not_signed_in_the_tile_offers_to_sign_in()
    {
        var gh = new FakeGh { SearchResult = new GhResult(4, "", "To get started with GitHub CLI, please run:  gh auth login") };
        var (connector, ctx) = await Started(gh, new FakeShell());
        await using var _ = connector;

        Assert.Equal("Click to sign in", ctx.Updates[^1].Subtitle);
        Assert.Equal("signin", Assert.Single((await connector.GetFlyoutAsync(CancellationToken.None))!.Actions).Id);
    }

    [Fact]
    public async Task Opening_a_notification_marks_it_read_and_drops_it_from_the_popup()
    {
        var gh = new FakeGh { Notes = TwoNotes };
        var shell = new FakeShell();
        var (connector, ctx) = await Started(gh, shell);
        await using var _ = connector;
        Assert.Equal("2 unread", ctx.Updates[^1].Title);

        await connector.OnActionAsync("note:501", CancellationToken.None);

        Assert.Equal("https://github.com/acme/web/issues/3", Assert.Single(shell.Opened));
        Assert.Contains("api -X PATCH notifications/threads/501", gh.Calls);
        Assert.Equal("1 unread", ctx.Updates[^1].Title);
        var popup = (await connector.GetFlyoutAsync(CancellationToken.None))!;
        Assert.DoesNotContain(popup.Rows, r => r.Action == "note:501");
    }

    [Fact]
    public async Task Mark_all_read_clears_the_notifications()
    {
        var gh = new FakeGh { Notes = TwoNotes };
        var (connector, ctx) = await Started(gh, new FakeShell());
        await using var _ = connector;

        await connector.OnActionAsync("readall", CancellationToken.None);

        Assert.Contains(gh.Calls, c => c.StartsWith("api -X PUT notifications -f last_read_at=2026-11-11T12:00:00Z", StringComparison.Ordinal));
        Assert.Equal("All clear", ctx.Updates[^1].Title);
    }

    [Fact]
    public async Task Refresh_fetches_again()
    {
        var gh = new FakeGh();
        var (connector, ctx) = await Started(gh, new FakeShell());
        await using var _ = connector;
        var before = gh.Calls.Count(c => c.StartsWith("api graphql", StringComparison.Ordinal));
        gh.Notes = TwoNotes;

        await connector.OnActionAsync("reload", CancellationToken.None);

        Assert.Equal(before + 1, gh.Calls.Count(c => c.StartsWith("api graphql", StringComparison.Ordinal)));
        Assert.Equal("2 unread", ctx.Updates[^1].Title);
    }

    [Fact]
    public async Task A_failed_first_fetch_shows_the_error_look_and_a_later_one_keeps_the_last_list()
    {
        var gh = new FakeGh { SearchResult = new GhResult(1, "", "HTTP 502: Bad Gateway") };
        var (connector, ctx) = await Started(gh, new FakeShell());
        await using var _ = connector;
        Assert.Equal("GitHub: HTTP 502: Bad Gateway", ctx.Errors[^1].Message);

        gh.SearchResult = null;
        gh.Notes = TwoNotes;
        await connector.FetchNowAsync(CancellationToken.None);
        gh.SearchResult = new GhResult(1, "", "HTTP 502: Bad Gateway");
        await connector.FetchNowAsync(CancellationToken.None);

        Assert.Equal("2 unread", ctx.Updates[^1].Title);
        Assert.Contains("Not updated: GitHub: HTTP 502: Bad Gateway", ctx.Updates[^1].Tooltip);
    }

    [Fact]
    public async Task Only_github_links_open()
    {
        var shell = new FakeShell();
        var (connector, _) = await Started(new FakeGh(), shell);
        await using var __ = connector;

        await connector.OnActionAsync("open:https://evil.example/x", CancellationToken.None);
        await connector.OnActionAsync("open:https://github.com/acme/api/pull/1", CancellationToken.None);

        Assert.Equal("https://github.com/acme/api/pull/1", Assert.Single(shell.Opened));
    }

    [Fact]
    public async Task Runs_are_fetched_for_watched_repositories_and_the_repositories_of_your_pull_requests()
    {
        var gh = new FakeGh
        {
            Search = """
                { "data": { "viewer": { "login": "me" }, "review": { "nodes": [] }, "issues": { "nodes": [] },
                  "mine": { "nodes": [ { "number": 1, "title": "T", "url": "https://github.com/acme/web/pull/1", "updatedAt": "2026-11-11T10:00:00Z", "repository": { "nameWithOwner": "acme/web" } } ] } } }
                """,
        };
        var (connector, _) = await Started(gh, new FakeShell(), """{ "repos": "acme/api" }""");
        await using var __ = connector;

        Assert.Contains(gh.Calls, c => c.StartsWith("api repos/acme/api/actions/runs?per_page=30&created=%3E%3D", StringComparison.Ordinal) && !c.Contains("actor="));
        Assert.Contains(gh.Calls, c => c.StartsWith("api repos/acme/web/actions/runs?", StringComparison.Ordinal) && c.EndsWith("&actor=me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switched_off_sections_are_not_fetched()
    {
        var gh = new FakeGh();
        var (connector, _) = await Started(gh, new FakeShell(), """{ "sections": ["Reviews"], "repos": "acme/api" }""");
        await using var __ = connector;

        Assert.DoesNotContain(gh.Calls, c => c.Contains("notifications", StringComparison.Ordinal) || c.Contains("actions/runs", StringComparison.Ordinal));
    }
}
