[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
# Taskbar Tiles bundled kind "github": open pull requests that wait for your review, and yours that need you, are
# blocked or wait for others; and recent GitHub Actions runs. Hooks (tile.json): interval, flyout, action:open
# (ids "open:<url>"), action:reload, action:install, action:signin. See docs/script-connectors.md. Replaced on every install: copy this
# folder under a new name before changing it.
# Talks to GitHub through the GitHub CLI (gh) and its sign-in; this script never sees, stores or prints a token.
# Settings: "refresh" (how often to fetch), "filter" (extra search qualifiers such as "org:acme"), "repos" (repositories
# whose Actions runs to show from anyone; runs you started show from the repositories of your open pull requests too).
# Testing only: TILE_GITHUB_RESPONSE names a file read instead of calling gh (no cache then); its data.runs holds the
# workflow runs as GitHub's REST API lists them.

$dot = [char]0x00B7
$cache = Join-Path ([IO.Path]::GetTempPath()) "TaskbarTiles-github-$env:TILE_ID.json"
$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$filter = if ($settings -and $settings.filter) { ([string]$settings.filter -replace '[\r\n"]+', ' ').Trim() } else { '' }
# "owner/name" entries of the repos setting: a list or a comma- or space-separated string. At most 10.
$watched = @(@($(if ($settings -and $settings.repos) { $settings.repos } else { @() })) | ForEach-Object { "$_" -split '[,;\s]+' } |
    ForEach-Object { $_.Trim().Trim('/') } | Where-Object { $_ -match '^[\w.-]+/[\w.-]+$' } | Select-Object -Unique -First 10)
$runHours = 24 # runs older than this are left out
$query = 'query($review: String!, $mine: String!) { viewer { login } review: search(query: $review, type: ISSUE, first: 30) { nodes { ...pr } } mine: search(query: $mine, type: ISSUE, first: 30) { nodes { ...pr } } } fragment pr on PullRequest { number title url isDraft updatedAt reviewDecision mergeable mergeStateStatus repository { nameWithOwner } commits(last: 1) { nodes { commit { statusCheckRollup { state } } } } }'

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }

# Where gh.exe is, or $null. The app's PATH is the one it started with, so a gh installed since then is looked for
# in the folders its installers use. Testing only: TILE_GITHUB_GH names the gh to use (a missing path means no gh).
function Get-Gh {
    if ($env:TILE_GITHUB_GH) { if (Test-Path -LiteralPath $env:TILE_GITHUB_GH) { return $env:TILE_GITHUB_GH } else { return $null } }
    $onPath = Get-Command gh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    foreach ($dir in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path "$env:LOCALAPPDATA" 'Programs'))) {
        if (-not $dir) { continue }
        $candidate = Join-Path $dir 'GitHub CLI\gh.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $null
}

# Runs a PowerShell command in a new console window the user can see and answer (winget and gh auth ask questions).
function Start-Console([string]$command) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process powershell.exe -ArgumentList '-NoProfile', '-NoExit', '-EncodedCommand', $encoded
}

# One fetch. Returns @{ Data } or @{ Error; Setup }; Setup is 'install' (no gh) or 'signin' (gh is not signed in).
function Invoke-GitHub {
    $gh = Get-Gh
    if ($env:TILE_GITHUB_GH -and -not $gh) { return @{ Setup = 'install'; Error = 'Needs the GitHub CLI' } }
    if ($env:TILE_GITHUB_RESPONSE) { $text = Get-Content -LiteralPath $env:TILE_GITHUB_RESPONSE -Raw; $code = 0 }
    else {
        if (-not $gh) { return @{ Setup = 'install'; Error = 'Needs the GitHub CLI' } }
        $base = "is:pr is:open archived:false $filter".Trim()
        $out = $null
        try {
            $ErrorActionPreference = 'Continue' # gh writes errors to stderr; the exit code tells
            $out = & $gh api graphql -f "query=$query" -f "review=$base review-requested:@me" -f "mine=$base author:@me" 2>&1
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = 'Stop' }
        $text = @($out | ForEach-Object { "$_" }) -join "`n"
    }
    if ($code -ne 0) {
        if ($text -match 'auth login|not logged|401|Bad credentials') { return @{ Setup = 'signin'; Error = 'Not signed in to GitHub' } }
        $first = @($text -split "`n" | Where-Object { $_.Trim() })[0]
        return @{ Error = "GitHub: $first" }
    }
    try { $r = $text | ConvertFrom-Json } catch { return @{ Error = 'GitHub sent an answer that is not JSON' } }
    if ($r.errors) { return @{ Error = "GitHub: $(@($r.errors)[0].message)" } }
    $data = $r.data
    $raw = @(); $runErrors = @()
    if ($env:TILE_GITHUB_RESPONSE) { $raw = @($data.runs) }
    else {
        # Anyone's runs in the watched repositories; yours in the repositories of your open pull requests.
        $since = [DateTimeOffset]::UtcNow.AddHours(-$runHours).ToString('yyyy-MM-ddTHH:mm:ssZ')
        $yours = @($data.mine.nodes | Where-Object { $_ } | ForEach-Object { [string]$_.repository.nameWithOwner } | Select-Object -Unique |
            Where-Object { $_ -notin $watched } | Select-Object -First 10)
        $asks = @($watched | ForEach-Object { @{ Repo = $_; Url = "repos/$_/actions/runs?per_page=30&created=%3E%3D$since" } }) +
            @($yours | ForEach-Object { @{ Repo = $_; Url = "repos/$_/actions/runs?per_page=30&created=%3E%3D$since&actor=$($data.viewer.login)" } })
        foreach ($ask in $asks) {
            try {
                $ErrorActionPreference = 'Continue'
                $out = & $gh api $ask.Url 2>&1
                $code = $LASTEXITCODE
            } finally { $ErrorActionPreference = 'Stop' }
            $text = @($out | ForEach-Object { "$_" }) -join "`n"
            if ($code -ne 0) { $runErrors += "$($ask.Repo): $(@($text -split "`n" | Where-Object { $_.Trim() })[0])"; continue }
            try { $raw += @(($text | ConvertFrom-Json).workflow_runs) } catch { $runErrors += "$($ask.Repo): not JSON" }
        }
    }
    $runs = @(foreach ($w in $raw) {
        if (-not $w -or -not $w.id) { continue }
        [pscustomobject]@{
            id = [long]$w.id; repo = [string]$w.repository.full_name; workflow = [string]$w.name; workflowId = [string]$w.workflow_id
            branch = [string]$w.head_branch; status = [string]$w.status; conclusion = [string]$w.conclusion; url = [string]$w.html_url
            created = [string]$w.created_at
        }
    })
    @{ Data = [pscustomobject]@{ viewer = $data.viewer; review = $data.review; mine = $data.mine; runs = $runs; runErrors = $runErrors } }
}

# A workflow run's state; failed and needs-approval runs need you. Skipped runs are left out (null).
function Get-RunStatus($run) {
    if ($run.status -ne 'completed') {
        if ($run.status -in 'waiting', 'action_required') { return @{ Group = 'action'; Label = 'Needs approval'; Color = 'warn'; Glyph = 'ring'; Order = 1 } }
        return @{ Group = 'running'; Label = 'Running'; Color = 'accent'; Glyph = 'half-ring'; Order = 2 }
    }
    switch ($run.conclusion) {
        'success' { return @{ Group = 'passed'; Label = 'Passed'; Color = 'ok'; Glyph = 'ring'; Order = 3 } }
        'failure' { return @{ Group = 'failed'; Label = 'Failed'; Color = 'bad'; Glyph = 'alert'; Order = 0 } }
        'timed_out' { return @{ Group = 'failed'; Label = 'Timed out'; Color = 'bad'; Glyph = 'alert'; Order = 0 } }
        'startup_failure' { return @{ Group = 'failed'; Label = 'Failed'; Color = 'bad'; Glyph = 'alert'; Order = 0 } }
        'action_required' { return @{ Group = 'action'; Label = 'Needs approval'; Color = 'warn'; Glyph = 'ring'; Order = 1 } }
        'cancelled' { return @{ Group = 'cancelled'; Label = 'Cancelled'; Color = 'neutral'; Glyph = 'dashes'; Order = 4 } }
    }
    $null
}

# The latest run of each workflow on each branch from the last day, most pressing first: a later run that passed
# hides an earlier failure.
function Get-LatestRuns($runs) {
    $cutoff = [DateTimeOffset]::UtcNow.AddHours(-$runHours)
    $recent = @($runs | Where-Object { $_ -and $_.created -and [DateTimeOffset]$_.created -ge $cutoff })
    $newest = @{ Expression = { [DateTimeOffset]$_.created }; Descending = $true }
    $latest = @(foreach ($g in @($recent | Group-Object -Property { "$($_.repo)|$($_.workflowId)|$($_.branch)" })) { @($g.Group | Sort-Object -Property $newest)[0] })
    $rated = @(foreach ($run in $latest) { $st = Get-RunStatus $run; if ($st) { @{ Run = $run; Status = $st } } })
    @($rated | Sort-Object -Property @{ Expression = { $_.Status.Order } }, @{ Expression = { [DateTimeOffset]$_.Run.created }; Descending = $true })
}

function Get-RunName($run) { "$(($run.repo -split '/')[-1]) $dot $($run.workflow) $dot $($run.branch)" }

function Read-Cache {
    if ($env:TILE_GITHUB_RESPONSE -or -not (Test-Path -LiteralPath $cache)) { return $null }
    try { return Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json } catch { return $null }
}

function Write-Cache($data) {
    if ($env:TILE_GITHUB_RESPONSE) { return }
    try { @{ fetched_at = [DateTimeOffset]::UtcNow.ToString('o'); filter = $filter; repos = ($watched -join ','); data = $data } | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $cache -Encoding UTF8 } catch { }
}

# The popup's cache age limit: the refresh setting ("2m", "30s", "1h"), at least 2 minutes.
function Get-CacheMinutes {
    $minutes = 2.0
    if ($settings -and ([string]$settings.refresh) -match '^\s*(\d+(?:\.\d+)?)\s*([smh])\s*$') {
        $n = [double]$Matches[1]
        $asMinutes = switch ($Matches[2]) { 's' { $n / 60 } 'm' { $n } 'h' { $n * 60 } }
        if ($asMinutes -gt $minutes) { $minutes = $asMinutes }
    }
    $minutes
}

# What one of your pull requests waits on. Group: blocked (cannot merge until you fix it), action (your move),
# waiting (someone else's move), draft. The first rule that matches wins.
function Get-MyStatus($pr) {
    $checks = $null
    $commit = @($pr.commits.nodes)[0]
    if ($commit -and $commit.commit.statusCheckRollup) { $checks = [string]$commit.commit.statusCheckRollup.state }
    if ($pr.isDraft) { return @{ Group = 'draft'; Label = 'Draft'; Color = 'neutral' } }
    if ($pr.mergeable -eq 'CONFLICTING') { return @{ Group = 'blocked'; Label = 'Conflicts'; Color = 'bad' } }
    if ($checks -in 'FAILURE', 'ERROR') { return @{ Group = 'blocked'; Label = 'Checks failing'; Color = 'bad' } }
    if ($pr.reviewDecision -eq 'CHANGES_REQUESTED') { return @{ Group = 'action'; Label = 'Changes requested'; Color = 'warn' } }
    if ($pr.mergeStateStatus -eq 'BEHIND') { return @{ Group = 'action'; Label = 'Behind base'; Color = 'warn' } }
    if ($checks -in 'PENDING', 'EXPECTED') { return @{ Group = 'waiting'; Label = 'Checks running'; Color = 'neutral' } }
    if ($pr.reviewDecision -eq 'REVIEW_REQUIRED') { return @{ Group = 'waiting'; Label = 'Awaiting review'; Color = 'neutral' } }
    if ($pr.mergeStateStatus -in 'CLEAN', 'HAS_HOOKS', 'UNSTABLE') { return @{ Group = 'action'; Label = 'Ready to merge'; Color = 'ok' } }
    if ($pr.mergeStateStatus -eq 'BLOCKED') { return @{ Group = 'blocked'; Label = 'Blocked'; Color = 'bad' } }
    @{ Group = 'waiting'; Label = 'Open'; Color = 'neutral' }
}

# The GitHub mark (Primer Octicons "mark-github", MIT), bundled as github.png; null if it is missing.
function Get-Logo {
    $path = Join-Path $PSScriptRoot 'github.png'
    if (Test-Path -LiteralPath $path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) } else { $null }
}
$logo = Get-Logo

function Get-Name($pr) { "$(($pr.repository.nameWithOwner -split '/')[-1])#$($pr.number) $($pr.title)" }

$hook = $env:TILE_HOOK
# The popup's Refresh: fetch now, as interval does, and print the tile; the popup stays open and redraws from the fresh cache.
if ($hook -eq 'action' -and "$env:TILE_ACTION" -eq 'reload') { $hook = 'interval' }
if ($hook -eq 'action') {
    $action = "$env:TILE_ACTION"
    if ($action.StartsWith('open:')) {
        $url = $action.Substring(5)
        if ($url -notmatch '^https://github\.com/') { Fail 'Not a GitHub link.' }
        Start-Process $url
        exit 0
    }
    if ($action -eq 'install') {
        # winget when Windows has it (it asks before installing), else the download page.
        if (Get-Command winget.exe -ErrorAction SilentlyContinue) {
            Start-Console @'
Write-Host 'Installing the GitHub CLI for the Taskbar Tiles GitHub tile...'
winget install --id GitHub.cli --exact --source winget
$gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
if (Test-Path $gh) { Write-Host 'Now sign in to GitHub:'; & $gh auth login --web --git-protocol https }
'@
        } else { Start-Process 'https://cli.github.com/' }
        exit 0
    }
    if ($action -eq 'signin') {
        $gh = Get-Gh
        if (-not $gh) { Fail 'The GitHub CLI is not installed.' }
        Start-Console "Write-Host 'Sign in to GitHub for the Taskbar Tiles GitHub tile:'; & '$($gh.Replace("'", "''"))' auth login --web --git-protocol https"
        exit 0
    }
    Fail "Unknown action '$action'."
}

# Not set up yet: no gh, or gh not signed in. A normal state, not a failure.
function Write-Setup([string]$setup) {
    if ($hook -eq 'flyout') {
        $popup = if ($setup -eq 'install') {
            @{ title = 'GitHub'; subtitle = 'Needs the GitHub CLI (gh)'
               rows = @('This tile reads your pull requests through gh and its sign-in.', 'Install opens a window that installs it with winget, then signs you in.')
               actions = @(@{ id = 'open:https://cli.github.com/'; label = 'About gh' }, @{ id = 'install'; label = 'Install gh'; primary = $true }) }
        } else {
            @{ title = 'GitHub'; subtitle = 'Not signed in to GitHub'
               rows = @('Sign in opens a window that runs gh auth login in your browser.')
               actions = @(@{ id = 'signin'; label = 'Sign in'; primary = $true }) }
        }
        if ($logo) { $popup.image = $logo }
        $popup | ConvertTo-Json -Depth 5 -Compress
    } else {
        $tile = @{ title = 'GitHub'; subtitle = $(if ($setup -eq 'install') { 'Click to install gh' } else { 'Click to sign in' }); icon = @{ glyph = 'sync'; color = 'neutral' } }
        if ($logo) { $tile.image = $logo; $tile.imageColor = 'neutral' }
        $tile | ConvertTo-Json -Depth 5 -Compress
    }
    exit 0
}

# interval always fetches; the popup takes the cache while it is young enough and was fetched with the same filter,
# and while gh is still there.
if ((-not $env:TILE_GITHUB_RESPONSE -or $env:TILE_GITHUB_GH) -and -not (Get-Gh)) { Write-Setup 'install' }
$cached = Read-Cache
if ($cached -and ([string]$cached.filter -ne $filter -or [string]$cached.repos -ne ($watched -join ','))) { $cached = $null }
$data = $null; $stale = $null
if ($hook -eq 'flyout' -and $cached -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$cached.fetched_at).TotalMinutes -lt (Get-CacheMinutes)) {
    $data = $cached.data
} else {
    $result = Invoke-GitHub
    if ($result.Setup) { Write-Setup $result.Setup }
    if ($result.Error) {
        if (-not $cached) { Fail $result.Error }
        $data = $cached.data; $stale = $result.Error
    } else {
        $data = $result.Data
        Write-Cache $data
    }
}

$byDate = @{ Expression = { [DateTimeOffset]$_.updatedAt }; Descending = $true }
$review = @(@($data.review.nodes | Where-Object { $_ -and $_.url }) | Sort-Object -Property $byDate)
$mine = @(foreach ($pr in @(@($data.mine.nodes | Where-Object { $_ -and $_.url }) | Sort-Object -Property $byDate)) { @{ Pr = $pr; Status = (Get-MyStatus $pr) } })
$blocked = @($mine | Where-Object { $_.Status.Group -eq 'blocked' })
$action = @($mine | Where-Object { $_.Status.Group -eq 'action' })
$waiting = @($mine | Where-Object { $_.Status.Group -in 'waiting', 'draft' } | Sort-Object -Property @{ Expression = { $_.Status.Group -eq 'draft' } })
$runs = @(Get-LatestRuns @($data.runs))
$failedRuns = @($runs | Where-Object { $_.Status.Group -eq 'failed' })
$approvalRuns = @($runs | Where-Object { $_.Status.Group -eq 'action' })
$runErrors = @($data.runErrors | Where-Object { $_ })
$needYou = $blocked.Count + $action.Count + $failedRuns.Count + $approvalRuns.Count

$parts = @()
if ($review.Count) { $parts += "$($review.Count) to review" }
if ($needYou) { $parts += "$needYou need you" }
if ($waiting.Count) { $parts += "$($waiting.Count) waiting" }
$summary = if ($parts.Count) { $parts -join " $dot " } else { 'No open pull requests' }

if ($hook -eq 'flyout') {
    $rows = @()
    function Add-Group([string]$heading, $items) {
        if (-not $items.Count) { return }
        $script:rows += @{ text = "$heading ($($items.Count))" }
        foreach ($i in $items) { $script:rows += $i }
    }
    Add-Group 'Waiting for your review' @($review | ForEach-Object { @{ text = (Get-Name $_); value = 'Review'; icon = @{ glyph = 'ring'; color = 'accent' }; indent = 1; action = "open:$($_.url)" } })
    $row = { param($m) @{ text = (Get-Name $m.Pr); value = $m.Status.Label; icon = @{ glyph = $(if ($m.Status.Group -eq 'blocked') { 'alert' } else { 'ring' }); color = $m.Status.Color }; indent = 1; action = "open:$($m.Pr.url)" } }
    Add-Group 'Blocked' @($blocked | ForEach-Object { & $row $_ })
    Add-Group 'Needs your action' @($action | ForEach-Object { & $row $_ })
    Add-Group 'Waiting for others' @($waiting | ForEach-Object { & $row $_ })
    Add-Group 'Actions' @($runs | Select-Object -First 10 | ForEach-Object { @{ text = (Get-RunName $_.Run); value = $_.Status.Label; icon = @{ glyph = $_.Status.Glyph; color = $_.Status.Color }; indent = 1; action = "open:$($_.Run.url)" } })
    if (-not $rows.Count) { $rows = @('No open pull requests for you') }
    $subtitle = if ($stale) { "Not updated: $stale" } else { $summary }
    $popup = @{
        title    = 'GitHub'
        subtitle = $subtitle
        stayOpen = $true # Refresh redraws in place; opening a link moves focus to the browser, which closes it anyway
        rows     = $rows
        actions  = @(
            @{ id = 'reload'; label = 'Refresh' },
            @{ id = 'open:https://github.com/pulls'; label = 'Open GitHub'; primary = $true })
    }
    if ($logo) { $popup.image = $logo }
    $popup | ConvertTo-Json -Depth 6 -Compress
    exit 0
}

# interval prints the tile: what needs you first, then reviews, then what waits on others.
$prLine = { param($pr) "#$($pr.number) $($pr.title)" }
$runLine = { param($r) "$($r.Run.workflow): $($r.Status.Label.ToLower()) on $($r.Run.branch)" }
$top = @($blocked | ForEach-Object { & $prLine $_.Pr }) + @($failedRuns | ForEach-Object { & $runLine $_ }) +
    @($action | ForEach-Object { & $prLine $_.Pr }) + @($approvalRuns | ForEach-Object { & $runLine $_ }) +
    @($review | ForEach-Object { & $prLine $_ }) + @($waiting | ForEach-Object { & $prLine $_.Pr })
$title = if ($needYou -and $review.Count) { "$needYou need you $dot $($review.Count) review" }
    elseif ($needYou) { "$needYou need you" }
    elseif ($review.Count) { "$($review.Count) to review" }
    elseif ($waiting.Count) { "$($waiting.Count) waiting" }
    else { 'All clear' }
$color = if ($blocked.Count -or $failedRuns.Count) { 'bad' } elseif ($action.Count -or $approvalRuns.Count) { 'warn' } elseif ($review.Count) { 'accent' } elseif ($waiting.Count) { 'neutral' } else { 'ok' }
$tip = @($summary) + @($top | Select-Object -First 6)
$running = @($runs | Where-Object { $_.Status.Group -eq 'running' }).Count
if ($running) { $tip += "$running workflow runs in progress" }
if ($runErrors.Count) { $tip += "Actions not read: $($runErrors -join '; ')" }
if ($stale) { $tip += "Not updated: $stale" }
$tile = @{
    title    = $title
    subtitle = $(if ($top.Count) { $top[0] } elseif ($running) { "$running runs in progress" } else { 'Nothing open' })
    icon     = @{ glyph = 'sync'; color = $color }
    tooltip  = $tip -join "`n"
}
if ($logo) { $tile.image = $logo; $tile.imageColor = $color } # the mark in the state color
$tile | ConvertTo-Json -Depth 5 -Compress
