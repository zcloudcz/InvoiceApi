# Runs `claude -p "/tick"` against a target repository.
# Designed to be invoked by Windows Task Scheduler. Skips if a previous
# run is still in progress (lock file with live PID). Appends timestamped
# stdout/stderr to a rolling log under %LOCALAPPDATA%\AgenticTeam.

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

function Write-Log([string]$line) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Add-Content -LiteralPath $logFile -Value "[$stamp] $line"
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
    try {
        # Stream stdout+stderr line-by-line into the log with timestamps.
        # `claude -p` is the headless / print mode.
        & $claude.Source -p $Command 2>&1 | ForEach-Object {
            Write-Log "  $_"
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    Write-Log "END exit=$exitCode"
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
