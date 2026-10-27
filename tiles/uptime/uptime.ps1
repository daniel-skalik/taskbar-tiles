# Taskbar Tiles bundled kind "uptime": clock and uptime. Hooks (tile.json): interval, flyout. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$now = Get-Date
$boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime
$up = $now - $boot
$upText = if ($up.Days -gt 0) { '{0}d {1}h' -f $up.Days, $up.Hours } else { '{0}h {1}m' -f $up.Hours, $up.Minutes }

if ($env:TILE_HOOK -eq 'flyout') {
    @{
        title    = $now.ToString('dddd d MMMM yyyy')
        subtitle = "Up $upText"
        rows     = @(
            @{ text = 'Time'; value = $now.ToString('HH\:mm\:ss') },
            @{ text = 'Booted'; value = $boot.ToString('yyyy-MM-dd HH\:mm') },
            @{ text = 'Uptime'; value = ('{0}d {1:hh\:mm}' -f $up.Days, $up) }
        )
    } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}

@{
    title    = $now.ToString('HH\:mm')
    subtitle = "up $upText"
    icon     = @{ glyph = 'clock'; color = 'accent' }
    tooltip  = "Booted $($boot.ToString('g'))"
} | ConvertTo-Json -Depth 4 -Compress
