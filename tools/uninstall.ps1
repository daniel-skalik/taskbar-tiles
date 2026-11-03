<#
.SYNOPSIS
    Removes Taskbar Tiles for the current user. uninstall.cmd next to this tools folder runs this script.

.DESCRIPTION
    1. Stops TaskbarTiles.exe if it runs from the install folder.
    2. Removes the Startup shortcut, if it starts this install.
    3. Removes the install folder.
    4. With -RemoveSettings, also removes config.json, logs and every tile kind (yours included): the whole
       %LOCALAPPDATA%\TaskbarTiles folder. Without it they are kept, and a later install picks them up again.

.PARAMETER AppDir
    Install folder. Default: %LOCALAPPDATA%\TaskbarTiles\app.

.PARAMETER RemoveSettings
    Also remove the data folder DataDir (config, logs, tiles).

.PARAMETER DataDir
    Data folder removed by -RemoveSettings. Default: %LOCALAPPDATA%\TaskbarTiles, where the app keeps them.

.EXAMPLE
    uninstall.cmd

.EXAMPLE
    uninstall.cmd -RemoveSettings
#>
[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles\app'),
    [switch]$RemoveSettings,
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'TaskbarTiles')
)

$ErrorActionPreference = 'Stop'
$AppDir = [System.IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$DataDir = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')
if ($RemoveSettings -and -not (Test-Path -LiteralPath (Join-Path $DataDir 'config.json')) -and (Test-Path -LiteralPath $DataDir)) {
    throw "$DataDir has no config.json, so it may not be Taskbar Tiles' data folder. Nothing was removed."
}

# 1. Stop the installed copy. Copies running from elsewhere (a development build) are left alone.
foreach ($p in @(Get-Process -Name TaskbarTiles -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase) })) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (-not $p.WaitForExit(10000)) { throw "TaskbarTiles (process $($p.Id)) did not exit within 10 seconds. Nothing was removed." }
    Write-Host "Stopped TaskbarTiles (process $($p.Id))."
}

# 2. Startup shortcut.
$link = Join-Path ([Environment]::GetFolderPath('Startup')) 'TaskbarTiles.lnk'
if (Test-Path -LiteralPath $link) {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath
    if ($target.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $link -Force; Write-Host "Removed $link" }
    else { Write-Host "Kept $link : it starts $target, not this install." }
}

# 3. The app, and leftovers of an interrupted install.
foreach ($dir in @($AppDir, "$AppDir.new", "$AppDir.old")) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force; Write-Host "Removed $dir" }
}

# 4. Settings, logs and tile kinds.
if ($RemoveSettings) {
    if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force; Write-Host "Removed $DataDir" }
} elseif (Test-Path -LiteralPath $DataDir) {
    Write-Host "Kept your config, logs and tiles in $DataDir (uninstall.cmd -RemoveSettings removes them too)."
}
Write-Host 'Taskbar Tiles is uninstalled.'
