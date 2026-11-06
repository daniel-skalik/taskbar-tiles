[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
# Taskbar Tiles bundled kind "github": open pull requests that wait for your review, and yours that need you, are
# blocked or wait for others. Hooks (tile.json): interval, flyout, action:open (ids "open:<url>"), action:install,
# action:signin. See docs/script-connectors.md. Replaced on every install: copy this
# folder under a new name before changing it.
# Talks to GitHub through the GitHub CLI (gh) and its sign-in; this script never sees, stores or prints a token.
# Settings: "refresh" (how often to fetch), "filter" (extra search qualifiers such as "org:acme").
# Testing only: TILE_GITHUB_RESPONSE names a file read instead of calling gh (no cache then).

$dot = [char]0x00B7
$cache = Join-Path ([IO.Path]::GetTempPath()) "TaskbarTiles-github-$env:TILE_ID.json"
$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$filter = if ($settings -and $settings.filter) { ([string]$settings.filter -replace '[\r\n"]+', ' ').Trim() } else { '' }
$query = 'query($review: String!, $mine: String!) { review: search(query: $review, type: ISSUE, first: 30) { nodes { ...pr } } mine: search(query: $mine, type: ISSUE, first: 30) { nodes { ...pr } } } fragment pr on PullRequest { number title url isDraft updatedAt reviewDecision mergeable mergeStateStatus repository { nameWithOwner } commits(last: 1) { nodes { commit { statusCheckRollup { state } } } } }'

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
    @{ Data = $r.data }
}

function Read-Cache {
    if ($env:TILE_GITHUB_RESPONSE -or -not (Test-Path -LiteralPath $cache)) { return $null }
    try { return Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json } catch { return $null }
}

function Write-Cache($data) {
    if ($env:TILE_GITHUB_RESPONSE) { return }
    try { @{ fetched_at = [DateTimeOffset]::UtcNow.ToString('o'); filter = $filter; data = $data } | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $cache -Encoding UTF8 } catch { }
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

function Get-Name($pr) { "$(($pr.repository.nameWithOwner -split '/')[-1])#$($pr.number) $($pr.title)" }

$hook = $env:TILE_HOOK
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
            @{ title = 'Pull requests'; subtitle = 'Needs the GitHub CLI (gh)'
               rows = @('This tile reads your pull requests through gh and its sign-in.', 'Install opens a window that installs it with winget, then signs you in.')
               actions = @(@{ id = 'open:https://cli.github.com/'; label = 'About gh' }, @{ id = 'install'; label = 'Install gh'; primary = $true }) }
        } else {
            @{ title = 'Pull requests'; subtitle = 'Not signed in to GitHub'
               rows = @('Sign in opens a window that runs gh auth login in your browser.')
               actions = @(@{ id = 'signin'; label = 'Sign in'; primary = $true }) }
        }
        $popup | ConvertTo-Json -Depth 5 -Compress
    } else {
        @{ title = 'GitHub'; subtitle = $(if ($setup -eq 'install') { 'Click to install gh' } else { 'Click to sign in' }); icon = @{ glyph = 'sync'; color = 'neutral' } } | ConvertTo-Json -Depth 5 -Compress
    }
    exit 0
}

# interval always fetches; the popup takes the cache while it is young enough and was fetched with the same filter,
# and while gh is still there.
if ((-not $env:TILE_GITHUB_RESPONSE -or $env:TILE_GITHUB_GH) -and -not (Get-Gh)) { Write-Setup 'install' }
$cached = Read-Cache
if ($cached -and [string]$cached.filter -ne $filter) { $cached = $null }
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
$needYou = $blocked.Count + $action.Count

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
    if (-not $rows.Count) { $rows = @('No open pull requests for you') }
    $subtitle = if ($stale) { "Not updated: $stale" } else { $summary }
    @{
        title    = 'Pull requests'
        subtitle = $subtitle
        rows     = $rows
        actions  = @(
            @{ id = 'open:https://github.com/pulls/review-requested'; label = 'Reviews' },
            @{ id = 'open:https://github.com/pulls'; label = 'Open GitHub'; primary = $true })
    } | ConvertTo-Json -Depth 6 -Compress
    exit 0
}

# interval prints the tile: what needs you first, then reviews, then what waits on others.
$top = @($blocked | ForEach-Object { $_.Pr }) + @($action | ForEach-Object { $_.Pr }) + $review + @($waiting | ForEach-Object { $_.Pr })
$title = if ($needYou -and $review.Count) { "$needYou need you $dot $($review.Count) review" }
    elseif ($needYou) { "$needYou need you" }
    elseif ($review.Count) { "$($review.Count) to review" }
    elseif ($waiting.Count) { "$($waiting.Count) waiting" }
    else { 'All clear' }
$color = if ($blocked.Count) { 'bad' } elseif ($action.Count) { 'warn' } elseif ($review.Count) { 'accent' } elseif ($waiting.Count) { 'neutral' } else { 'ok' }
$tip = @($summary) + @($top | Select-Object -First 6 | ForEach-Object { Get-Name $_ })
if ($stale) { $tip += "Not updated: $stale" }
@{
    title    = $title
    subtitle = $(if ($top.Count) { "#$($top[0].number) $($top[0].title)" } else { 'Nothing open' })
    icon     = @{ glyph = 'sync'; color = $color }
    tooltip  = $tip -join "`n"
} | ConvertTo-Json -Depth 5 -Compress
