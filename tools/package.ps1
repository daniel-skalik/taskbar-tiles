<#
.SYNOPSIS
    Builds the release zip: a self-contained Taskbar Tiles (no .NET install needed) with install.cmd and uninstall.cmd.

.DESCRIPTION
    Publishes src\TaskbarTiles.Host for win-x64 with the .NET runtime included, lays it out as

        TaskbarTiles-<version>-win-x64\
            install.cmd, uninstall.cmd, README.md
            tools\install.ps1, tools\uninstall.ps1
            app\            the published app (TaskbarTiles.exe, plugins\, tiles\)

    and zips it. install.cmd in the zip copies app\ into place instead of building (see install.ps1).
    The GitHub release workflow (.github\workflows\release.yml) runs this script on a v* tag.

.PARAMETER Version
    Version stamped into the app and the zip name, such as 1.2.0 (a leading "v" is dropped). Default: 0.0.0-dev.

.PARAMETER OutDir
    Where the folder and the zip are written. Default: artifacts\package in the repository.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-dev',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\TaskbarTiles.Host\TaskbarTiles.Host.csproj'
$Version = $Version.TrimStart('v', 'V')
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\package' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$name = "TaskbarTiles-$Version-win-x64"
$root = Join-Path $OutDir $name
$zip = "$root.zip"

foreach ($path in @($root, $zip)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'tools') | Out-Null

Write-Host "Publishing $Version (win-x64, self-contained) ..."
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version -o (Join-Path $root 'app') --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $repo 'install.cmd'), (Join-Path $repo 'uninstall.cmd'), (Join-Path $repo 'README.md') -Destination $root
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $root 'tools')

Compress-Archive -Path $root -DestinationPath $zip
Write-Host "Package: $zip"
