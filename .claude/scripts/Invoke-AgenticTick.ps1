# Runs `claude -p "/tick"` against a target repository.
# Designed to be invoked by Windows Task Scheduler. Skips if a previous
# run is still in progress (lock file with live PID). Appends timestamped
# stdout/stderr to a rolling log under %LOCALAPPDATA%\AgenticTeam.
#
# The run is asked for `--output-format json`, which reports what the tick
# actually cost -- total_cost_usd, the token split, and a per-model breakdown,
# subagents included. That is the only exact whole-run figure available, so it is
# appended to ticks.csv as a cross-check against the per-agent attribution that
# Get-AgentTokenStats.ps1 derives from the transcripts.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepoPath,

    [string]$Command = "/tick",

    [string]$LogDir = (Join-Path $env:LOCALAPPDATA "AgenticTeam"),

    # Rotate the log file when it grows beyond this many bytes.
    [int]$MaxLogBytes = 10MB
)

$ErrorActionPreference = "Stop"

# Force UTF-8 across the board so Czech diacritics, emoji and box-drawing
# glyphs from `claude -p` survive the trip into the log file. PowerShell 5.1
# defaults to the system OEM code page (cp852/Windows-1250 on cs-CZ Windows),
# which mangles anything outside ASCII into "���".
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['Add-Content:Encoding'] = 'UTF8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'UTF8'
$PSDefaultParameterValues['Out-File:Encoding']    = 'UTF8'

if (-not (Test-Path -LiteralPath $RepoPath -PathType Container)) {
    throw "RepoPath does not exist: $RepoPath"
}
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path

if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

# Slug the repo path so multiple repos can coexist in the same log dir.
$slug = ($RepoPath -replace '[\\/:]', '_').Trim('_')
$logFile  = Join-Path $LogDir "$slug.log"
$lockFile = Join-Path $LogDir "$slug.lock"

$costFile = Join-Path $LogDir "ticks.csv"
$costHeader = "ts,repo,command,exitCode,isError,numTurns,durationMs,inputTok,outputTok,cacheCreateTok,cacheReadTok,usd,sessionId,models"

function Write-Log([string]$line) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Add-Content -LiteralPath $logFile -Value "[$stamp] $line" -Encoding UTF8
}

function ConvertTo-CsvField($value) {
    if ($null -eq $value) { return "" }
    $s = [string]$value
    if ($s -match '[",\r\n]') { return '"' + ($s -replace '"', '""') + '"' }
    return $s
}

# One CSV is shared by every repo's scheduled task, so the append is serialised
# across processes. Failing to record the cost must never fail the tick itself.
function Add-CostRow([string]$line) {
    $mutex = New-Object System.Threading.Mutex($false, "Global\AgenticTeamTicksCsv")
    $held = $false
    try {
        $held = $mutex.WaitOne(5000)
        if (-not (Test-Path -LiteralPath $costFile)) {
            Set-Content -LiteralPath $costFile -Value $costHeader -Encoding UTF8
        }
        Add-Content -LiteralPath $costFile -Value $line -Encoding UTF8
    }
    catch { Write-Log "WARN: could not record tick cost: $($_.Exception.Message)" }
    finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

# Rotate if oversized.
if ((Test-Path -LiteralPath $logFile) -and ((Get-Item -LiteralPath $logFile).Length -gt $MaxLogBytes)) {
    $rotated = "$logFile.1"
    if (Test-Path -LiteralPath $rotated) { Remove-Item -LiteralPath $rotated -Force }
    Move-Item -LiteralPath $logFile -Destination $rotated -Force
}

# Lock check: if file exists and PID is alive, skip.
if (Test-Path -LiteralPath $lockFile) {
    $oldPid = Get-Content -LiteralPath $lockFile -ErrorAction SilentlyContinue | Select-Object -First 1
    $alive = $false
    if ($oldPid -match '^\d+$') {
        $alive = [bool](Get-Process -Id ([int]$oldPid) -ErrorAction SilentlyContinue)
    }
    if ($alive) {
        Write-Log "SKIP: previous run still active (pid=$oldPid)"
        exit 0
    }
    # Stale lock — overwrite.
    Write-Log "stale lock removed (pid=$oldPid not running)"
}

Set-Content -LiteralPath $lockFile -Value $PID -Force

try {
    Write-Log "START repo=$RepoPath cmd=$Command"

    $claude = (Get-Command claude -ErrorAction SilentlyContinue)
    if (-not $claude) {
        Write-Log "ERROR: 'claude' CLI not found on PATH"
        exit 2
    }

    Push-Location -LiteralPath $RepoPath
    $prevEap = $ErrorActionPreference
    try {
        # PowerShell 5.1 wraps every stderr line of a native command in an
        # ErrorRecord, which under ErrorActionPreference=Stop aborts the run --
        # so a harmless warning from the CLI would look like a crashed tick.
        # Errors are demoted for the call and each record flattened back to text.
        $ErrorActionPreference = 'Continue'

        # `claude -p` is the headless / print mode. Pipe empty stdin so the CLI
        # never blocks waiting for input under Task Scheduler. The JSON result
        # arrives as one object on stdout, possibly preceded by stderr
        # diagnostics, so the payload is located rather than assumed.
        $raw = ('' | & $claude.Source -p $Command --output-format json 2>&1 |
                ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { $_ } } |
                Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
        Pop-Location
    }

    $result = $null
    $jsonStart = $raw.IndexOf('{')
    if ($jsonStart -ge 0) {
        try { $result = $raw.Substring($jsonStart).Trim() | ConvertFrom-Json } catch { $result = $null }
    }

    if ($null -eq $result) {
        # Not JSON: the CLI failed before producing a result. Log it verbatim --
        # BOARD-OPS.md points people here when a card stops advancing.
        foreach ($line in ($raw -split "`r?`n")) {
            if ($line.Trim()) { Write-Log "  $line" }
        }
        Write-Log "END exit=$exitCode (no JSON result)"
        exit $exitCode
    }

    # Log what the run actually said, so the log stays greppable.
    foreach ($line in (([string]$result.result) -split "`r?`n")) {
        if ($line.Trim()) { Write-Log "  $line" }
    }

    $u = $result.usage
    $models = ''
    if ($result.PSObject.Properties.Name -contains 'modelUsage' -and $result.modelUsage) {
        $models = ($result.modelUsage.PSObject.Properties.Name | Sort-Object) -join ' '
    }

    $row = @(
        (Get-Date -Format 'o')
        $RepoPath
        $Command
        $exitCode
        $result.is_error
        $result.num_turns
        $result.duration_ms
        $u.input_tokens
        $u.output_tokens
        $u.cache_creation_input_tokens
        $u.cache_read_input_tokens
        # Invariant culture: on a cs-CZ machine the default would emit "0,255057",
        # which a CSV reader cannot tell from a field separator.
        ([double]$result.total_cost_usd).ToString('F6', [System.Globalization.CultureInfo]::InvariantCulture)
        $result.session_id
        $models
    ) | ForEach-Object { ConvertTo-CsvField $_ }
    Add-CostRow ($row -join ',')

    Write-Log ("END exit={0} turns={1} usd={2:F4} tokens={3}" -f
        $exitCode, $result.num_turns, [double]$result.total_cost_usd,
        ($u.input_tokens + $u.output_tokens + $u.cache_creation_input_tokens + $u.cache_read_input_tokens))
    exit $exitCode
}
catch {
    Write-Log "EXCEPTION: $($_.Exception.Message)"
    exit 1
}
finally {
    if (Test-Path -LiteralPath $lockFile) {
        Remove-Item -LiteralPath $lockFile -Force -ErrorAction SilentlyContinue
    }
}
