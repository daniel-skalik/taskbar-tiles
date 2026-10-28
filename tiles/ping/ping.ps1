# Taskbar Tiles bundled kind "ping": ping a host. Hooks (tile.json): interval, flyout, action:ping. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# Settings: "host" (default 1.1.1.1).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$target = if ($settings -and $settings.host) { [string]$settings.host } else { '1.1.1.1' }

function Measure-Ping([string]$name, [int]$timeoutMs = 2000) {
    try {
        $reply = (New-Object System.Net.NetworkInformation.Ping).Send($name, $timeoutMs)
        if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) { return [int]$reply.RoundtripTime }
    } catch { }
    return $null
}

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    foreach ($i in 1..4) {
        $ms = Measure-Ping $target 700 # 4 pings must stay well under the host's 10 s popup limit
        if ($null -eq $ms) {
            $rows += @{ text = "Ping $i"; value = 'timeout'; icon = @{ glyph = 'cancel'; color = 'bad' } }
        } else {
            $rows += @{ text = "Ping $i"; value = "$ms ms"; icon = @{ glyph = 'check'; color = 'ok' } }
        }
    }
    @{
        title    = $target
        subtitle = 'Last 4 pings'
        rows     = $rows
        actions  = @(@{ id = 'ping'; label = 'Ping again'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval, and action:ping (the button and the popup action) print the tile.
$ms = Measure-Ping $target
if ($null -eq $ms) {
    $state = @{ title = $target; subtitle = 'unreachable'; icon = @{ glyph = 'cancel'; color = 'bad' }; urgent = $true }
} else {
    $color = if ($ms -lt 50) { 'ok' } elseif ($ms -lt 150) { 'warn' } else { 'bad' }
    $state = @{ title = $target; subtitle = "$ms ms"; icon = @{ glyph = 'check'; color = $color } }
}
$state.buttons = @(@{ id = 'ping'; glyph = 'refresh'; tooltip = 'Ping now' })
$state.tooltip = "Pings $target. Click for details."
$state | ConvertTo-Json -Depth 5 -Compress
