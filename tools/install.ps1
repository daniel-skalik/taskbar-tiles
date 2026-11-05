<#
.SYNOPSIS
    Installs Taskbar Tiles for the current user: builds it from the repository, or copies it from a release zip.
    install.cmd next to this tools folder runs this script.

.DESCRIPTION
    Run it from your own terminal or by double-clicking install.cmd (not from an agent session: the Claude desktop app
    redirects an agent's writes under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the
    installed app and the bundled tile kinds.

    1. Puts the new build into a staging folder next to the install folder and verifies it. In a release zip (an app
       folder next to tools, see package.ps1) it copies app\; in the repository it publishes src\TaskbarTiles.Host
       (Release), which needs the .NET 9 SDK.
    2. Stops TaskbarTiles.exe if it runs from the install folder.
    3. Swaps the staging folder in for the install folder.
    4. Replaces the bundled tile kinds uptime, media, claude-usage, battery, clipboard, system, docker, git, todo, aquarium,
       snake, racers and cozy-farm in the tiles folder with the new copies, and removes retired bundled kinds (pixel-scene, claude, ping).
    5. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    6. Starts the installed app.

    If the publish or copy fails, the running app and the installed copy are left untouched. uninstall.cmd removes it.

    Your config.json, logs and every other folder in tiles\ (your own kinds) are never touched. To change a bundled
    kind, copy its folder under a new name first: install replaces tiles\uptime, tiles\media, tiles\claude-usage,
    tiles\battery, tiles\clipboard, tiles\system, tiles\docker, tiles\git, tiles\todo, tiles\aquarium, tiles\snake, tiles\racers and tiles\cozy-farm.

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
$packaged = Join-Path $repo 'app' # a release zip ships the published app here instead of the sources
$AppDir = [System.IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$exe = Join-Path $AppDir 'TaskbarTiles.exe'
$stage = "$AppDir.new"
$old = "$AppDir.old"
$TilesDir = [System.IO.Path]::GetFullPath($TilesDir).TrimEnd('\')
$bundledKinds = @('uptime', 'media', 'claude-usage', 'battery', 'clipboard', 'system', 'docker', 'git', 'todo', 'aquarium', 'snake', 'racers', 'cozy-farm') # replaced on every install; other folders in tiles\ are the user's
$retiredKinds = @('pixel-scene', 'claude', 'ping') # bundled once, now split up or gone; removed on install

$fromZip = Test-Path -LiteralPath (Join-Path $packaged 'TaskbarTiles.exe')
if (-not $fromZip -and -not (Test-Path -LiteralPath $project)) {
    throw "Cannot find $packaged\TaskbarTiles.exe or $project. Run install.cmd from the unzipped release or the repository."
}

# 1. Stage the new build and verify it. A failure here leaves the running app and the install untouched.
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if ($fromZip) {
    Write-Host "Copying $packaged into $stage ..."
    try { Copy-Item -LiteralPath $packaged -Destination $stage -Recurse }
    catch {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw "Could not copy the app: $($_.Exception.Message) The installed app was not changed."
    }
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Building from source needs the .NET 9 SDK (https://dotnet.microsoft.com/download). Or install from a release zip.'
    }
    Write-Host "Publishing Release into $stage ..."
    # --disable-build-servers: a build node left running by another process (for example an agent session, whose
    # writes the Claude desktop app redirects) would otherwise do the copy, and the files land somewhere else.
    & dotnet publish $project -c Release -o $stage --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw "dotnet publish failed with exit code $LASTEXITCODE. The installed app was not changed."
    }
}
foreach ($required in @((Join-Path $stage 'TaskbarTiles.exe'),
        (Join-Path $stage 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'),
        (Join-Path $stage 'tiles\media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $stage 'tiles\media\tile.json'),
        (Join-Path $stage 'tiles\uptime\tile.json'),
        (Join-Path $stage 'tiles\docker\tile.json'),
        (Join-Path $stage 'tiles\git\tile.json'),
        (Join-Path $stage 'tiles\todo\tile.json'),
        (Join-Path $stage 'tiles\battery\TaskbarTiles.Plugins.Battery.dll'),
        (Join-Path $stage 'tiles\battery\tile.json'),
        (Join-Path $stage 'tiles\clipboard\TaskbarTiles.Plugins.Clipboard.dll'),
        (Join-Path $stage 'tiles\system\TaskbarTiles.Plugins.SystemMonitor.dll'),
        (Join-Path $stage 'tiles\aquarium\TaskbarTiles.Plugins.Scenes.dll'),
        (Join-Path $stage 'tiles\snake\TaskbarTiles.Plugins.Scenes.dll'),
        (Join-Path $stage 'tiles\racers\TaskbarTiles.Plugins.Scenes.dll'),
        (Join-Path $stage 'tiles\cozy-farm\TaskbarTiles.Plugins.Scenes.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw "The new build is missing $required. The installed app was not changed."
    }
}
Write-Host 'Verified the new build.'

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
foreach ($kind in $retiredKinds) {
    $target = Join-Path $TilesDir $kind
    if (-not (Test-Path -LiteralPath $target)) { continue }
    try { Remove-Item -LiteralPath $target -Recurse -Force; Write-Host "Removed retired tile kind $kind from $TilesDir" }
    catch { Write-Warning "Could not remove the retired tile kind $target : $($_.Exception.Message)" }
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
