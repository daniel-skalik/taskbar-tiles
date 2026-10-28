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

# The Claude desktop app's own logo, read from its installed package; looking the package up is slow, so its path is
# cached in %TEMP%. Null when the app is not installed from its package: the tile then keeps its glyph icon.
function Find-Logo {
    $cache = Join-Path ([IO.Path]::GetTempPath()) 'TaskbarTiles-claude-logo.txt'
    $path = if (Test-Path $cache) { Get-Content $cache -Raw -ErrorAction SilentlyContinue } else { $null }
    if ($path) { $path = $path.Trim() }
    if ($path -and (Test-Path $path)) { return $path }
    try { $package = Get-AppxPackage -Name Claude -ErrorAction Stop | Select-Object -First 1 } catch { return $null }
    if (-not $package) { return $null }
    $path = Join-Path $package.InstallLocation 'assets\Square44x44Logo.targetsize-64_altform-unplated.png'
    if (-not (Test-Path $path)) { return $null }
    Set-Content $cache $path -ErrorAction SilentlyContinue
    return $path
}

# The logo as base64 PNG, with a status dot in the bottom-right corner when $dotColor (#RRGGBB) is given.
function Get-LogoImage([string]$dotColor) {
    $path = Find-Logo
    if (-not $path) { return $null }
    if (-not $dotColor) { return [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
    Add-Type -AssemblyName System.Drawing
    $logo = [System.Drawing.Image]::FromFile($path)
    try {
        $size = $logo.Width
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.DrawImage($logo, 0, 0, $size, $size)
        $d = [int]($size * 0.42); $x = $size - $d; $ring = [Math]::Max(2, [int]($size / 16))
        $g.FillEllipse([System.Drawing.Brushes]::Black, $x - $ring, $x - $ring, $d + $ring, $d + $ring) # dark ring keeps the dot apart from the logo
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($dotColor))
        $g.FillEllipse($brush, $x, $x, $d - $ring, $d - $ring)
        $g.Dispose(); $brush.Dispose()
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        return [Convert]::ToBase64String($stream.ToArray())
    } finally { $logo.Dispose() }
}

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @($items | ForEach-Object {
        $color = switch ($_.State) { 'working' { 'accent' } 'blocked' { 'warn' } default { 'neutral' } }
        @{ text = $_.Name; value = $_.State; icon = @{ glyph = 'info'; color = $color } }
    })
    $popup = @{
        title    = 'Claude sessions'
        subtitle = if ($items.Count) { "$($items.Count) running" } else { 'No sessions' }
        rows     = $rows
        actions  = @(@{ id = 'open'; label = 'Open Claude'; primary = $true })
    }
    $image = Get-LogoImage ''
    if ($image) { $popup.image = $image }
    $popup | ConvertTo-Json -Depth 5 -Compress
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
# The app's logo replaces the glyph when it can be found; the dot carries the state the glyph showed.
$dot = if ($blocked.Count) { '#F2A33A' } elseif ($working.Count) { '#4C8DF6' } else { '' }
$image = Get-LogoImage $dot
if ($image) { $state.image = $image }
$active = @($blocked + $working | ForEach-Object { "$($_.Name) ($($_.State))" })
$state.tooltip = if ($active.Count) { $active -join "`n" } else { 'No session is working. Click for details.' }
$state | ConvertTo-Json -Depth 5 -Compress
