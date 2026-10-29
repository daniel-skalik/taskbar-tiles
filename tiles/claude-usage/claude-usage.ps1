# Taskbar Tiles bundled kind "claude-usage": your Claude plan's 5-hour and weekly usage limits, from the usage endpoint
# Claude Code's /usage reads, signed in with Claude Code's login. Hooks (tile.json): interval, flyout, action:open.
# See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# Settings: "credentials" (path to Claude Code's .credentials.json; default: %CLAUDE_CONFIG_DIR% or %USERPROFILE%\.claude).
# The token is only sent to api.anthropic.com and never written anywhere; Claude Code renews it.
# For tests: TILE_CLAUDE_USAGE_RESPONSE names a file read instead of calling the endpoint.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$dot = [char]0x00B7 # middle dot; the file stays ASCII for Windows PowerShell 5.1

if ($env:TILE_HOOK -eq 'action' -and $env:TILE_ACTION -eq 'open') {
    Start-Process 'https://claude.ai/settings/usage'
    exit 0
}

# The last answer, so the popup does not call the endpoint again and a failed call can still show recent numbers.
$cache = Join-Path ([IO.Path]::GetTempPath()) "TaskbarTiles-claude-usage-$env:TILE_ID.json"

# PowerShell 7 turns ISO dates into DateTime while parsing JSON; 5.1 keeps the string.
function ConvertTo-Offset($value) {
    if ($null -eq $value -or $value -eq '') { return $null }
    if ($value -is [datetime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Read-Cache {
    if ($env:TILE_CLAUDE_USAGE_RESPONSE) { return $null } # tests never touch the real cache
    if (-not (Test-Path $cache)) { return $null }
    try { return Get-Content $cache -Raw | ConvertFrom-Json } catch { return $null }
}

function Find-Credentials {
    if ($settings -and $settings.credentials) { return [string]$settings.credentials }
    $dir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
    return Join-Path $dir '.credentials.json'
}

# The endpoint's JSON as text; throws with a message for the tile.
function Get-Usage {
    if ($env:TILE_CLAUDE_USAGE_RESPONSE) { return Get-Content $env:TILE_CLAUDE_USAGE_RESPONSE -Raw }
    $path = Find-Credentials
    if (-not (Test-Path $path)) { throw "Not signed in: $path not found. Sign in to Claude Code first." }
    $token = (Get-Content $path -Raw | ConvertFrom-Json).claudeAiOauth.accessToken
    if (-not $token) { throw "No Claude login in $path. Sign in to Claude Code with your Claude account." }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 8 -Uri 'https://api.anthropic.com/api/oauth/usage' `
            -Headers @{ Authorization = "Bearer $token"; 'anthropic-beta' = 'oauth-2025-04-20' }
        return $response.Content
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 401) { throw 'Claude login expired. Start Claude Code once to renew it.' }
        if ($status) { throw "The usage endpoint answered $status." }
        throw "The usage endpoint could not be reached: $($_.Exception.Message)"
    }
}

# Fresh numbers for interval; the popup takes the cache while it is under 2 minutes old.
$cached = Read-Cache
$fetchedAt = $null; $stale = $null
if ($env:TILE_HOOK -eq 'flyout' -and $cached -and ([DateTimeOffset]::UtcNow - (ConvertTo-Offset $cached.fetched_at)).TotalMinutes -lt 2) {
    $json = $cached.json; $fetchedAt = ConvertTo-Offset $cached.fetched_at
} else {
    try {
        $json = Get-Usage
        $fetchedAt = [DateTimeOffset]::UtcNow
        if (-not $env:TILE_CLAUDE_USAGE_RESPONSE) { try { @{ fetched_at = $fetchedAt.ToString('o'); json = $json } | ConvertTo-Json -Compress | Set-Content $cache -Encoding UTF8 } catch { } }
    } catch {
        # Numbers under an hour old beat the error look; the tooltip says why they are not newer.
        $age = if ($cached) { [DateTimeOffset]::UtcNow - (ConvertTo-Offset $cached.fetched_at) } else { $null }
        if (-not $age -or $age.TotalMinutes -ge 60) { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        $json = $cached.json; $fetchedAt = ConvertTo-Offset $cached.fetched_at; $stale = $_.Exception.Message
    }
}
try { $usage = $json | ConvertFrom-Json } catch { [Console]::Error.WriteLine("The usage endpoint answered something that is not JSON: $json"); exit 1 }
if (-not $usage.five_hour -and -not $usage.seven_day) {
    [Console]::Error.WriteLine('No usage limits in the answer: limits are shown for Claude Pro and Max plans.'); exit 1
}

function ConvertTo-Local($value) { $o = ConvertTo-Offset $value; if ($o) { $o.ToLocalTime() } else { $null } }
function Get-Percent($window) { if ($window) { [int][Math]::Round([double]$window.utilization) } else { $null } }
# Today: 18:20. Later: Sun 07:00 (or Sun when $dayOnly).
function Format-Reset($when, [switch]$dayOnly) {
    if (-not $when) { return '' }
    if ($when.Date -eq [DateTime]::Today) { return $when.ToString('HH:mm') }
    if ($dayOnly) { return $when.ToString('ddd') }
    return $when.ToString('ddd HH:mm')
}
function Get-Color($percent) { if ($percent -ge 95) { 'bad' } elseif ($percent -ge 80) { 'warn' } else { 'ok' } }

$hour = Get-Percent $usage.five_hour; $hourReset = ConvertTo-Local $usage.five_hour.resets_at
$week = Get-Percent $usage.seven_day; $weekReset = ConvertTo-Local $usage.seven_day.resets_at
$updated = "Updated $($fetchedAt.ToLocalTime().ToString('HH:mm'))"
if ($stale) { $updated += " ($stale)" }

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    if ($null -ne $hour) { $rows += @{ text = '5 hours'; value = "$hour% $dot resets $(Format-Reset $hourReset)"; icon = @{ glyph = 'info'; color = (Get-Color $hour) } } }
    if ($null -ne $week) { $rows += @{ text = 'Week'; value = "$week% $dot resets $(Format-Reset $weekReset)"; icon = @{ glyph = 'info'; color = (Get-Color $week) } } }
    # Weekly limits of one model (e.g. Opus), when the plan has them.
    foreach ($limit in @($usage.limits | Where-Object { $_.kind -eq 'weekly_scoped' -and $_.scope.model.display_name })) {
        $p = [int]$limit.percent
        $rows += @{ text = "Week, $($limit.scope.model.display_name)"; value = "$p%"; icon = @{ glyph = 'info'; color = (Get-Color $p) } }
    }
    @{
        title    = 'Claude usage'
        subtitle = $updated
        rows     = $rows
        actions  = @(@{ id = 'open'; label = 'Open usage page'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval prints the tile.
$state = @{}
if ($null -ne $hour) {
    $state.title = "5h $hour% $dot resets $(Format-Reset $hourReset)"
    $state.progress = [Math]::Min(1.0, [Math]::Max(0.0, $hour / 100.0))
    $state.icon = @{ glyph = 'clock'; color = (Get-Color $hour) }
} else {
    $state.title = '5h: no limit'
    $state.icon = @{ glyph = 'clock'; color = 'neutral' }
}
if ($null -ne $week) {
    $state.subtitle = "Week $week% $dot resets $(Format-Reset $weekReset -dayOnly)"
    if ((Get-Color $week) -ne 'ok' -and $state.icon.color -eq 'ok') { $state.icon.color = Get-Color $week }
}
$tip = @()
if ($null -ne $hour) { $tip += "5 hours: $hour% used, resets $(Format-Reset $hourReset)" }
if ($null -ne $week) { $tip += "Week: $week% used, resets $(Format-Reset $weekReset)" }
$tip += $updated
$state.tooltip = $tip -join "`n"
$state | ConvertTo-Json -Depth 5 -Compress
