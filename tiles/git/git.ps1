# Taskbar Tiles bundled kind "git": one repository's branch, local changes and ahead/behind counts, from git status.
# Hooks (tile.json): interval, flyout, action:fetch, action:folder. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# Settings: "repo" (the repository folder), "autoFetch" (fetch every 5 minutes), "git" (path to git.exe; default: on PATH).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$dot = [char]0x00B7; $up = [char]0x2191; $down = [char]0x2193 # the file stays ASCII for Windows PowerShell 5.1
$repo = if ($settings -and $settings.repo) { [Environment]::ExpandEnvironmentVariables([string]$settings.repo).Trim().Trim('"') } else { '' }

# No repository yet is a normal state: the tile asks for one.
if (-not $repo) {
    if ($env:TILE_HOOK -eq 'flyout') {
        @{ title = 'Git repository'; subtitle = 'No repository set'; rows = @('Right-click the tile > Settings... and enter a repository folder.') } | ConvertTo-Json -Depth 5 -Compress
    } elseif ($env:TILE_HOOK -ne 'action') {
        @{ title = 'Git'; subtitle = 'Set a repository in Settings'; icon = @{ glyph = 'branch'; color = 'neutral' } } | ConvertTo-Json -Depth 5 -Compress
    }
    exit 0
}
if (-not (Test-Path -LiteralPath $repo -PathType Container)) { [Console]::Error.WriteLine("The folder $repo does not exist."); exit 1 }
$name = Split-Path -Leaf ($repo.TrimEnd('\', '/'))

if ($env:TILE_HOOK -eq 'action' -and $env:TILE_ACTION -eq 'folder') { Start-Process explorer.exe -ArgumentList "`"$repo`""; exit 0 }

$git = if ($settings -and $settings.git) { [string]$settings.git } else { (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
if (-not $git) { [Console]::Error.WriteLine('Git is not installed: git was not found on PATH.'); exit 1 }

# Runs git in the repository; gives up after $seconds. Output in UTF-8, prompts for credentials turned off.
function Invoke-Git([string]$arguments, [int]$seconds = 8) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $git, "-C `"$repo`" -c core.quotepath=false $arguments"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $info.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $info.EnvironmentVariables['GIT_OPTIONAL_LOCKS'] = '0' # status must not take the index lock from the user's own git
    $p = [System.Diagnostics.Process]::Start($info)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) {
        try { $p.Kill() } catch { }
        return @{ Code = -1; Out = ''; Err = "git did not finish within $seconds s" }
    }
    return @{ Code = $p.ExitCode; Out = $out.Result; Err = $err.Result.Trim() }
}

if ($env:TILE_HOOK -eq 'action' -and $env:TILE_ACTION -eq 'fetch') {
    # Prints nothing on success: the host then reads the status again.
    $fetch = Invoke-Git 'fetch --quiet --prune' 9
    if ($fetch.Code -ne 0) { [Console]::Error.WriteLine("Fetch failed: $(($fetch.Err -split "`n")[0])"); exit 1 }
    exit 0
}

$top = Invoke-Git 'rev-parse --show-toplevel'
if ($top.Code -ne 0) { [Console]::Error.WriteLine("$repo is not a Git repository."); exit 1 }
$gitDir = (Invoke-Git 'rev-parse --absolute-git-dir').Out.Trim()

# Fetch now and then when asked to, so "behind" means something. A failed fetch (offline) just waits for the next try.
if ($env:TILE_HOOK -eq 'interval' -and $settings -and $settings.autoFetch -eq $true) {
    $head = Join-Path $gitDir 'FETCH_HEAD'
    if (-not (Test-Path -LiteralPath $head) -or ((Get-Date) - (Get-Item -LiteralPath $head).LastWriteTime).TotalMinutes -ge 5) {
        $null = Invoke-Git 'fetch --quiet --prune' 20
    }
}

$status = Invoke-Git 'status --porcelain=v2 --branch'
if ($status.Code -ne 0) { [Console]::Error.WriteLine("git status failed: $(($status.Err -split "`n")[0])"); exit 1 }
$branch = ''; $oid = ''; $upstream = ''; $ahead = 0; $behind = 0
$staged = 0; $changed = 0; $untracked = 0; $conflicts = 0
foreach ($line in $status.Out -split "`n") {
    if ($line -match '^# branch\.head (.+)$') { $branch = $Matches[1].Trim() }
    elseif ($line -match '^# branch\.oid (.+)$') { $oid = $Matches[1].Trim() }
    elseif ($line -match '^# branch\.upstream (.+)$') { $upstream = $Matches[1].Trim() }
    elseif ($line -match '^# branch\.ab \+(\d+) -(\d+)') { $ahead = [int]$Matches[1]; $behind = [int]$Matches[2] }
    elseif ($line -match '^[12] (.)(.) ') {
        if ($Matches[1] -ne '.') { $staged++ }
        if ($Matches[2] -ne '.') { $changed++ }
    }
    elseif ($line -match '^u ') { $conflicts++ }
    elseif ($line -match '^\? ') { $untracked++ }
}
if ($branch -eq '(detached)') { $branch = if ($oid -match '^[0-9a-f]{7}') { "detached at $($oid.Substring(0, 7))" } else { 'detached' } }
$dirty = $staged + $changed + $untracked + $conflicts

# "3 changed, 1 new" style parts; ahead and behind as arrows.
$parts = @()
if ($conflicts) { $parts += "$conflicts conflicts" }
if ($staged) { $parts += "$staged staged" }
if ($changed) { $parts += "$changed changed" }
if ($untracked) { $parts += "$untracked new" }
$sync = @()
if ($ahead) { $sync += "$up$ahead" }
if ($behind) { $sync += "$down$behind" }
$color = if ($conflicts) { 'bad' } elseif ($behind) { 'warn' } elseif ($dirty -or $ahead) { 'accent' } else { 'ok' }

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    $rows += @{ text = 'Branch'; value = $branch }
    if ($upstream) {
        $where = if ($ahead -or $behind) { "$ahead ahead $dot $behind behind" } else { 'up to date' }
        $rows += @{ text = $upstream; value = $where; icon = @{ glyph = 'info'; color = $(if ($behind) { 'warn' } else { 'ok' }) } }
    } else {
        $rows += @{ text = 'Upstream'; value = 'none' }
    }
    if ($conflicts) { $rows += @{ text = 'Conflicts'; value = "$conflicts"; icon = @{ glyph = 'info'; color = 'bad' } } }
    $rows += @{ text = 'Staged'; value = "$staged" }
    $rows += @{ text = 'Changed'; value = "$changed" }
    $rows += @{ text = 'New files'; value = "$untracked" }
    $last = Invoke-Git 'log -1 --format=%s%x1f%cr'
    if ($last.Code -eq 0 -and $last.Out.Trim()) {
        $subject, $when = $last.Out.Trim() -split [char]0x1F, 2
        if ($subject.Length -gt 40) { $subject = $subject.Substring(0, 39) + [char]0x2026 }
        $rows += @{ text = $subject; value = $when }
    }
    $fetched = Join-Path $gitDir 'FETCH_HEAD'
    $subtitle = if (Test-Path -LiteralPath $fetched) { "Fetched $((Get-Item -LiteralPath $fetched).LastWriteTime.ToString('ddd HH:mm'))" } else { 'Never fetched' }
    @{
        title    = $name
        subtitle = $subtitle
        rows     = $rows
        actions  = @(@{ id = 'fetch'; label = 'Fetch' }, @{ id = 'folder'; label = 'Open folder'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

$subtitle = if ($parts.Count -or $sync.Count) { (@($parts -join ', ') + @($sync -join ' ') | Where-Object { $_ }) -join " $dot " } else { 'Clean' }
$tip = @("$($top.Out.Trim())", "Branch: $branch")
if ($upstream) { $tip += "${upstream}: $ahead ahead, $behind behind" }
if ($dirty) { $tip += "$staged staged, $changed changed, $untracked new" + $(if ($conflicts) { ", $conflicts conflicts" } else { '' }) }
@{
    title    = "$name $dot $branch"
    subtitle = $subtitle
    icon     = @{ glyph = 'branch'; color = $color }
    tooltip  = $tip -join "`n"
} | ConvertTo-Json -Depth 5 -Compress
