# Taskbar Tiles bundled kind "todo": tasks from a Markdown file ("- [ ] task" / "- [x] task" lines), ticked off and
# added from the popup. Hooks (tile.json): interval, flyout, action:add, action:toggle, action:clear, action:open.
# See docs/script-connectors.md. Replaced on every install: copy this folder under a new name before changing it.
# Settings: "file" (the Markdown file; default Documents\todo.md), "showDone" (list done tasks in the popup).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$settings = if ($env:TILE_SETTINGS_JSON) { $env:TILE_SETTINGS_JSON | ConvertFrom-Json } else { $null }
$file = if ($settings -and $settings.file) { [Environment]::ExpandEnvironmentVariables([string]$settings.file).Trim().Trim('"') } else { '' }
if (-not $file) { $file = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'todo.md' }
$file = [IO.Path]::GetFullPath($file)
$showDone = -not ($settings -and $settings.showDone -eq $false)
$taskPattern = '^(\s*[-*] \[)( |x|X)(\] )(.+)$'
$maxDone = 10
$utf8 = New-Object System.Text.UTF8Encoding $false

# The file as lines, plus what writing it back must keep: BOM, line ending, newline after the last line.
function Read-Todo {
    $doc = @{ Exists = $false; Lines = New-Object System.Collections.Generic.List[string]; Bom = $false; Eol = "`r`n"; FinalNewline = $true }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $doc }
    try { $bytes = [IO.File]::ReadAllBytes($file) } catch { [Console]::Error.WriteLine("Cannot read ${file}: $($_.Exception.Message)"); exit 1 }
    $doc.Exists = $true
    $doc.Bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($doc.Bom) { 3 } else { 0 }
    $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $first = $text.IndexOf("`n")
    if ($first -ge 0) { $doc.Eol = if ($first -gt 0 -and $text[$first - 1] -eq "`r") { "`r`n" } else { "`n" } }
    $doc.FinalNewline = $text.Length -eq 0 -or $text.EndsWith("`n")
    if ($text.EndsWith("`r`n")) { $text = $text.Substring(0, $text.Length - 2) } elseif ($text.EndsWith("`n")) { $text = $text.Substring(0, $text.Length - 1) }
    if ($text.Length -gt 0) { foreach ($line in ($text -split "`r?`n")) { $doc.Lines.Add($line) } }
    return $doc
}

# Writes to a temporary file next to the original, then swaps it in: a crash never leaves half a file.
function Write-Todo($doc) {
    $text = [string]::Join($doc.Eol, $doc.Lines)
    if ($doc.FinalNewline -and $doc.Lines.Count -gt 0) { $text += $doc.Eol }
    $body = $utf8.GetBytes($text)
    # Plain assignment, not an if-expression: an empty array would unroll to $null there.
    $bytes = $body
    if ($doc.Bom) { $bytes = [byte[]](0xEF, 0xBB, 0xBF) + $body }
    $folder = Split-Path -Parent $file
    $temp = Join-Path $folder (".todo-" + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        if (-not (Test-Path -LiteralPath $folder)) { $null = New-Item -ItemType Directory -Path $folder -Force }
        [IO.File]::WriteAllBytes($temp, [byte[]]$bytes)
        if (Test-Path -LiteralPath $file) { [IO.File]::Replace($temp, $file, [NullString]::Value) } else { [IO.File]::Move($temp, $file) }
    } catch {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        [Console]::Error.WriteLine("Cannot save ${file}: $($_.Exception.Message)"); exit 1
    }
}

# The first 8 hex digits of the SHA-1 of a task's text: names a task in an action id even after lines move.
function Get-Hash([string]$text) {
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return -join ($sha.ComputeHash($utf8.GetBytes($text))[0..3] | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

# Every task line: its 1-based line number, done or not, and its text after the checkbox.
function Get-Tasks($doc) {
    $tasks = @()
    for ($i = 0; $i -lt $doc.Lines.Count; $i++) {
        if ($doc.Lines[$i] -match $taskPattern) { $tasks += @{ Line = $i + 1; Done = $Matches[2] -ne ' '; Text = $Matches[4] } }
    }
    return ,$tasks
}

$doc = Read-Todo

if ($env:TILE_HOOK -eq 'action') {
    $action = [string]$env:TILE_ACTION
    if ($action -eq 'add') {
        $text = (([string]$env:TILE_INPUT) -replace '[\r\n]+', ' ').Trim()
        if (-not $text) { exit 0 }
        $tasks = Get-Tasks $doc
        $at = if ($tasks.Count) { $tasks[-1].Line } else { $doc.Lines.Count } # after the last task, else at the end
        $doc.Lines.Insert($at, "- [ ] $text")
        Write-Todo $doc
    } elseif ($action -match '^toggle:(\d+):([0-9a-f]{8})$') {
        $line = [int]$Matches[1]; $hash = $Matches[2]
        $tasks = Get-Tasks $doc
        $task = $tasks | Where-Object { $_.Line -eq $line -and (Get-Hash $_.Text) -eq $hash } | Select-Object -First 1
        if (-not $task) { $task = $tasks | Where-Object { (Get-Hash $_.Text) -eq $hash } | Select-Object -First 1 } # the file changed since the popup opened
        if (-not $task) { exit 0 }
        $null = $doc.Lines[$task.Line - 1] -match $taskPattern
        $mark = if ($task.Done) { ' ' } else { 'x' }
        $doc.Lines[$task.Line - 1] = $Matches[1] + $mark + $Matches[3] + $Matches[4]
        Write-Todo $doc
    } elseif ($action -eq 'clear') {
        $kept = New-Object System.Collections.Generic.List[string]
        $removed = 0
        foreach ($l in $doc.Lines) { if ($l -match $taskPattern -and $Matches[2] -ne ' ') { $removed++ } else { $kept.Add($l) } }
        if ($removed -eq 0) { exit 0 }
        $doc.Lines = $kept
        Write-Todo $doc
    } elseif ($action -eq 'open') {
        if (-not $doc.Exists) { Write-Todo $doc }
        Start-Process -FilePath $file
    }
    exit 0
}

$tasks = Get-Tasks $doc
$open = @($tasks | Where-Object { -not $_.Done })
$done = @($tasks | Where-Object { $_.Done })

if ($env:TILE_HOOK -eq 'flyout') {
    $rows = @()
    foreach ($t in $open) { $rows += @{ text = $t.Text; check = $false; action = "toggle:$($t.Line):$(Get-Hash $t.Text)" } }
    if ($showDone) { foreach ($t in ($done | Select-Object -First $maxDone)) { $rows += @{ text = $t.Text; check = $true; action = "toggle:$($t.Line):$(Get-Hash $t.Text)" } } }
    if (-not $tasks.Count) { $rows += @{ text = 'No tasks yet' } }
    elseif (-not $rows.Count) { $rows += @{ text = 'Nothing open' } } # all done and the done ones hidden
    $actions = @()
    if ($done.Count) { $actions += @{ id = 'clear'; label = 'Clear done' } }
    $actions += @{ id = 'open'; label = 'Open file'; primary = $true }
    @{
        title    = 'To do'
        subtitle = (Split-Path -Leaf $file)
        stayOpen = $true
        input    = @{ placeholder = 'Add a task'; action = 'add' }
        rows     = $rows
        actions  = $actions
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# interval prints the tile.
$subtitle = if ($open.Count) { $open[0].Text } elseif ($doc.Exists -and $tasks.Count) { 'Nothing open' } else { 'Add a task' }
$tip = @($file) + @($open | Select-Object -First 5 | ForEach-Object { "- $($_.Text)" })
@{
    title    = $(if ($open.Count) { "$($open.Count) to do" } else { 'All done' })
    subtitle = $subtitle
    icon     = @{ glyph = 'check'; color = $(if ($open.Count) { 'accent' } else { 'ok' }) }
    tooltip  = $tip -join "`n"
} | ConvertTo-Json -Depth 5 -Compress
