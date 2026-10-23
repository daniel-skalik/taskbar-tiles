<#
.SYNOPSIS
    Builds Taskbar Tiles and installs it for the current user.

.DESCRIPTION
    Run it from your own terminal (not from an agent session: the Claude desktop app redirects an agent's writes
    under %LOCALAPPDATA% to a private copy). Safe to run again; each run replaces the installed app.

    1. Stops TaskbarTiles.exe if it runs from the install folder.
    2. Publishes src\TaskbarTiles.Host (Release) into the install folder.
    3. Creates or updates the TaskbarTiles shortcut in the Startup folder.
    4. Starts the installed app.

    Your config.json, scripts, plugins and logs in %LOCALAPPDATA%\TaskbarTiles are never touched.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.

.PARAMETER NoShortcut
    Do not create or update the Startup shortcut.

.PARAMETER NoStart
    Do not start the app afterwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install.ps1
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\TaskbarTiles.Host\TaskbarTiles.Host.csproj'
$AppDir = [System.IO.Path]::GetFullPath($AppDir)
$exe = Join-Path $AppDir 'TaskbarTiles.exe'

if (-not (Test-Path $project)) { throw "Cannot find $project. Run this script from the repository's tools folder." }

# 1. Stop the installed copy so its files can be replaced. Copies running from elsewhere are left alone.
$running = @(Get-Process -Name TaskbarTiles -ErrorAction SilentlyContinue)
$installed = @($running | Where-Object { $_.Path -and $_.Path.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase) })
foreach ($p in $installed) {
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(10000) | Out-Null
    Write-Host "Stopped TaskbarTiles (process $($p.Id))."
}
foreach ($p in @($running | Where-Object { $installed -notcontains $_ })) {
    Write-Warning "TaskbarTiles is also running from $($p.Path) (process $($p.Id)); it keeps running and the installed copy will exit at start (one instance at a time)."
}
if ($installed.Count -eq 0) { Write-Host 'TaskbarTiles was not running from the install folder.' }

# 2. Publish.
Write-Host "Publishing Release into $AppDir ..."
& dotnet publish $project -c Release -o $AppDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
foreach ($required in @($exe,
        (Join-Path $AppDir 'plugins\TaskbarTiles.Plugins.Media\TaskbarTiles.Plugins.Media.dll'),
        (Join-Path $AppDir 'plugins\TaskbarTiles.Plugins.Script\TaskbarTiles.Plugins.Script.dll'))) {
    if (-not (Test-Path $required)) { throw "Publish output is missing $required." }
}
Write-Host "Published $exe"

# 3. Startup shortcut (shows in Task Manager > Startup apps).
if ($NoShortcut) {
    Write-Host 'Skipped the Startup shortcut (-NoShortcut).'
} else {
    $link = Join-Path ([Environment]::GetFolderPath('Startup')) 'TaskbarTiles.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $AppDir
    $shortcut.Description = 'Taskbar Tiles'
    $shortcut.Save()
    Write-Host "Startup shortcut: $link -> $exe"
}

# 4. Start.
if ($NoStart) {
    Write-Host 'Not started (-NoStart).'
} else {
    Start-Process -FilePath $exe -WorkingDirectory $AppDir
    Write-Host 'Started TaskbarTiles.'
}
