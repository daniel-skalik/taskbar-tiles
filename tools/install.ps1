<#
.SYNOPSIS
    Builds Taskbar Tiles and installs it for the current user. install.cmd in the repository root runs this script.

.DESCRIPTION
    Run it from your own terminal or by double-clicking install.cmd (not from an agent session: the Claude desktop app
    redirects an agent's writes under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the
    installed app and the bundled tile kinds.

    1. Publishes src\TaskbarTiles.Host (Release) into a staging folder next to the install folder and verifies it.
    2. Stops TaskbarTiles.exe if it runs from the install folder.
    3. Swaps the staging folder in for the install folder.
    4. Replaces the bundled tile kinds uptime, ping, media, claude and battery in the tiles folder with the new copies.
    5. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    6. Starts the installed app.

    If the publish fails, the running app and the installed copy are left untouched.

    Your config.json, logs and every other folder in tiles\ (your own kinds) are never touched. To change a bundled
    kind, copy its folder under a new name first: install replaces tiles\uptime, tiles\ping, tiles\media,
    tiles\claude and tiles\battery.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.

.PARAMETER TilesDir
    Tile kinds folder whose bundled kinds are replaced. Default: %LOCALAPPDATA%\TaskbarTiles\tiles.

.PARAMETER NoShortcut
    Do not create or update the Startup shortcut.

.PARAMETER NoStart
    Do not start the app afterwards.

.EXAMPLE
    install.cmd

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install.ps1 -AppDir D:\tmp\app -TilesDir D:\tmp\tiles -NoShortcut -NoStart
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [string]$TilesDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\tiles'),
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\TaskbarTiles.Host\TaskbarTiles.Host.csproj'
$AppDir = [System.IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$exe = Join-Path $AppDir 'TaskbarTiles.exe'
$stage = "$AppDir.new"
$old = "$AppDir.old"
$TilesDir = [System.IO.Path]::GetFullPath($TilesDir).TrimEnd('\')
$bundledKinds = @('uptime', 'ping', 'media', 'claude', 'battery') # replaced on every install; other folders in tiles\ are the user's

if (-not (Test-Path -LiteralPath $project)) { throw "Cannot find $project. Run this script from the repository's tools folder." }

# 1. Publish into a staging folder and verify it. A failure here leaves the running app and the install untouched.
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
Write-Host "Publishing Release into $stage ..."
# --disable-build-servers: a build node left running by another process (for example an agent session, whose
# writes the Claude desktop app redirects) would otherwise do the copy, and the files land somewhere else.
& dotnet publish $project -c Release -o $stage --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    throw "dotnet publish failed with exit code $LASTEXITCODE. The installed app was not changed."
}
foreach ($required in @((Join-Path $stage 'TaskbarTiles.exe'),
        (Join-Path $stage 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'),
        (Join-Path $stage 'tiles\media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $stage 'tiles\media\tile.json'),
        (Join-Path $stage 'tiles\ping\tile.json'),
        (Join-Path $stage 'tiles\uptime\tile.json'),
        (Join-Path $stage 'tiles\claude\tile.json'),
        (Join-Path $stage 'tiles\battery\TaskbarTiles.Plugins.Battery.dll'),
        (Join-Path $stage 'tiles\battery\tile.json'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw "Publish output is missing $required. The installed app was not changed."
    }
}
Write-Host 'Published and verified the new build.'

# 2. Stop the installed copy so its folder can be replaced. Copies running from elsewhere are left alone.
$running = @(Get-Process -Name TaskbarTiles -ErrorAction SilentlyContinue)
$installed = @($running | Where-Object { $_.Path -and $_.Path.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase) })
$others = @($running | Where-Object { $installed -notcontains $_ })
foreach ($p in $installed) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (-not $p.WaitForExit(10000)) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw "TaskbarTiles (process $($p.Id)) did not exit within 10 seconds. The installed app was not changed."
    }
    Write-Host "Stopped TaskbarTiles (process $($p.Id))."
}
foreach ($p in $others) {
    Write-Warning "TaskbarTiles is also running from $($p.Path) (process $($p.Id)); it keeps running (one instance at a time)."
}
if ($installed.Count -eq 0) { Write-Host 'TaskbarTiles was not running from the install folder.' }

# 3. Swap the folders; put the old one back (and restart it) if the swap fails.
Write-Host "Replacing $AppDir ..."
$moved = $false
try {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    if (Test-Path -LiteralPath $AppDir) {
        Move-Item -LiteralPath $AppDir -Destination $old
        $moved = $true
    }
    Move-Item -LiteralPath $stage -Destination $AppDir
} catch {
    $reason = $_.Exception.Message
    if ($moved -and -not (Test-Path -LiteralPath $AppDir)) { Move-Item -LiteralPath $old -Destination $AppDir }
    $restarted = ''
    if ($installed.Count -gt 0 -and -not $NoStart -and (Test-Path -LiteralPath $exe)) {
        Start-Process -FilePath $exe -WorkingDirectory $AppDir
        $restarted = ' The previous TaskbarTiles was started again.'
    }
    throw "Could not replace the install folder: $reason The previous install was restored.$restarted"
}
if (Test-Path -LiteralPath $old) {
    try { Remove-Item -LiteralPath $old -Recurse -Force } catch { Write-Warning "Could not remove the old copy $old : $($_.Exception.Message)" }
}
Write-Host "Installed $exe"

# 4. Replace the bundled tile kinds (the app is stopped, as for the swap). Other folders in tiles\ are never touched.
New-Item -ItemType Directory -Force -Path $TilesDir | Out-Null
foreach ($kind in $bundledKinds) {
    $source = Join-Path $AppDir "tiles\$kind"
    $target = Join-Path $TilesDir $kind
    try {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Copy-Item -LiteralPath $source -Destination $target -Recurse
    } catch {
        throw "Could not replace $target : $($_.Exception.Message) The new app is installed but was not started; close whatever uses that folder and run the install again."
    }
    Write-Host "Replaced tile kind $kind in $TilesDir"
}

# 5. Startup shortcut (shows in Task Manager > Startup apps).
if ($NoShortcut) {
    Write-Host 'Skipped the Startup shortcut (-NoShortcut).'
} else {
    $link = Join-Path ([Environment]::GetFolderPath('Startup')) 'TaskbarTiles.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $AppDir
    $shortcut.Description = 'Taskbar Tiles'
    $shortcut.Save()
    Write-Host "Startup shortcut: $link -> $exe"
}

# 6. Start.
if ($NoStart) {
    Write-Host 'Not started (-NoStart).'
} elseif ($others.Count -gt 0) {
    Write-Host "Not started: TaskbarTiles is already running from $($others[0].Path). Exit it and start $exe to use the new build."
} else {
    Start-Process -FilePath $exe -WorkingDirectory $AppDir
    Write-Host 'Started TaskbarTiles.'
}
