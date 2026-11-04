# Taskbar Tiles bundled kind "docker": your Docker containers, from the docker CLI that Docker Desktop installs.
# Hooks (tile.json): interval, flyout, action:open. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# Settings: "docker" (path to docker.exe; default: docker on PATH).
# For tests: TILE_DOCKER_SAMPLE names a file of `docker ps -a --format "{{json .}}"` lines read instead of running
# docker ("down" in it: the engine is not running).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$dot = [char]0x00B7 # middle dot; the file stays ASCII for Windows PowerShell 5.1

function Find-Docker {
    if ($settings -and $settings.docker) { return [string]$settings.docker }
    $cmd = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

# Docker Desktop.exe: two folders above the docker.exe it installs, or its usual install folders.
function Find-DockerDesktop($docker) {
    $candidates = @()
    if ($docker) { $candidates += Join-Path (Split-Path (Split-Path (Split-Path $docker))) 'Docker Desktop.exe' }
    $candidates += Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    $candidates += Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\Docker Desktop.exe'
    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

$docker = Find-Docker

# docker.png next to this script as base64 PNG: grey when $grey, with a status dot in the bottom-right corner when
# $dotColor (#RRGGBB) is given. Null when the file is missing: the tile then keeps its glyph icon.
function Get-LogoImage([string]$dotColor, [switch]$grey) {
    $path = Join-Path $PSScriptRoot 'docker.png'
    if (-not (Test-Path $path)) { return $null }
    if (-not $dotColor -and -not $grey) { return [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
    Add-Type -AssemblyName System.Drawing
    $logo = [System.Drawing.Image]::FromFile($path)
    try {
        $size = $logo.Width
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        if ($grey) {
            # Luminance weights, a little dimmed: Docker is off.
            $m = New-Object System.Drawing.Imaging.ColorMatrix (,[single[][]]@(
                [single[]]@(0.25, 0.25, 0.25, 0, 0), [single[]]@(0.5, 0.5, 0.5, 0, 0), [single[]]@(0.1, 0.1, 0.1, 0, 0),
                [single[]]@(0, 0, 0, 0.8, 0), [single[]]@(0, 0, 0, 0, 1)))
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            $attributes.SetColorMatrix($m)
            $g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $size, $size, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        } else {
            $g.DrawImage($logo, 0, 0, $size, $size)
        }
        if ($dotColor) {
            $d = [int]($size * 0.42); $x = $size - $d; $ring = [Math]::Max(2, [int]($size / 16))
            $g.FillEllipse([System.Drawing.Brushes]::Black, $x - $ring, $x - $ring, $d + $ring, $d + $ring) # dark ring keeps the dot apart from the logo
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($dotColor))
            $g.FillEllipse($brush, $x, $x, $d - $ring, $d - $ring)
            $brush.Dispose()
        }
        $g.Dispose()
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        return [Convert]::ToBase64String($stream.ToArray())
    } finally { $logo.Dispose() }
}

if ($env:TILE_HOOK -eq 'action' -and $env:TILE_ACTION -eq 'open') {
    $desktop = Find-DockerDesktop $docker
    if (-not $desktop) { [Console]::Error.WriteLine('Docker Desktop was not found.'); exit 1 }
    Start-Process -FilePath $desktop
    exit 0
}

# Runs docker with $arguments; gives up after $seconds (a starting engine can keep it waiting).
function Invoke-Docker([string]$arguments, [int]$seconds = 6) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $docker, $arguments
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($info)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) {
        try { $p.Kill() } catch { }
        return @{ Code = -1; Out = ''; Err = "docker did not answer within $seconds s" }
    }
    return @{ Code = $p.ExitCode; Out = $out.Result; Err = $err.Result }
}

# The containers, or $null when the engine is not running.
if ($env:TILE_DOCKER_SAMPLE) {
    $text = Get-Content -LiteralPath $env:TILE_DOCKER_SAMPLE -Raw
    $lines = if ($text -match '^\s*down\s*$') { $null } else { @($text -split "`r?`n" | Where-Object { $_.Trim() }) }
} else {
    if (-not $docker) { [Console]::Error.WriteLine('Docker is not installed: docker was not found on PATH.'); exit 1 }
    $run = Invoke-Docker 'ps -a --no-trunc --format "{{json .}}"'
    # Not running answers "error during connect" / "cannot connect", or nothing in time: a normal state.
    $lines = if ($run.Code -eq 0) { @($run.Out -split "`r?`n" | Where-Object { $_.Trim() }) } else { $null }
}

if ($null -eq $lines) {
    if ($env:TILE_HOOK -eq 'flyout') {
        @{
            title    = 'Docker'
            subtitle = 'Docker Desktop is not running'
            rows     = @()
            actions  = @(@{ id = 'open'; label = 'Start Docker Desktop'; primary = $true })
        } | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    $state = @{
        title    = 'Docker'
        subtitle = 'Not running'
        icon     = @{ glyph = 'package'; color = 'neutral' }
        tooltip  = 'Docker Desktop is not running. Click to start it.'
    }
    $image = Get-LogoImage '' -grey
    if ($image) { $state.image = $image }
    $state | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# running, attention (restarting, unhealthy, paused, dead) or stopped; attention and failed exits get warm colors.
$containers = foreach ($line in $lines) {
    try { $c = $line | ConvertFrom-Json } catch { continue }
    $state = [string]$c.State
    $status = [string]$c.Status
    $trouble = $state -in @('restarting', 'dead', 'paused') -or $status -match '\(unhealthy\)'
    $failed = $state -eq 'exited' -and $status -match 'Exited \((\d+)\)' -and $Matches[1] -ne '0'
    # Compose projects group their containers, as in Docker Desktop: "projects-hub" > "worker-1".
    $project = if ([string]$c.Labels -match '(?:^|,)com\.docker\.compose\.project=([^,]+)') { $Matches[1] } else { '' }
    $name = ([string]$c.Names).Split(',')[0]
    [pscustomobject]@{
        Name    = $name
        Project = $project
        Short   = if ($project -and $name.StartsWith("$project-")) { $name.Substring($project.Length + 1) } else { $name }
        Status  = $status
        Running = $state -eq 'running'
        Trouble = $trouble
        Color   = if ($trouble) { 'warn' } elseif ($state -eq 'running') { 'ok' } elseif ($failed) { 'bad' } else { 'neutral' }
    }
}
$containers = @($containers)
$running = @($containers | Where-Object Running).Count
$stopped = $containers.Count - $running
$trouble = @($containers | Where-Object Trouble).Count

if ($env:TILE_HOOK -eq 'flyout') {
    # Compose projects as a row each with their containers indented under it, and lone containers: trouble first,
    # then running, then the rest, each by name; at most 14 rows.
    $indent = [string][char]0x2003 * 2
    function Get-Rank($items) { if (@($items | Where-Object Trouble).Count) { 0 } elseif (@($items | Where-Object Running).Count) { 1 } else { 2 } }
    $entries = @($containers | Group-Object { if ($_.Project) { "p:$($_.Project)" } else { "c:$($_.Name)" } } | ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{ Name = $(if ($first.Project) { $first.Project } else { $first.Name }); Project = [bool]$first.Project; Items = @($_.Group); Rank = (Get-Rank $_.Group) }
    } | Sort-Object Rank, Name)
    $rows = @()
    foreach ($e in $entries) {
        if (-not $e.Project) {
            $c = $e.Items[0]
            $rows += @{ text = $c.Name; value = $c.Status; icon = @{ glyph = 'info'; color = $c.Color } }
            continue
        }
        $up = @($e.Items | Where-Object Running).Count; $down = $e.Items.Count - $up
        $value = if ($up -and $down) { "$up running $dot $down stopped" } elseif ($up) { "$up running" } else { "$down stopped" }
        $color = if (@($e.Items | Where-Object Trouble).Count) { 'warn' } elseif ($up) { 'ok' } elseif (@($e.Items | Where-Object { $_.Color -eq 'bad' }).Count) { 'bad' } else { 'neutral' }
        $rows += @{ text = $e.Name; value = $value; icon = @{ glyph = 'info'; color = $color } }
        foreach ($c in @($e.Items | Sort-Object @{ e = { if ($_.Trouble) { 0 } elseif ($_.Running) { 1 } else { 2 } } }, Short)) {
            $rows += @{ text = "$indent$($c.Short)"; value = $c.Status; icon = @{ glyph = 'info'; color = $c.Color } }
        }
    }
    if ($rows.Count -gt 14) { $more = $rows.Count - 13; $rows = @($rows[0..12]) + @{ text = "and $more more"; value = '' } }
    if ($containers.Count -eq 0) { $rows = @('No containers') }
    @{
        title    = 'Docker'
        subtitle = "$running running $dot $stopped stopped"
        rows     = $rows
        actions  = @(@{ id = 'open'; label = 'Open Docker Desktop'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

$subtitle = if ($containers.Count -eq 0) { 'No containers' } else { "$running running $dot $stopped stopped" }
if ($trouble -gt 0) { $subtitle = "$trouble with problems $dot $subtitle" }
$color = if ($trouble -gt 0) { 'warn' } elseif ($running -gt 0) { 'ok' } else { 'neutral' }
$tip = @($containers | Where-Object { $_.Running -or $_.Trouble } | ForEach-Object { "$($_.Name): $($_.Status)" })
$state = @{
    title    = 'Docker'
    subtitle = $subtitle
    icon     = @{ glyph = 'package'; color = $color }
    tooltip  = if ($tip.Count) { $tip -join "`n" } else { 'No container is running' }
}
# The Docker logo replaces the glyph, with an orange dot while a container has problems.
$image = Get-LogoImage $(if ($trouble -gt 0) { '#F2A33A' } else { '' })
if ($image) { $state.image = $image }
$state | ConvertTo-Json -Depth 5 -Compress
