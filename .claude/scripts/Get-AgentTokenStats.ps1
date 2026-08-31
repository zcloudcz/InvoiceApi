<#
.SYNOPSIS
    Reports Claude Code token spend broken down per subagent.

.DESCRIPTION
    A subagent's turns are not written into the session transcript (there is no
    `isSidechain: true` record anywhere), so tools that sum the transcript --
    ccusage included -- report the orchestrator only and attribute nothing to any
    subagent. This script pulls the per-agent numbers from three places:

    1. `<project>\<sessionId>\subagents\agent-<id>.jsonl` -- each subagent's own
       conversation, kept since mid-July 2026, with per-turn `message.usage`.
       This is the real billed split and is preferred wherever it exists.

    2. The parent transcript's `toolUseResult` block, which names the run
       (`agentType`, `agentId`, `resolvedModel`) and counts its tools and
       wall-clock. Merged onto the measured tokens from (1).

    3. `toolUseResult.totalTokens` alone, for runs that predate (2). That figure
       is the size of the run's *final* turn, not what it was billed -- a probe
       run reporting 20,597 had actually been billed 40,004, and the gap widens
       with every turn -- so those rows are reconstructed by integrating the
       context across the run's turns. The `Measured` column says how many rows
       in a group needed that fallback.

    Orchestrator turns are recorded per turn and are always exact; they appear as
    `__main__` and are usually the single largest consumer.

.EXAMPLE
    .\Get-AgentTokenStats.ps1
    Per-agent summary across all projects.

.EXAMPLE
    .\Get-AgentTokenStats.ps1 -Project 'C--GIT-ZCLOUD-InvoiceApi' -GroupBy Day
    Daily spend for one repo.

.EXAMPLE
    .\Get-AgentTokenStats.ps1 -Since (Get-Date).AddDays(-7) -Csv .\week.csv
    Last week, exported per run.
#>
[CmdletBinding()]
param(
    # Root of the Claude Code session transcripts.
    [string] $ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects'),

    # Project directory name, wildcards allowed (e.g. 'C--GIT-ZCLOUD-*').
    [string] $Project = '*',

    [Nullable[datetime]] $Since,
    [Nullable[datetime]] $Until,

    # Agent type filter, wildcards allowed.
    [string] $Agent = '*',

    [ValidateSet('Agent', 'Model', 'Project', 'Day')]
    [string] $GroupBy = 'Agent',

    # Export every individual run (not the aggregate) to CSV.
    [string] $Csv,

    # Write a self-contained HTML report.
    [string] $Html,

    [int] $Top = 20,

    # Defaults to model-pricing.json next to this script; resolved below because
    # $PSScriptRoot is not reliably populated inside a param default.
    [string] $PricingPath,

    # Exclude the orchestrator (main-thread) rows.
    [switch] $NoMainThread,

    # Emit the raw run objects instead of printing a table.
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
if (-not $PricingPath) { $PricingPath = Join-Path $scriptDir 'model-pricing.json' }

#region json helpers ----------------------------------------------------------

function Test-HasProperty {
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $false }
    return [bool]($Object.PSObject.Properties.Name -contains $Name)
}

function Get-Prop {
    param($Object, [string] $Name)
    if (-not (Test-HasProperty $Object $Name)) { return $null }
    return $Object.$Name
}

function Get-UsageField {
    param($Usage, [string] $Field)
    if (-not (Test-HasProperty $Usage $Field)) { return 0.0 }
    if ($null -eq $Usage.$Field) { return 0.0 }
    return [double]$Usage.$Field
}

#endregion

#region pricing ---------------------------------------------------------------

if (-not (Test-Path -LiteralPath $PricingPath)) {
    throw "Pricing table not found: $PricingPath"
}
$script:Pricing = (Get-Content -LiteralPath $PricingPath -Raw -Encoding UTF8 | ConvertFrom-Json).models
$script:PricedIds = @($script:Pricing.PSObject.Properties.Name)
$script:UnknownModels = @{}

<#
    Claude Code decorates model ids: 'claude-opus-5[1m]' marks the 1M-context
    variant, and older ids carry a snapshot date. Neither changes the price --
    the 1M window is billed at standard rates and a date suffix only pins a
    snapshot of the same model -- so both are stripped before lookup.
#>
function Resolve-ModelPrice {
    param([string] $Model)

    if ([string]::IsNullOrWhiteSpace($Model)) { return $null }

    $key = $Model -replace '\[1m\]\s*$', ''
    $key = $key -replace '-\d{8}$', ''
    $key = $key.Trim()

    if ($script:PricedIds -contains $key) { return $script:Pricing.$key }

    $script:UnknownModels[$Model] = $true
    return $null
}

# Costs a token split. Returns $null when the model is unpriced, so an unknown
# model shows as a blank cost rather than a silent $0.
function Get-TokenCost {
    param(
        [string] $Model,
        [double] $InputTok,
        [double] $OutputTok,
        [double] $CacheCreate5mTok,
        [double] $CacheCreate1hTok,
        [double] $CacheReadTok
    )

    $p = Resolve-ModelPrice -Model $Model
    if ($null -eq $p) { return $null }

    # The two cache-write durations are priced differently -- 1.25x base input for
    # the 5-minute cache, 2x for the 1-hour one. Claude Code writes 1-hour cache,
    # so collapsing the two would understate the bill by roughly 60% on that term.
    return ($InputTok         * $p.input        +
            $OutputTok        * $p.output       +
            $CacheCreate5mTok * $p.cacheWrite5m +
            $CacheCreate1hTok * $p.cacheWrite1h +
            $CacheReadTok     * $p.cacheRead) / 1000000
}

# Splits a usage block's cache creation into its 5-minute and 1-hour halves.
# Older records carry only the total, which is attributed to the 1-hour cache
# because that is what Claude Code writes.
function Get-CacheCreationSplit {
    param($Usage)

    $total = Get-UsageField $Usage 'cache_creation_input_tokens'
    $detail = Get-Prop $Usage 'cache_creation'
    if ($null -eq $detail) { return @{ M5 = 0.0; H1 = $total } }

    $m5 = Get-UsageField $detail 'ephemeral_5m_input_tokens'
    $h1 = Get-UsageField $detail 'ephemeral_1h_input_tokens'
    if (($m5 + $h1) -le 0) { return @{ M5 = 0.0; H1 = $total } }
    return @{ M5 = $m5; H1 = $h1 }
}

#endregion

#region role resolution -------------------------------------------------------

<#
    AgenticTeam runs some roles through a generic runner: `agent-reviewer` has no
    model of its own and is dispatched as `hydra` with the role file as its
    instructions. The transcript then reports `agentType = hydra` and the actual
    role is only visible in the dispatch prompt, so recover it from there.
#>
$script:RoleNames   = 'analyst|dev|ops|reviewer|tester|warden'
$script:RolePattern = "agent-($script:RoleNames)"

function Resolve-Role {
    param([string] $AgentType, [string] $Prompt)

    if ($AgentType -match "^$script:RolePattern$") { return $AgentType }
    if (-not $Prompt) { return $AgentType }

    # Role prompts open with a self-identification line ("You are **AgentOps**").
    # That is authoritative, so it is tried before any bare file reference -- a
    # role prompt often names *other* role files further down, and matching those
    # would mislabel the run.
    if ($Prompt -match "You are \**Agent-?($script:RoleNames)") {
        return 'agent-' + $Matches[1].ToLowerInvariant()
    }
    if ($Prompt -match $script:RolePattern) { return $Matches[0].ToLowerInvariant() }

    return $AgentType
}

#endregion

#region transcript parsing ----------------------------------------------------

<#
    Claude Code injects locally generated assistant messages -- rate-limit notices
    and the like -- under the model name '<synthetic>'. They never hit the API,
    carry an all-zero usage block, and have no price, so counting them would both
    inflate the turn count and raise a bogus unpriced-model warning.
#>
function Test-BilledTurn {
    param([string] $Model)
    return ($Model -ne '<synthetic>')
}

<#
    Identity of one API response, used to drop turns a resumed session replayed.
    Returns empty when the record carries neither id -- such a record cannot be
    matched against anything, and folding them all onto one blank key would make
    them delete each other.
#>
function Get-TurnKey {
    param($Record)
    $messageId = [string](Get-Prop (Get-Prop $Record 'message') 'id')
    $requestId = [string](Get-Prop $Record 'requestId')
    if (-not $messageId -and -not $requestId) { return '' }
    return '{0}|{1}' -f $messageId, $requestId
}

$runs    = New-Object System.Collections.Generic.List[object]
$mainAcc = @{}   # project|session|day|model -> accumulator for orchestrator turns
$skipped = 0

<#
    The same API response lands in the transcript more than once -- a resumed or
    compacted session replays earlier turns into the new session file, and in this
    workspace that inflated the raw sum by roughly 2x. Each response is identified
    by its message id plus request id, and each agent run by its agent id, so both
    are deduplicated on first sight. (This is the same rule ccusage applies; after
    adding it the orchestrator total matched ccusage to the token.)
#>
$seenTurns = New-Object 'System.Collections.Generic.HashSet[string]'
$seenAgents = New-Object 'System.Collections.Generic.HashSet[string]'
$dupTurns = 0
$dupAgents = 0

$projectDirs = Get-ChildItem -LiteralPath $ProjectsRoot -Directory -ErrorAction Stop |
               Where-Object { $_.Name -like $Project }

if (-not $projectDirs) { throw "No project directory under '$ProjectsRoot' matches '$Project'." }

Write-Verbose "Scanning $(@($projectDirs).Count) project directory/ies."

foreach ($dir in $projectDirs) {
    foreach ($file in (Get-ChildItem -LiteralPath $dir.FullName -Filter *.jsonl -File -ErrorAction SilentlyContinue)) {

        # Transcripts run to hundreds of MB in total. Stream the file and reject
        # lines by substring before paying for ConvertFrom-Json -- the vast
        # majority of records are neither an agent result nor an assistant turn.
        foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {

            $isAgent = $line.Contains('"totalTokens"') -or $line.Contains('subagent_tokens')
            $isMain  = (-not $NoMainThread) -and $line.Contains('"type":"assistant"')
            if (-not ($isAgent -or $isMain)) { continue }

            try   { $rec = $line | ConvertFrom-Json }
            catch { $skipped++; continue }

            $ts = $null
            if ((Test-HasProperty $rec 'timestamp') -and $rec.timestamp) {
                try { $ts = [datetime]$rec.timestamp } catch { $ts = $null }
            }
            if ($null -ne $Since -and $null -ne $ts -and $ts -lt $Since) { continue }
            if ($null -ne $Until -and $null -ne $ts -and $ts -gt $Until) { continue }

            $day = if ($null -ne $ts) { $ts.ToString('yyyy-MM-dd') } else { 'unknown' }

            # ---- orchestrator turn (exact: per-turn usage is recorded) -------
            if ($rec.type -eq 'assistant' -and (Test-HasProperty $rec.message 'usage') -and $rec.message.usage) {

                if (-not $NoMainThread) {
                    $mModel = [string]$rec.message.model
                    if (-not (Test-BilledTurn $mModel)) { continue }

                    $turnKey = Get-TurnKey $rec
                    if ($turnKey -and -not $seenTurns.Add($turnKey)) { $dupTurns++; continue }

                    $key    = '{0}|{1}|{2}|{3}' -f $dir.Name, $rec.sessionId, $day, $mModel

                    if (-not $mainAcc.ContainsKey($key)) {
                        $mainAcc[$key] = [pscustomobject]@{
                            Timestamp = $ts; Project = $dir.Name; SessionId = [string]$rec.sessionId
                            Day = $day; Model = $mModel; Turns = 0
                            InputTok = 0.0; OutputTok = 0.0; CacheReadTok = 0.0
                            CacheCreate5mTok = 0.0; CacheCreate1hTok = 0.0
                        }
                    }
                    $split = Get-CacheCreationSplit $rec.message.usage
                    $acc = $mainAcc[$key]
                    $acc.Turns            += 1
                    $acc.InputTok         += Get-UsageField $rec.message.usage 'input_tokens'
                    $acc.OutputTok        += Get-UsageField $rec.message.usage 'output_tokens'
                    $acc.CacheReadTok     += Get-UsageField $rec.message.usage 'cache_read_input_tokens'
                    $acc.CacheCreate5mTok += $split.M5
                    $acc.CacheCreate1hTok += $split.H1
                }
                continue
            }

            if (-not $isAgent) { continue }
            if (-not (Test-HasProperty $rec 'toolUseResult')) { continue }
            $r = $rec.toolUseResult
            if ($null -eq $r) { continue }

            $agentType = ''
            $peakCtx   = 0.0
            $toolUses  = 0
            $durMs     = 0.0
            $model     = ''
            $status    = ''
            $prompt    = ''
            $stats     = $null
            $shape     = 'sync'
            $lastIn    = 0.0
            $lastOut   = 0.0

            if ((Test-HasProperty $r 'totalTokens') -and $r.totalTokens) {
                # ---- synchronous Agent result -------------------------------
                $agentType = [string]$r.agentType
                $peakCtx   = [double]$r.totalTokens
                if ((Test-HasProperty $r 'totalToolUseCount') -and $r.totalToolUseCount) { $toolUses = [int]$r.totalToolUseCount }
                if ((Test-HasProperty $r 'totalDurationMs')   -and $r.totalDurationMs)   { $durMs    = [double]$r.totalDurationMs }
                if (Test-HasProperty $r 'resolvedModel') { $model  = [string]$r.resolvedModel }
                if (Test-HasProperty $r 'status')        { $status = [string]$r.status }
                if (Test-HasProperty $r 'prompt')        { $prompt = [string]$r.prompt }
                if (Test-HasProperty $r 'toolStats')     { $stats  = $r.toolStats }

                $lastUsage = if (Test-HasProperty $r 'usage') { $r.usage } else { $null }
                $lastIn    = Get-UsageField $lastUsage 'input_tokens'
                $lastOut   = Get-UsageField $lastUsage 'output_tokens'
            }
            else {
                # ---- background/async Agent result --------------------------
                # The parent gets a task-notification whose <usage> block is plain
                # text, not JSON, so it has to be matched out of the result body.
                $body = ''
                if (Test-HasProperty $r 'content') {
                    $body = if ($r.content -is [string]) { $r.content } else { ($r.content | Out-String) }
                }
                if ($body -notmatch '<subagent_tokens>(\d+)</subagent_tokens>') { continue }

                $shape   = 'async'
                $peakCtx = [double]$Matches[1]
                if ($body -match '<tool_uses>(\d+)</tool_uses>')     { $toolUses = [int]$Matches[1] }
                if ($body -match '<duration_ms>(\d+)</duration_ms>') { $durMs    = [double]$Matches[1] }
                if (Test-HasProperty $r 'agentType')     { $agentType = [string]$r.agentType }
                if (Test-HasProperty $r 'prompt')        { $prompt    = [string]$r.prompt }
                if (Test-HasProperty $r 'resolvedModel') { $model     = [string]$r.resolvedModel }

                # No per-turn usage is reported for this shape; filled in later
                # from the output-per-turn observed on same-model sync runs.
                $lastIn  = 0.0
                $lastOut = -1.0
            }

            if (-not $agentType) { $agentType = 'unknown' }

            # An agent id is unique per run; without one, fall back to the id of
            # the tool_use this result answers.
            $agentId = if (Test-HasProperty $r 'agentId') { [string]$r.agentId } else { '' }
            $agentKey = if ($agentId) { $agentId } else { '{0}|{1}' -f $rec.sessionId, $rec.uuid }
            if (-not $seenAgents.Add($agentKey)) { $dupAgents++; continue }

            $runs.Add([pscustomobject]@{
                Timestamp   = $ts
                Day         = $day
                Project     = $dir.Name
                SessionId   = [string]$rec.sessionId
                AgentId     = $agentId
                AgentType   = $agentType
                Role        = Resolve-Role -AgentType $agentType -Prompt $prompt
                Model       = $model
                Status      = $status
                Shape       = $shape
                PeakCtx     = $peakCtx
                Turns       = [Math]::Max(1, $toolUses + 1)
                ToolUses    = $toolUses
                DurationMs  = $durMs
                LastInTok   = $lastIn
                LastOutTok  = $lastOut
                Reads       = if (Test-HasProperty $stats 'readCount')     { [int]$stats.readCount }     else { 0 }
                Searches    = if (Test-HasProperty $stats 'searchCount')   { [int]$stats.searchCount }   else { 0 }
                BashCalls   = if (Test-HasProperty $stats 'bashCount')     { [int]$stats.bashCount }     else { 0 }
                Edits       = if (Test-HasProperty $stats 'editFileCount') { [int]$stats.editFileCount } else { 0 }
                LinesAdded  = if (Test-HasProperty $stats 'linesAdded')    { [int]$stats.linesAdded }    else { 0 }
                Prompt      = if ($prompt.Length -gt 200) { $prompt.Substring(0, 200) } else { $prompt }
            })
        }
    }
}

#region exact subagent usage --------------------------------------------------

<#
    Since mid-July 2026 Claude Code keeps each subagent's own conversation next to
    the session transcript:

        <project>\<sessionId>\subagents\agent-<agentId>.jsonl
        <project>\<sessionId>\subagents\agent-<agentId>.meta.json

    Those files carry per-turn `message.usage`, which is the real billed split --
    no reconstruction needed. Runs that predate the feature have no such file and
    fall back to the estimate below.

    Measured against a probe run: the transcript summary reported 20,597 tokens
    while the subagent had actually been billed 40,004. The gap widens with every
    extra turn, which is exactly why the estimate exists but is not preferred.
#>

# Pulls the dispatch prompt out of a subagent transcript so its AgenticTeam role
# can be recovered the same way it is for parent-side records.
function Get-FirstUserText {
    param($Record)
    $content = Get-Prop (Get-Prop $Record 'message') 'content'
    if ($null -eq $content) { return '' }
    if ($content -is [string]) { return $content }
    foreach ($block in @($content)) {
        if ((Get-Prop $block 'type') -eq 'text') { return [string](Get-Prop $block 'text') }
    }
    return ''
}

$exactByAgentId = @{}

foreach ($dir in $projectDirs) {
    foreach ($sessionDir in (Get-ChildItem -LiteralPath $dir.FullName -Directory -ErrorAction SilentlyContinue)) {

        $subDir = Join-Path $sessionDir.FullName 'subagents'
        if (-not (Test-Path -LiteralPath $subDir)) { continue }

        foreach ($tf in (Get-ChildItem -LiteralPath $subDir -Filter 'agent-*.jsonl' -File -ErrorAction SilentlyContinue)) {

            $agentId = $tf.BaseName -replace '^agent-', ''
            if ($exactByAgentId.ContainsKey($agentId)) { continue }

            $turns = 0; $inTok = 0.0; $outTok = 0.0; $cc5 = 0.0; $cc1h = 0.0; $crTok = 0.0
            $model = ''; $lastTotal = 0.0; $firstTs = $null; $prompt = ''
            $seenSub = New-Object 'System.Collections.Generic.HashSet[string]'

            foreach ($line in [System.IO.File]::ReadLines($tf.FullName)) {

                if ((-not $prompt) -and $line.Contains('"type":"user"')) {
                    try { $prompt = Get-FirstUserText ($line | ConvertFrom-Json) } catch { }
                    continue
                }
                if (-not $line.Contains('"type":"assistant"')) { continue }

                try { $rec = $line | ConvertFrom-Json } catch { $skipped++; continue }
                $usage = Get-Prop (Get-Prop $rec 'message') 'usage'
                if ($null -eq $usage) { continue }
                if (-not (Test-BilledTurn ([string](Get-Prop $rec.message 'model')))) { continue }
                $subKey = Get-TurnKey $rec
                if ($subKey -and -not $seenSub.Add($subKey)) { continue }

                if ($null -eq $firstTs -and (Get-Prop $rec 'timestamp')) {
                    try { $firstTs = [datetime]$rec.timestamp } catch { }
                }

                $i = Get-UsageField $usage 'input_tokens'
                $o = Get-UsageField $usage 'output_tokens'
                $c = Get-UsageField $usage 'cache_creation_input_tokens'
                $r = Get-UsageField $usage 'cache_read_input_tokens'
                $s = Get-CacheCreationSplit $usage

                $turns += 1; $inTok += $i; $outTok += $o; $crTok += $r
                $cc5 += $s.M5; $cc1h += $s.H1
                $lastTotal = $i + $o + $c + $r
                if (-not $model) { $model = [string](Get-Prop $rec.message 'model') }
            }

            if ($turns -eq 0) { continue }
            if ($null -ne $Since -and $null -ne $firstTs -and $firstTs -lt $Since) { continue }
            if ($null -ne $Until -and $null -ne $firstTs -and $firstTs -gt $Until) { continue }

            $agentType = ''
            $metaPath = Join-Path $subDir ($tf.BaseName + '.meta.json')
            if (Test-Path -LiteralPath $metaPath) {
                try { $agentType = [string](Get-Prop (Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json) 'agentType') } catch { }
            }
            if (-not $agentType) { $agentType = 'unknown' }

            $exactByAgentId[$agentId] = [pscustomobject]@{
                Timestamp = $firstTs
                Day       = if ($null -ne $firstTs) { $firstTs.ToString('yyyy-MM-dd') } else { 'unknown' }
                Project   = $dir.Name
                SessionId = $sessionDir.Name
                AgentId   = $agentId
                AgentType = $agentType
                Role      = Resolve-Role -AgentType $agentType -Prompt $prompt
                Model     = $model
                Turns     = $turns
                PeakCtx   = $lastTotal
                InputTok  = $inTok
                OutputTok = $outTok
                CacheCreate5mTok = $cc5
                CacheCreate1hTok = $cc1h
                CacheReadTok     = $crTok
            }
        }
    }
}

#endregion

#region cost reconstruction ---------------------------------------------------

<#
    Rebuilding the billed split from a peak-context figure.

    Over a run the context grows from roughly nothing to PeakCtx, one turn at a
    time, and every turn re-reads everything accumulated so far:

      cache reads   ~ Turns * PeakCtx / 2   (area under a linear ramp)
      cache writes  ~ PeakCtx               (each token is written to cache once)
      output        ~ Turns * output-of-last-turn
      plain input   ~ Turns * input-of-last-turn (near zero once caching is warm)

    Cache reads dominate the bill, and that is the term with the firmest ground
    under it, so the total lands far closer than PeakCtx alone. It is still an
    estimate -- the hook ledger replaces it with measured values where available.
#>

# Async results carry no per-turn output figure. Rather than invent one, borrow
# the median output-per-turn actually observed on sync runs of the same model.
$outPerTurnByModel = @{}
$syncRuns = @($runs | Where-Object { $_.Shape -eq 'sync' -and $_.LastOutTok -ge 0 })
foreach ($g in ($syncRuns | Group-Object Model)) {
    $vals = @($g.Group | ForEach-Object { $_.LastOutTok } | Sort-Object)
    if ($vals.Count -gt 0) { $outPerTurnByModel[$g.Name] = $vals[[int]($vals.Count / 2)] }
}
$globalOutPerTurn = 0.0
$allVals = @($syncRuns | ForEach-Object { $_.LastOutTok } | Sort-Object)
if ($allVals.Count -gt 0) { $globalOutPerTurn = $allVals[[int]($allVals.Count / 2)] }

$usedExact = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($run in $runs) {

    # A measured run always beats a reconstructed one. The parent record is still
    # the better source for tool counts and wall-clock, so the two are merged
    # rather than one replacing the other.
    $exact = $null
    if ($run.AgentId -and $exactByAgentId.ContainsKey($run.AgentId)) {
        $exact = $exactByAgentId[$run.AgentId]
        [void]$usedExact.Add($run.AgentId)
    }

    if ($null -ne $exact) {
        $inTok = $exact.InputTok; $outTok = $exact.OutputTok
        $cc5 = $exact.CacheCreate5mTok; $cc1h = $exact.CacheCreate1hTok
        $crTok = $exact.CacheReadTok
        $run.Turns = $exact.Turns
        if (-not $run.Model) { $run.Model = $exact.Model }
        $isExact = $true
    }
    else {
        $outPerTurn = $run.LastOutTok
        if ($outPerTurn -lt 0) {
            if ($run.Model -and $outPerTurnByModel.ContainsKey($run.Model)) { $outPerTurn = $outPerTurnByModel[$run.Model] }
            else { $outPerTurn = $globalOutPerTurn }
        }
        $crTok  = $run.Turns * $run.PeakCtx / 2
        # Claude Code writes 1-hour cache, and these rows predate the per-turn
        # detail that would say otherwise, so the whole write is priced as 1-hour.
        $cc5    = 0.0
        $cc1h   = $run.PeakCtx
        $outTok = $run.Turns * $outPerTurn
        $inTok  = $run.Turns * $run.LastInTok
        $isExact = $false
    }

    $run | Add-Member NoteProperty EstInputTok       ([long]$inTok)
    $run | Add-Member NoteProperty EstOutputTok      ([long]$outTok)
    $run | Add-Member NoteProperty EstCacheCreateTok ([long]($cc5 + $cc1h))
    $run | Add-Member NoteProperty EstCacheReadTok   ([long]$crTok)
    $run | Add-Member NoteProperty EstBilledTok      ([long]($inTok + $outTok + $cc5 + $cc1h + $crTok))
    $run | Add-Member NoteProperty EstUsd            (Get-TokenCost -Model $run.Model -InputTok $inTok -OutputTok $outTok -CacheCreate5mTok $cc5 -CacheCreate1hTok $cc1h -CacheReadTok $crTok)
    $run | Add-Member NoteProperty Exact             $isExact
}

# A subagent transcript can outlive the parent record that dispatched it -- for
# instance when the parent session was trimmed. Those runs would otherwise vanish.
foreach ($id in $exactByAgentId.Keys) {
    if ($usedExact.Contains($id)) { continue }
    $e = $exactByAgentId[$id]
    $billed = $e.InputTok + $e.OutputTok + $e.CacheCreate5mTok + $e.CacheCreate1hTok + $e.CacheReadTok
    if ($billed -le 0) { continue }
    $runs.Add([pscustomobject]@{
        Timestamp = $e.Timestamp; Day = $e.Day; Project = $e.Project; SessionId = $e.SessionId
        AgentId = $e.AgentId; AgentType = $e.AgentType; Role = $e.Role; Model = $e.Model; Status = 'completed'
        Shape = 'subagent-transcript'; PeakCtx = $e.PeakCtx; Turns = $e.Turns; ToolUses = 0; DurationMs = 0.0
        LastInTok = 0.0; LastOutTok = 0.0
        Reads = 0; Searches = 0; BashCalls = 0; Edits = 0; LinesAdded = 0; Prompt = ''
        EstInputTok = [long]$e.InputTok; EstOutputTok = [long]$e.OutputTok
        EstCacheCreateTok = [long]($e.CacheCreate5mTok + $e.CacheCreate1hTok); EstCacheReadTok = [long]$e.CacheReadTok
        EstBilledTok = [long]$billed
        EstUsd = (Get-TokenCost -Model $e.Model -InputTok $e.InputTok -OutputTok $e.OutputTok -CacheCreate5mTok $e.CacheCreate5mTok -CacheCreate1hTok $e.CacheCreate1hTok -CacheReadTok $e.CacheReadTok)
        Exact = $true
    })
}

# Orchestrator rows are measured per turn, so they are folded in as-is.
foreach ($acc in $mainAcc.Values) {
    $billed = $acc.InputTok + $acc.OutputTok + $acc.CacheCreate5mTok + $acc.CacheCreate1hTok + $acc.CacheReadTok
    if ($billed -le 0) { continue }
    $runs.Add([pscustomobject]@{
        Timestamp = $acc.Timestamp; Day = $acc.Day; Project = $acc.Project; SessionId = $acc.SessionId
        AgentId = ''; AgentType = '__main__'; Role = '__main__'; Model = $acc.Model; Status = 'completed'
        Shape = 'main'; PeakCtx = 0.0; Turns = $acc.Turns; ToolUses = 0; DurationMs = 0.0
        LastInTok = 0.0; LastOutTok = 0.0
        Reads = 0; Searches = 0; BashCalls = 0; Edits = 0; LinesAdded = 0; Prompt = ''
        EstInputTok = [long]$acc.InputTok; EstOutputTok = [long]$acc.OutputTok
        EstCacheCreateTok = [long]($acc.CacheCreate5mTok + $acc.CacheCreate1hTok); EstCacheReadTok = [long]$acc.CacheReadTok
        EstBilledTok = [long]$billed
        EstUsd = (Get-TokenCost -Model $acc.Model -InputTok $acc.InputTok -OutputTok $acc.OutputTok -CacheCreate5mTok $acc.CacheCreate5mTok -CacheCreate1hTok $acc.CacheCreate1hTok -CacheReadTok $acc.CacheReadTok)
        Exact = $true
    })
}

# Agent filtering happens here rather than during parsing so that a filtered run
# still contributes its model's output-per-turn sample to the reconstruction.
if ($Agent -ne '*') {
    $kept = @($runs | Where-Object { $_.AgentType -like $Agent -or $_.Role -like $Agent })
    $runs = New-Object System.Collections.Generic.List[object]
    if ($kept.Count -gt 0) { $runs.AddRange($kept) }
}

#endregion

#region output ----------------------------------------------------------------

if ($runs.Count -eq 0) {
    Write-Warning 'No agent runs matched the given filters.'
    return
}

if ($Csv) {
    $runs | Select-Object Timestamp, Day, Project, SessionId, AgentId, AgentType, Role, Model, Status, Shape,
                          PeakCtx, Turns, ToolUses, DurationMs,
                          EstInputTok, EstOutputTok, EstCacheCreateTok, EstCacheReadTok, EstBilledTok, EstUsd, Exact,
                          Reads, Searches, BashCalls, Edits, LinesAdded, Prompt |
            Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8
    Write-Host "Per-run detail written to $Csv" -ForegroundColor DarkGray
}

if ($PassThru) { return $runs }

$groupProp = switch ($GroupBy) {
    'Agent'   { 'Role' }
    'Model'   { 'Model' }
    'Project' { 'Project' }
    'Day'     { 'Day' }
}

$summary = $runs | Group-Object $groupProp | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Key     = $_.Name
        Runs    = $_.Count
        Measured = @($g | Where-Object { $_.Exact }).Count
        Billed  = ($g | Measure-Object EstBilledTok -Sum).Sum
        Usd     = ($g | Where-Object { $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum
        Turns   = ($g | Measure-Object Turns -Sum).Sum
        Reads   = ($g | Measure-Object Reads -Sum).Sum
        Bash    = ($g | Measure-Object BashCalls -Sum).Sum
        Edits   = ($g | Measure-Object Edits -Sum).Sum
    }
} | Sort-Object Billed -Descending | Select-Object -First $Top

$summary |
    Format-Table @{ N = $GroupBy;   E = { $_.Key } },
                 @{ N = 'Runs';     E = { $_.Runs };                        A = 'right' },
                 @{ N = 'Measured'; E = { '{0}/{1}' -f $_.Measured, $_.Runs }; A = 'right' },
                 @{ N = 'Billed';   E = { '{0:N0}' -f $_.Billed };          A = 'right' },
                 @{ N = 'USD';      E = { if ($null -eq $_.Usd) { 'n/a' } else { '{0:N2}' -f $_.Usd } }; A = 'right' },
                 @{ N = 'Turns';    E = { '{0:N0}' -f $_.Turns };           A = 'right' },
                 @{ N = 'Reads';    E = { $_.Reads };                       A = 'right' },
                 @{ N = 'Bash';     E = { $_.Bash };                        A = 'right' },
                 @{ N = 'Edits';    E = { $_.Edits };                       A = 'right' } |
    Out-Host

$totalUsd    = ($runs | Where-Object { $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum
$totalBilled = ($runs | Measure-Object EstBilledTok -Sum).Sum
$exactRuns   = @($runs | Where-Object { $_.Exact }).Count

Write-Host ''
Write-Host ('  {0:N0} rows | {1:N0} billed tokens | ${2:N2} | {3} of {4} rows measured per turn' -f
    $runs.Count, $totalBilled, $totalUsd, $exactRuns, $runs.Count) -ForegroundColor Cyan
Write-Host ('  Measured rows read per-turn usage (orchestrator turns, and subagent transcripts kept since 2026-07).' ) -ForegroundColor DarkGray
Write-Host ('  The rest are reconstructed from peak context and understate long runs.') -ForegroundColor DarkGray
if ($skipped -gt 0)   { Write-Host "  $skipped unparsable line(s) skipped." -ForegroundColor DarkGray }
if ($dupTurns -gt 0 -or $dupAgents -gt 0) {
    Write-Host ("  deduplicated {0:N0} replayed turn(s) and {1:N0} replayed agent run(s)." -f $dupTurns, $dupAgents) -ForegroundColor DarkGray
}
if ($script:UnknownModels.Count -gt 0) {
    Write-Warning ('Unpriced model(s), counted in tokens but not in USD: {0}' -f (($script:UnknownModels.Keys | Sort-Object) -join ', '))
}

if ($Html) {
    $htmlScript = Join-Path $scriptDir 'New-AgentTokenReport.ps1'
    if (Test-Path -LiteralPath $htmlScript) {
        & $htmlScript -Runs $runs -OutputPath $Html
    }
    else {
        Write-Warning "HTML report generator not found: $htmlScript"
    }
}

#endregion
