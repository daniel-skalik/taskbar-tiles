<#
.SYNOPSIS
    Draws docs\images\promo.png, the wide picture at the top of the README, from the tile pictures in docs\images.

.DESCRIPTION
    Run tools\screenshots first (dotnet run --project tools/screenshots) so the tile pictures are current, then:
    powershell -ExecutionPolicy Bypass -File tools\promo.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$images = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\images'

$rows = @(
    @('tile-media', 'tile-google-calendar', 'tile-github', 'tile-system'),
    @('tile-claude-usage', 'tile-cozy-farm', 'tile-battery', 'tile-linear', 'tile-uptime')
)
$margin = 64; $gap = 16; $barPad = 12; $rowGap = 28; $headerHeight = 170
$popup = [System.Drawing.Image]::FromFile((Join-Path $images 'popup.png'))

$loaded = foreach ($row in $rows) { , @($row | ForEach-Object { [System.Drawing.Image]::FromFile((Join-Path $images "$_.png")) }) }
$rowWidths = foreach ($row in $loaded) { ($row | Measure-Object -Property Width -Sum).Sum + $gap * ($row.Count - 1) + 2 * $barPad }
$barsWidth = ($rowWidths | Measure-Object -Maximum).Maximum
$tileHeight = $loaded[0][0].Height
$barsHeight = $rows.Count * ($tileHeight + 2 * $barPad) + ($rows.Count - 1) * $rowGap
$width = [int]($margin + $barsWidth + 48 + $popup.Width + $margin)
$height = [int]($headerHeight + [Math]::Max($barsHeight, $popup.Height) + $margin)

$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'

function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); $p
}

$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $width, $height),
    ([System.Drawing.Color]::FromArgb(255, 24, 30, 48)), ([System.Drawing.Color]::FromArgb(255, 40, 26, 44))
$g.FillPath($bg, (RoundRect 0 0 $width $height 32))

$titleFont = New-Object System.Drawing.Font 'Segoe UI Semibold', 44, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$tagFont = New-Object System.Drawing.Font 'Segoe UI', 26, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Taskbar Tiles', $titleFont, [System.Drawing.Brushes]::White, $margin - 4, 48)
$g.DrawString('Live widgets for the Windows 11 taskbar. Free and open source.', $tagFont,
    (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 190, 196, 210))), $margin, 108)

$bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 28, 28, 28))
$y = $headerHeight
for ($i = 0; $i -lt $loaded.Count; $i++) {
    $g.FillPath($bar, (RoundRect $margin $y $rowWidths[$i] ($tileHeight + 2 * $barPad) 14))
    $x = $margin + $barPad
    foreach ($tile in $loaded[$i]) { $g.DrawImage($tile, $x, $y + $barPad, $tile.Width, $tile.Height); $x += $tile.Width + $gap }
    $y += $tileHeight + 2 * $barPad + $rowGap
}
$g.DrawImage($popup, $margin + $barsWidth + 48, $headerHeight, $popup.Width, $popup.Height)

$g.Dispose()
$out = Join-Path $images 'promo.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
$popup.Dispose(); foreach ($row in $loaded) { foreach ($tile in $row) { $tile.Dispose() } }
Write-Host "Promo: $out ($width x $height)"
