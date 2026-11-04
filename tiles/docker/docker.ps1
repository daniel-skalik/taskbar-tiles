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
    @{
        title    = 'Docker'
        subtitle = 'Not running'
        icon     = @{ glyph = 'package'; color = 'neutral' }
        tooltip  = 'Docker Desktop is not running. Click to start it.'
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# running, attention (restarting, unhealthy, paused, dead) or stopped; attention and failed exits get warm colors.
$containers = foreach ($line in $lines) {
    try { $c = $line | ConvertFrom-Json } catch { continue }
    $state = [string]$c.State
    $status = [string]$c.Status
    $trouble = $state -in @('restarting', 'dead', 'paused') -or $status -match '\(unhealthy\)'
    $failed = $state -eq 'exited' -and $status -match 'Exited \((\d+)\)' -and $Matches[1] -ne '0'
    [pscustomobject]@{
        Name    = ([string]$c.Names).Split(',')[0]
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
    # Trouble first, then running, then the rest, each by name; at most 12 rows.
    $sorted = @($containers | Sort-Object @{ e = { if ($_.Trouble) { 0 } elseif ($_.Running) { 1 } else { 2 } } }, Name)
    $rows = @($sorted | Select-Object -First 12 | ForEach-Object {
        @{ text = $_.Name; value = $_.Status; icon = @{ glyph = 'info'; color = $_.Color } }
    })
    if ($sorted.Count -gt 12) { $rows += @{ text = "and $($sorted.Count - 12) more"; value = '' } }
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
@{
    title    = 'Docker'
    subtitle = $subtitle
    icon     = @{ glyph = 'package'; color = $color }
    tooltip  = if ($tip.Count) { $tip -join "`n" } else { 'No container is running' }
} | ConvertTo-Json -Depth 5 -Compress
