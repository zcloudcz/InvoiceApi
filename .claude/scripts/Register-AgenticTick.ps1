# Registers (or updates) a Windows Scheduled Task that runs the
# AgenticTeam `/tick` slash command against a given repository on a
# fixed interval. The task runs as the current interactive user so that
# the Claude Code CLI uses the user's existing login session.
#
# Usage:
#   pwsh -File .\Register-AgenticTick.ps1 -RepoPath C:\GIT\ZCLOUD\MyApp
#   pwsh -File .\Register-AgenticTick.ps1 -RepoPath C:\GIT\ZCLOUD\MyApp -Minutes 10
#   pwsh -File .\Register-AgenticTick.ps1 -RepoPath C:\GIT\ZCLOUD\MyApp -Command "/ticks"
#
# Re-running with the same -RepoPath replaces the existing task.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepoPath,

    [ValidateRange(1, 1440)]
    [int]$Minutes = 5,

    [string]$Command = "/tick",

    [string]$TaskName,

    [string]$RunnerScript = (Join-Path $PSScriptRoot "Invoke-AgenticTick.ps1")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RepoPath -PathType Container)) {
    throw "RepoPath does not exist: $RepoPath"
}
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path

if (-not (Test-Path -LiteralPath $RunnerScript -PathType Leaf)) {
    throw "Runner script not found: $RunnerScript"
}
$RunnerScript = (Resolve-Path -LiteralPath $RunnerScript).Path

if (-not $TaskName) {
    $slug = (Split-Path -Leaf $RepoPath) -replace '[^A-Za-z0-9_\-]', '_'
    $TaskName = "AgenticTeam-Tick-$slug"
}

$argList = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', "`"$RunnerScript`""
    '-RepoPath', "`"$RepoPath`""
    '-Command', "`"$Command`""
) -join ' '

$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument $argList

# Trigger every N minutes, starting one minute from now, indefinitely.
$start = (Get-Date).AddMinutes(1)
$trigger = New-ScheduledTaskTrigger -Once -At $start `
    -RepetitionInterval (New-TimeSpan -Minutes $Minutes)

# Run as the current user, only when interactively logged on, so the
# Claude Code CLI inherits the user's session/login.
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

# Belt-and-suspenders against overlap (runner script also has a lock).
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes ([Math]::Max(2, $Minutes - 1)))

$task = New-ScheduledTask `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description "AgenticTeam: runs '$Command' against $RepoPath every $Minutes minute(s)."

Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

Write-Host "Registered scheduled task: $TaskName"
Write-Host "  Repo:     $RepoPath"
Write-Host "  Command:  $Command"
Write-Host "  Interval: every $Minutes minute(s), starting $start"
Write-Host "  Logs:     $(Join-Path $env:LOCALAPPDATA 'AgenticTeam')"
Write-Host ""
Write-Host "Manage with:"
Write-Host "  Get-ScheduledTask -TaskName $TaskName"
Write-Host "  Start-ScheduledTask -TaskName $TaskName        # run now"
Write-Host "  Unregister-ScheduledTask -TaskName $TaskName -Confirm:`$false"
