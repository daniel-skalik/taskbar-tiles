[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
# Taskbar Tiles bundled kind "linear": your Linear issues in In Progress and Todo. Hooks (tile.json): interval, flyout,
# action:open (ids "open:<url>"), action:setkey, action:forget. See docs/script-connectors.md.
# Replaced on every install: copy this folder under a new name before changing it.
# The API key lives in Windows Credential Manager (generic credential "TaskbarTiles-Linear"). It is sent only to
# api.linear.app, and never printed, logged, cached or put in an error message.
# Testing only: TILE_LINEAR_RESPONSE names a file read instead of calling the API (no cache then); only together with
# it, TILE_LINEAR_KEYFILE names a file used instead of Credential Manager (so a stray variable cannot put the key in a file).

$target = 'TaskbarTiles-Linear'
$keyFile = if ($env:TILE_LINEAR_RESPONSE -and $env:TILE_LINEAR_KEYFILE) { $env:TILE_LINEAR_KEYFILE } else { $null }
$api = 'https://api.linear.app/graphql'
$keyPage = 'https://linear.app/settings/account/security'
$dot = [char]0x00B7
$cache = Join-Path ([IO.Path]::GetTempPath()) "TaskbarTiles-linear-$env:TILE_ID.json"
# A setkey failure message waiting for the next popup; never the key.
$note = if ($keyFile) { "$keyFile.note" } else { Join-Path ([IO.Path]::GetTempPath()) "TaskbarTiles-linear-$env:TILE_ID.note" }
$query = 'query { organization { urlKey } viewer { assignedIssues(first: 50, filter: { state: { type: { in: ["started", "unstarted"] } } }) { nodes { identifier title url priority updatedAt state { name type } } } } }'

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }

# Credential Manager through advapi32; compiled only when the real store is used.
function Use-CredentialManager {
    if ('TaskbarTiles.LinearCred' -as [type]) { return }
    Add-Type -Namespace TaskbarTiles -Name LinearCred -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
    public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
}
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL credential, int flags);
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, int type, int flags);
[DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);
public static string Read(string target) {
    IntPtr p;
    if (!CredReadW(target, 1, 0, out p)) return null;
    try {
        var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
        return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
    } finally { CredFree(p); }
}
public static void Write(string target, string secret) {
    var blob = Marshal.StringToCoTaskMemUni(secret);
    try {
        var c = new CREDENTIAL { Type = 1, TargetName = target, UserName = "api", CredentialBlob = blob, CredentialBlobSize = secret.Length * 2, Persist = 2 };
        if (!CredWriteW(ref c, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    } finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
}
public static void Delete(string target) { CredDeleteW(target, 1, 0); }
'@
}

function Get-Key {
    if ($keyFile) {
        if (Test-Path -LiteralPath $keyFile) { return (Get-Content -LiteralPath $keyFile -Raw).Trim() }
        return $null
    }
    Use-CredentialManager
    [TaskbarTiles.LinearCred]::Read($target)
}

function Set-Key([string]$key) {
    if ($keyFile) { Set-Content -LiteralPath $keyFile -Value $key -NoNewline -Encoding ASCII; return }
    Use-CredentialManager
    [TaskbarTiles.LinearCred]::Write($target, $key)
}

function Remove-Key {
    if ($keyFile) { Remove-Item -LiteralPath $keyFile -ErrorAction SilentlyContinue; return }
    Use-CredentialManager
    [TaskbarTiles.LinearCred]::Delete($target)
}

# One request. Returns @{ Data } or @{ Error; Auth }; Auth is true when Linear refused the key.
function Invoke-Linear([string]$key) {
    $r = $null; $status = 0
    try {
        if ($env:TILE_LINEAR_RESPONSE) { $r = Get-Content -LiteralPath $env:TILE_LINEAR_RESPONSE -Raw | ConvertFrom-Json }
        else {
            $body = @{ query = $query } | ConvertTo-Json -Compress
            $r = Invoke-RestMethod -Method Post -Uri $api -Headers @{ Authorization = $key } -ContentType 'application/json' -Body $body -TimeoutSec 8
        }
    } catch {
        $failure = $_
        try { $status = [int]$failure.Exception.Response.StatusCode } catch { }
        try { $r = $failure.ErrorDetails.Message | ConvertFrom-Json } catch { $r = $null }
        if (-not $r -or -not $r.errors) {
            if ($status -eq 401) { return @{ Auth = $true; Error = 'Key rejected by Linear' } }
            $msg = "$($failure.Exception.Message)"
            if ($key) { $msg = $msg.Replace($key, '<key>') }
            return @{ Auth = $false; Error = "Could not reach Linear: $msg" }
        }
    }
    if ($r.errors) {
        $auth = $status -eq 401 -or @($r.errors | Where-Object { $_.extensions.code -eq 'AUTHENTICATION_ERROR' }).Count -gt 0
        if ($auth) { return @{ Auth = $true; Error = 'Key rejected by Linear' } }
        return @{ Auth = $false; Error = "Linear: $(@($r.errors)[0].message)" }
    }
    @{ Data = $r.data }
}

function Read-Cache {
    if ($env:TILE_LINEAR_RESPONSE -or -not (Test-Path -LiteralPath $cache)) { return $null }
    try { return Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json } catch { return $null }
}

function Write-Cache($data) {
    if ($env:TILE_LINEAR_RESPONSE) { return }
    try { @{ fetched_at = [DateTimeOffset]::UtcNow.ToString('o'); data = $data } | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $cache -Encoding UTF8 } catch { }
}

# In Progress before Todo, then Urgent (1) to Low (4) with No priority (0) last, then most recently updated first.
function Sort-Issues($nodes) {
    $order = @(
        @{ Expression = { if ($_.state.type -eq 'started') { 0 } else { 1 } } },
        @{ Expression = { if ([int]$_.priority -ge 1) { [int]$_.priority } else { 5 } } },
        @{ Expression = { [DateTimeOffset]$_.updatedAt }; Descending = $true })
    @($nodes | Where-Object { $_ } | Sort-Object -Property $order)
}

# The popup that takes a key. A message left by a failed setkey replaces the subtitle once.
function Write-Setup([string]$subtitle) {
    if (Test-Path -LiteralPath $note) {
        $waiting = (Get-Content -LiteralPath $note -Raw).Trim()
        Remove-Item -LiteralPath $note -ErrorAction SilentlyContinue
        if ($waiting) { $subtitle = $waiting }
    }
    $sep = [char]0x203A
    @{
        title    = 'Linear'
        subtitle = $subtitle
        stayOpen = $true
        input    = @{ placeholder = 'Personal API key'; action = 'setkey'; secret = $true }
        rows     = @("Linear $sep Settings $sep Security & access")
        actions  = @(@{ id = "open:$keyPage"; label = 'Get a key'; primary = $true })
    } | ConvertTo-Json -Depth 5 -Compress
}

function Write-SetupTile([string]$subtitle) {
    @{ title = 'Linear'; subtitle = $subtitle; icon = @{ glyph = 'activity'; color = 'neutral' } } | ConvertTo-Json -Depth 5 -Compress
}

$hook = $env:TILE_HOOK
$action = "$env:TILE_ACTION"

if ($hook -eq 'action') {
    if ($action.StartsWith('open:')) {
        $url = $action.Substring(5)
        if ($url -notmatch '^https://linear\.app/') { Fail 'Not a Linear link.' }
        Start-Process $url
        exit 0
    }
    if ($action -eq 'setkey') {
        $key = "$env:TILE_INPUT".Trim()
        if (-not $key) { exit 0 }
        if ($key -cnotmatch '^[!-~]+$') { Set-Content -LiteralPath $note -Value 'That does not look like a Linear API key' -Encoding UTF8; exit 0 }
        $result = Invoke-Linear $key
        if ($result.Error) { Set-Content -LiteralPath $note -Value $result.Error -Encoding UTF8; exit 0 }
        Set-Key $key
        Write-Cache $result.Data
        Remove-Item -LiteralPath $note -ErrorAction SilentlyContinue
        exit 0
    }
    if ($action -eq 'forget') {
        Remove-Key
        Remove-Item -LiteralPath $cache, $note -ErrorAction SilentlyContinue
        exit 0
    }
    Fail "Unknown action '$action'."
}

$key = Get-Key
if (-not $key) {
    if ($hook -eq 'flyout') { Write-Setup 'Paste your personal API key' } else { Write-SetupTile 'Click to set up' }
    exit 0
}

# The popup's cache age limit: the refresh setting ("2m", "30s", "1h"), at least 2 minutes.
function Get-CacheMinutes {
    $minutes = 2.0
    try {
        $refresh = [string](($env:TILE_SETTINGS_JSON | ConvertFrom-Json).refresh)
        if ($refresh -match '^\s*(\d+(?:\.\d+)?)\s*([smh])\s*$') {
            $n = [double]$Matches[1]
            $asMinutes = switch ($Matches[2]) { 's' { $n / 60 } 'm' { $n } 'h' { $n * 60 } }
            if ($asMinutes -gt $minutes) { $minutes = $asMinutes }
        }
    } catch { }
    $minutes
}

# interval always fetches; the popup takes the cache while it is younger than that.
$cached = Read-Cache
$data = $null; $stale = $null
if ($hook -eq 'flyout' -and $cached -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$cached.fetched_at).TotalMinutes -lt (Get-CacheMinutes)) {
    $data = $cached.data
} else {
    $result = Invoke-Linear $key
    if ($result.Auth) {
        if ($hook -eq 'flyout') { Write-Setup 'Linear rejected the saved key' } else { Write-SetupTile 'Key rejected' }
        exit 0
    }
    if ($result.Error) {
        if (-not $cached) { Fail $result.Error }
        $data = $cached.data; $stale = $result.Error
    } else {
        $data = $result.Data
        Write-Cache $data
    }
}

$issues = @(Sort-Issues $data.viewer.assignedIssues.nodes)
$doing = @($issues | Where-Object { $_.state.type -eq 'started' }).Count
$todo = $issues.Count - $doing
$summary = "$doing in progress $dot $todo to do"

if ($hook -eq 'flyout') {
    $rows = @()
    foreach ($i in @($issues | Select-Object -First 15)) {
        $rows += @{
            text   = "$($i.identifier) $($i.title)"
            value  = $i.state.name
            icon   = @{ glyph = 'info'; color = $(if ($i.state.type -eq 'started') { 'accent' } else { 'neutral' }) }
            action = "open:$($i.url)"
        }
    }
    if (-not $rows.Count) { $rows = @('Nothing in In Progress or Todo') }
    @{
        title    = 'Linear'
        subtitle = $summary
        rows     = $rows
        actions  = @(
            @{ id = 'forget'; label = 'Forget key' },
            @{ id = "open:https://linear.app/$($data.organization.urlKey)/my-issues/assigned"; label = 'Open Linear'; primary = $true })
    } | ConvertTo-Json -Depth 6 -Compress
    exit 0
}

# interval prints the tile.
$tip = @($summary) + @($issues | Select-Object -First 5 | ForEach-Object { "$($_.identifier) $($_.title)" })
if ($stale) { $tip += "Stale: $stale" }
@{
    title    = $(if ($doing) { "$doing in progress" } elseif ($todo) { "$todo to do" } else { 'All clear' })
    subtitle = $(if ($issues.Count) { "$($issues[0].identifier) $($issues[0].title)" } else { 'Nothing assigned' })
    icon     = @{ glyph = 'activity'; color = $(if ($issues.Count) { 'accent' } else { 'ok' }) }
    tooltip  = $tip -join "`n"
} | ConvertTo-Json -Depth 5 -Compress
