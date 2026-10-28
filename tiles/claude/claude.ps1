# Taskbar Tiles bundled kind "claude": Claude Code sessions from `claude agents --json`. Hooks (tile.json): interval, flyout,
# action:open. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# Settings: "claude" (path to claude.exe; default: claude on PATH, then %USERPROFILE%\.local\bin\claude.exe).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8 # also how claude's output is decoded

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }

if ($env:TILE_HOOK -eq 'action' -and $env:TILE_ACTION -eq 'open') {
    # Launching the Start-menu app focuses a running instance.
    $app = Get-StartApps | Where-Object { $_.Name -eq 'Claude' } | Select-Object -First 1
    if (-not $app) { [Console]::Error.WriteLine('The Claude desktop app is not installed.'); exit 1 }
    Start-Process explorer.exe "shell:AppsFolder\$($app.AppID)"
    exit 0
}

function Find-Claude {
    if ($settings -and $settings.claude) { return [string]$settings.claude }
    $onPath = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    $local = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
    if (Test-Path $local) { return $local }
    return $null
}

$claude = Find-Claude
if (-not $claude -or -not (Test-Path $claude)) {
    [Console]::Error.WriteLine("claude not found. Install Claude Code, or set ""claude"" to its path in this tile's settings.")
    exit 1
}
$output = (& $claude agents --json 2>$null) -join "`n"
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("claude agents --json exited with code $LASTEXITCODE."); exit 1 }
# Two steps: Windows PowerShell 5.1 emits a parsed array as one pipeline item, so @(... | ConvertFrom-Json) would nest it.
try { $parsed = $output | ConvertFrom-Json; $sessions = @($parsed) } catch {
    [Console]::Error.WriteLine("claude agents --json printed something that is not JSON: $output"); exit 1
}

# Interactive sessions report "status" (busy, idle); background agents report "state" (e.g. blocked).
function Get-State($s) {
    $raw = if ($s.status) { [string]$s.status } elseif ($s.state) { [string]$s.state } else { 'unknown' }
    switch ($raw) { 'busy' { 'working' } default { $raw } }
}
function Get-Rank([string]$state) { switch ($state) { 'blocked' { 0 } 'working' { 1 } default { 2 } } }
function Get-Name($s) { if ($s.name) { [string]$s.name } else { Split-Path ([string]$s.cwd) -Leaf } }

$items = @($sessions | ForEach-Object { [pscustomobject]@{ Name = Get-Name $_; State = Get-State $_ } } |
    Sort-Object { Get-Rank $_.State }, Name)

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @($items | ForEach-Object {
        $color = switch ($_.State) { 'working' { 'accent' } 'blocked' { 'warn' } default { 'neutral' } }
        @{ text = $_.Name; value = $_.State; icon = @{ glyph = 'info'; color = $color } }
    })
    @{
        title    = 'Claude sessions'
        subtitle = if ($items.Count) { "$($items.Count) running" } else { 'No sessions' }
        rows     = $rows
        actions  = @(@{ id = 'open'; label = 'Open Claude'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval prints the tile.
$working = @($items | Where-Object State -eq 'working')
$blocked = @($items | Where-Object State -eq 'blocked')
$idle = $items.Count - $working.Count - $blocked.Count
$parts = @() # most urgent first: the tile cuts the subtitle off at its end
if ($blocked.Count) { $parts += "$($blocked.Count) blocked" }
if ($working.Count) { $parts += "$($working.Count) working" }
if ($idle) { $parts += "$idle idle" }

$state = @{ title = 'Claude' }
if (-not $items.Count) {
    $state.subtitle = 'No sessions'
    $state.icon = @{ glyph = 'check'; color = 'neutral' }
} else {
    $state.subtitle = $parts -join " $([char]0x00B7) " # middle dot; the file stays ASCII for Windows PowerShell 5.1
    $state.icon = if ($blocked.Count) { @{ glyph = 'warning'; color = 'warn' } }
        elseif ($working.Count) { @{ glyph = 'sync'; color = 'accent' } }
        else { @{ glyph = 'check'; color = 'ok' } }
}
$active = @($blocked + $working | ForEach-Object { "$($_.Name) ($($_.State))" })
$state.tooltip = if ($active.Count) { $active -join "`n" } else { 'No session is working. Click for details.' }
$state | ConvertTo-Json -Depth 5 -Compress
