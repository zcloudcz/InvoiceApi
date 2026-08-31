<#
.SYNOPSIS
    Renders the run objects from Get-AgentTokenStats.ps1 as a self-contained
    HTML report.

.DESCRIPTION
    Called by Get-AgentTokenStats.ps1 -Html, or directly with piped run objects.
    The page has no external assets beyond a Google Fonts stylesheet, so it can be
    opened from disk or published as an Artifact unchanged.

    It is a panel to be scanned, not a document to be read: the three readouts at
    the top answer "how much", the ranked bars answer "who", and the token-class
    split inside each bar answers "why" -- cache reads dominate almost every run,
    which is the fact that makes turn count matter more than context size.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [object[]] $Runs,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

begin { $all = New-Object System.Collections.Generic.List[object] }
process { foreach ($r in $Runs) { $all.Add($r) } }

end {

if ($all.Count -eq 0) { Write-Warning 'No runs to report.'; return }

$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Format-Int  { param([double] $V) return $V.ToString('N0', $inv) }
function Format-Usd  { param($V) if ($null -eq $V) { return 'n/a' } return '$' + ([double]$V).ToString('N2', $inv) }
function Format-Num  { param([double] $V, [int] $D = 2) return $V.ToString('F' + $D, $inv) }
function Format-Html { param([string] $S) return [System.Net.WebUtility]::HtmlEncode($S) }

# Compact token counts for axis and label use, where full digits would crowd out
# the bar they annotate.
function Format-Short {
    param([double] $V)
    if ($V -ge 1e9) { return (Format-Num ($V / 1e9) 2) + 'B' }
    if ($V -ge 1e6) { return (Format-Num ($V / 1e6) 1) + 'M' }
    if ($V -ge 1e3) { return (Format-Num ($V / 1e3) 0) + 'k' }
    return (Format-Int $V)
}

#region aggregation -----------------------------------------------------------

$totalBilled = ($all | Measure-Object EstBilledTok -Sum).Sum
$totalUsd    = ($all | Where-Object { $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum
$totalTurns  = ($all | Measure-Object Turns -Sum).Sum
$measured    = @($all | Where-Object { $_.Exact }).Count

$mainUsd = ($all | Where-Object { $_.AgentType -eq '__main__' -and $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum
$mainShare = if ($totalUsd -gt 0) { 100 * $mainUsd / $totalUsd } else { 0 }

$dates = @($all | Where-Object { $null -ne $_.Timestamp } | ForEach-Object { $_.Timestamp } | Sort-Object)
$range = if ($dates.Count -gt 0) {
    '{0} to {1}' -f $dates[0].ToString('d MMM yyyy', $inv), $dates[-1].ToString('d MMM yyyy', $inv)
} else { 'unknown range' }

function Group-Runs {
    param([string] $Property)
    $all | Group-Object $Property | ForEach-Object {
        $g = $_.Group
        [pscustomobject]@{
            Name     = $_.Name
            Runs     = $_.Count
            Measured = @($g | Where-Object { $_.Exact }).Count
            Billed   = [double](($g | Measure-Object EstBilledTok -Sum).Sum)
            Usd      = ($g | Where-Object { $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum
            Turns    = [double](($g | Measure-Object Turns -Sum).Sum)
            InputTok = [double](($g | Measure-Object EstInputTok -Sum).Sum)
            OutputTok = [double](($g | Measure-Object EstOutputTok -Sum).Sum)
            CacheCreateTok = [double](($g | Measure-Object EstCacheCreateTok -Sum).Sum)
            CacheReadTok   = [double](($g | Measure-Object EstCacheReadTok -Sum).Sum)
            Models   = (($g | ForEach-Object { $_.Model } | Where-Object { $_ } | Sort-Object -Unique) -join ', ')
        }
    } | Sort-Object Billed -Descending
}

$byAgent   = Group-Runs 'Role'
$byModel   = Group-Runs 'Model'
$byProject = Group-Runs 'Project'
$byDay     = $all | Group-Object Day | ForEach-Object {
    [pscustomobject]@{
        Day = $_.Name
        Usd = [double](($_.Group | Where-Object { $null -ne $_.EstUsd } | Measure-Object EstUsd -Sum).Sum)
        Billed = [double](($_.Group | Measure-Object EstBilledTok -Sum).Sum)
    }
} | Where-Object { $_.Day -ne 'unknown' } | Sort-Object Day

$topRuns = $all | Where-Object { $_.AgentType -ne '__main__' } |
           Sort-Object EstBilledTok -Descending | Select-Object -First 10

#endregion

#region markup ----------------------------------------------------------------

$series = @(
    @{ Key = 'CacheReadTok';   Label = 'Cache read';  Var = '--s-cache-read' }
    @{ Key = 'CacheCreateTok'; Label = 'Cache write'; Var = '--s-cache-write' }
    @{ Key = 'OutputTok';      Label = 'Output';      Var = '--s-output' }
    @{ Key = 'InputTok';       Label = 'Input';       Var = '--s-input' }
)

$maxAgentBilled = ($byAgent | Measure-Object Billed -Maximum).Maximum
if ($maxAgentBilled -le 0) { $maxAgentBilled = 1 }

$sb = New-Object System.Text.StringBuilder
function Add-Line { param([string] $Text) [void]$sb.AppendLine($Text) }

# --- ranked agent bars ---
$agentRows = New-Object System.Text.StringBuilder
foreach ($a in $byAgent) {
    $rowWidth = 100 * $a.Billed / $maxAgentBilled
    $segments = New-Object System.Text.StringBuilder
    foreach ($s in $series) {
        $v = [double]$a.($s.Key)
        if ($v -le 0) { continue }
        $pct = 100 * $v / $a.Billed
        [void]$segments.Append(('<span class="seg" style="width:{0}%;background:var({1})" data-label="{2}"></span>' -f
            (Format-Num $pct 4), $s.Var, (Format-Html $s.Label)))
    }

    $quality = if ($a.Measured -eq $a.Runs) { '<span class="chip chip-ok">measured</span>' }
               else { '<span class="chip chip-est">{0}/{1} measured</span>' -f $a.Measured, $a.Runs }

    $tip = '{0} &middot; {1} runs &middot; {2} turns<br>cache read {3} &middot; cache write {4} &middot; output {5} &middot; input {6}' -f
        (Format-Html $a.Name), $a.Runs, (Format-Int $a.Turns),
        (Format-Short $a.CacheReadTok), (Format-Short $a.CacheCreateTok),
        (Format-Short $a.OutputTok), (Format-Short $a.InputTok)

    [void]$agentRows.AppendLine(@"
        <div class="bar-row">
          <div class="bar-name">$(Format-Html $a.Name) $quality</div>
          <div class="bar-track"><div class="bar" style="width:$(Format-Num $rowWidth 4)%">$($segments.ToString())</div>
            <div class="tip">$tip</div>
          </div>
          <div class="bar-value"><strong>$(Format-Usd $a.Usd)</strong><span>$(Format-Short $a.Billed)</span></div>
        </div>
"@)
}

# --- daily series (SVG) ---
$chartW = 760; $chartH = 200; $padL = 44; $padR = 12; $padT = 14; $padB = 26
$dailySvg = ''
if ($byDay.Count -ge 2) {
    $maxDay = ($byDay | Measure-Object Usd -Maximum).Maximum
    if ($maxDay -le 0) { $maxDay = 1 }
    $plotW = $chartW - $padL - $padR
    $plotH = $chartH - $padT - $padB
    $step  = $plotW / [Math]::Max(1, ($byDay.Count - 1))

    $pts = @()
    $i = 0
    foreach ($d in $byDay) {
        $x = $padL + $i * $step
        $y = $padT + $plotH - ($plotH * $d.Usd / $maxDay)
        $pts += , @($x, $y, $d)
        $i++
    }

    $line = ($pts | ForEach-Object { '{0},{1}' -f (Format-Num $_[0] 1), (Format-Num $_[1] 1) }) -join ' '
    $area = '{0},{1} {2} {3},{1}' -f (Format-Num $pts[0][0] 1), (Format-Num ($padT + $plotH) 1), $line, (Format-Num $pts[-1][0] 1)

    $dots = ''
    foreach ($p in $pts) {
        $dots += '<circle class="pt" cx="{0}" cy="{1}" r="4" data-day="{2}" data-usd="{3}" data-tok="{4}"></circle>' -f
            (Format-Num $p[0] 1), (Format-Num $p[1] 1), $p[2].Day, (Format-Usd $p[2].Usd), (Format-Short $p[2].Billed)
    }

    $grid = ''
    foreach ($f in @(0, 0.5, 1)) {
        $gy = $padT + $plotH - $plotH * $f
        $grid += '<line class="grid" x1="{0}" y1="{1}" x2="{2}" y2="{1}"></line>' -f $padL, (Format-Num $gy 1), ($chartW - $padR)
        $grid += '<text class="axis" x="{0}" y="{1}" text-anchor="end">{2}</text>' -f ($padL - 8), (Format-Num ($gy + 4) 1), (Format-Usd ($maxDay * $f))
    }

    $firstDay = $byDay[0].Day
    $lastDay  = $byDay[-1].Day

    $dailySvg = @"
      <div class="chart-wrap">
        <svg viewBox="0 0 $chartW $chartH" class="daily" role="img" aria-label="Daily spend">
          $grid
          <polygon class="area" points="$area"></polygon>
          <polyline class="line" points="$line"></polyline>
          $dots
          <text class="axis" x="$padL" y="$($chartH - 6)">$firstDay</text>
          <text class="axis" x="$($chartW - $padR)" y="$($chartH - 6)" text-anchor="end">$lastDay</text>
        </svg>
        <div class="chart-tip" hidden></div>
      </div>
"@
}

function New-TableRows {
    param($Groups, [int] $Take = 100)
    $max = ($Groups | Measure-Object Billed -Maximum).Maximum
    if ($max -le 0) { $max = 1 }
    $out = New-Object System.Text.StringBuilder
    foreach ($g in ($Groups | Select-Object -First $Take)) {
        [void]$out.AppendLine(@"
        <tr>
          <th scope="row">$(Format-Html $g.Name)</th>
          <td class="num">$($g.Runs)</td>
          <td class="num">$(Format-Int $g.Turns)</td>
          <td class="num">$(Format-Int $g.Billed)</td>
          <td class="num">$(Format-Usd $g.Usd)</td>
          <td class="minibar"><span style="width:$(Format-Num (100 * $g.Billed / $max) 3)%"></span></td>
        </tr>
"@)
    }
    return $out.ToString()
}

$legend = ''
foreach ($s in $series) {
    $legend += '<span class="key"><i style="background:var({0})"></i>{1}</span>' -f $s.Var, (Format-Html $s.Label)
}

$topRunRows = New-Object System.Text.StringBuilder
foreach ($r in $topRuns) {
    $desc = if ($r.Prompt) { $r.Prompt } else { '' }
    if ($desc.Length -gt 110) { $desc = $desc.Substring(0, 110) + '...' }
    [void]$topRunRows.AppendLine(@"
        <tr>
          <th scope="row">$(Format-Html $r.Role)</th>
          <td>$(Format-Html $r.Project)</td>
          <td class="num">$(Format-Int $r.Turns)</td>
          <td class="num">$(Format-Int $r.EstBilledTok)</td>
          <td class="num">$(Format-Usd $r.EstUsd)</td>
          <td class="desc">$(Format-Html $desc)</td>
        </tr>
"@)
}

$estimatedRows = $all.Count - $measured
$qualityNote = if ($estimatedRows -eq 0) {
    'Every row was measured turn by turn from its own transcript.'
} else {
    "$estimatedRows of $($all.Count) rows predate the subagent transcripts Claude Code has kept since July 2026; those are reconstructed from peak context and understate long runs."
}

#endregion

$html = @"
<title>Agent Token Ledger</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=IBM+Plex+Mono:wght@400;500;600&family=IBM+Plex+Sans:wght@400;500;600&display=swap">
<style>
  :root {
    --ground:  #f6f7f5;
    --surface: #ffffff;
    --sunken:  #eef1ee;
    --ink:     #15201d;
    --ink-2:   #3d4b47;
    --muted:   #66756f;
    --hair:    #dee4e0;
    --accent:  #00706a;
    --ok:      #0f7a4a;

    --s-cache-read:  #009086;
    --s-cache-write: #b45309;
    --s-output:      #6d28d9;
    --s-input:       #be123c;

    --sans: 'IBM Plex Sans', ui-sans-serif, system-ui, sans-serif;
    --display: 'Archivo', var(--sans);
    --mono: 'IBM Plex Mono', ui-monospace, 'Cascadia Mono', Consolas, monospace;
  }

  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --ground:  #0f1413;
      --surface: #171d1b;
      --sunken:  #121817;
      --ink:     #e7edea;
      --ink-2:   #c2cdc9;
      --muted:   #8fa09b;
      --hair:    #29322f;
      --accent:  #2fb3a6;
      --ok:      #46b87c;

      --s-cache-read:  #1fa396;
      --s-cache-write: #c97c22;
      --s-output:      #9b7be8;
      --s-input:       #e06181;
    }
  }

  :root[data-theme="dark"] {
    --ground:  #0f1413;
    --surface: #171d1b;
    --sunken:  #121817;
    --ink:     #e7edea;
    --ink-2:   #c2cdc9;
    --muted:   #8fa09b;
    --hair:    #29322f;
    --accent:  #2fb3a6;
    --ok:      #46b87c;

    --s-cache-read:  #1fa396;
    --s-cache-write: #c97c22;
    --s-output:      #9b7be8;
    --s-input:       #e06181;
  }

  * { box-sizing: border-box; }

  body {
    margin: 0;
    background: var(--ground);
    color: var(--ink);
    font: 400 15px/1.6 var(--sans);
    -webkit-font-smoothing: antialiased;
  }

  .page { max-width: 980px; margin: 0 auto; padding: 40px 24px 72px; }

  header { border-bottom: 1px solid var(--hair); padding-bottom: 22px; margin-bottom: 30px; }
  .eyebrow {
    font: 500 11px/1 var(--mono);
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--muted);
    margin: 0 0 12px;
  }
  h1 {
    font: 700 34px/1.1 var(--display);
    letter-spacing: -0.02em;
    margin: 0 0 10px;
    text-wrap: balance;
  }
  .sub { margin: 0; color: var(--muted); max-width: 62ch; }

  .readouts {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
    gap: 1px;
    background: var(--hair);
    border: 1px solid var(--hair);
    border-radius: 4px;
    overflow: hidden;
    margin-bottom: 40px;
  }
  .readout { background: var(--surface); padding: 18px 20px; }
  .readout dt {
    font: 500 11px/1 var(--mono);
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--muted);
    margin-bottom: 10px;
  }
  .readout dd {
    margin: 0;
    font: 600 27px/1.1 var(--mono);
    font-variant-numeric: tabular-nums;
    letter-spacing: -0.02em;
  }
  .readout .foot { display: block; margin-top: 6px; font: 400 12px/1.4 var(--sans); color: var(--muted); }

  section { margin-bottom: 44px; }
  h2 {
    font: 600 19px/1.3 var(--display);
    letter-spacing: -0.01em;
    margin: 0 0 6px;
  }
  .lede { margin: 0 0 20px; color: var(--muted); max-width: 66ch; font-size: 14px; }

  .legend { display: flex; flex-wrap: wrap; gap: 16px; margin-bottom: 18px; }
  .key { display: inline-flex; align-items: center; gap: 7px; font: 400 12.5px/1 var(--sans); color: var(--ink-2); }
  .key i { width: 11px; height: 11px; border-radius: 2px; display: inline-block; }

  .bars { display: flex; flex-direction: column; gap: 12px; }
  .bar-row { display: grid; grid-template-columns: minmax(150px, 200px) 1fr 110px; gap: 16px; align-items: center; }
  .bar-name { font-size: 13.5px; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .bar-track { position: relative; background: var(--sunken); border-radius: 3px; height: 26px; }
  .bar { display: flex; height: 100%; border-radius: 3px; overflow: hidden; min-width: 3px; }
  .seg { display: block; height: 100%; box-shadow: 0 0 0 1px var(--surface) inset; }
  .bar-value { text-align: right; font-family: var(--mono); font-variant-numeric: tabular-nums; }
  .bar-value strong { display: block; font-size: 14px; font-weight: 600; }
  .bar-value span { display: block; font-size: 11.5px; color: var(--muted); }

  .tip {
    position: absolute; left: 0; bottom: calc(100% + 8px); z-index: 5;
    background: var(--ink); color: var(--ground);
    padding: 8px 11px; border-radius: 4px;
    font: 400 12px/1.5 var(--sans);
    white-space: nowrap;
    opacity: 0; pointer-events: none; transition: opacity .12s ease;
  }
  .bar-track:hover .tip, .bar-track:focus-within .tip { opacity: 1; }

  .chip {
    font: 500 10.5px/1 var(--mono);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    padding: 3px 6px; border-radius: 3px;
    border: 1px solid var(--hair);
    color: var(--muted);
  }
  .chip-ok  { color: var(--ok); border-color: color-mix(in srgb, var(--ok) 35%, transparent); }
  .chip-est { color: var(--s-cache-write); border-color: color-mix(in srgb, var(--s-cache-write) 40%, transparent); }

  .scroll { overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; font-size: 13.5px; }
  caption { text-align: left; color: var(--muted); font-size: 13px; padding-bottom: 10px; }
  th, td { padding: 9px 12px; border-bottom: 1px solid var(--hair); text-align: left; vertical-align: baseline; }
  thead th {
    font: 500 11px/1 var(--mono);
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted);
    white-space: nowrap;
  }
  tbody th { font-weight: 500; white-space: nowrap; }
  .num { font-family: var(--mono); font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
  .desc { color: var(--muted); font-size: 12.5px; }
  .minibar { width: 110px; }
  .minibar span { display: block; height: 6px; border-radius: 3px; background: var(--accent); min-width: 2px; }

  .chart-wrap { position: relative; }
  svg.daily { width: 100%; height: auto; display: block; overflow: visible; }
  .grid { stroke: var(--hair); stroke-width: 1; }
  .axis { fill: var(--muted); font: 400 10px var(--mono); }
  .area { fill: color-mix(in srgb, var(--accent) 14%, transparent); }
  .line { fill: none; stroke: var(--accent); stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
  .pt { fill: var(--surface); stroke: var(--accent); stroke-width: 2; }
  .pt:hover { fill: var(--accent); }
  .chart-tip {
    position: absolute; transform: translate(-50%, -130%);
    background: var(--ink); color: var(--ground);
    padding: 7px 10px; border-radius: 4px;
    font: 400 12px/1.45 var(--sans); white-space: nowrap; pointer-events: none;
  }

  footer {
    border-top: 1px solid var(--hair);
    padding-top: 18px; margin-top: 8px;
    color: var(--muted); font-size: 12.5px;
  }
  footer code { font-family: var(--mono); font-size: 12px; color: var(--ink-2); }

  :focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  @media (prefers-reduced-motion: reduce) { * { transition: none !important; } }

  @media (max-width: 620px) {
    .bar-row { grid-template-columns: 1fr; gap: 6px; }
    .bar-value { text-align: left; display: flex; gap: 10px; align-items: baseline; }
    h1 { font-size: 27px; }
  }
</style>

<div class="page">
  <header>
    <p class="eyebrow">Claude Code &middot; per-agent accounting</p>
    <h1>Agent Token Ledger</h1>
    <p class="sub">$range &middot; $($all.Count) rows across $($byProject.Count) project(s). Subagent turns never reach the session transcript, so ccusage attributes all of this to the session; here it is split by the agent that actually spent it.</p>
  </header>

  <dl class="readouts">
    <div class="readout">
      <dt>Billed tokens</dt>
      <dd>$(Format-Short $totalBilled)</dd>
      <span class="foot">$(Format-Int $totalBilled) across $(Format-Int $totalTurns) turns</span>
    </div>
    <div class="readout">
      <dt>Cost</dt>
      <dd>$(Format-Usd $totalUsd)</dd>
      <span class="foot">$measured of $($all.Count) rows measured</span>
    </div>
    <div class="readout">
      <dt>Orchestrator share</dt>
      <dd>$(Format-Num $mainShare 1)%</dd>
      <span class="foot">$(Format-Usd $mainUsd) spent by the session itself, not by any subagent</span>
    </div>
  </dl>

  <section>
    <h2>Where the tokens go</h2>
    <p class="lede">Ranked by billed tokens. Each bar is split by token class &mdash; cache reads dominate almost every run, because every turn re-reads the whole conversation. That is why turn count drives cost more than context size does.</p>
    <div class="legend">$legend</div>
    <div class="bars">
$($agentRows.ToString())
    </div>
  </section>

$(if ($dailySvg) {
@"
  <section>
    <h2>Spend by day</h2>
    <p class="lede">Daily cost across every project in range.</p>
$dailySvg
  </section>
"@
})

  <section>
    <h2>By model</h2>
    <p class="lede">The same spend, grouped by the model that ran it. Model choice per role is the cheapest lever available.</p>
    <div class="scroll">
      <table>
        <thead><tr><th>Model</th><th class="num">Runs</th><th class="num">Turns</th><th class="num">Tokens</th><th class="num">Cost</th><th>Share</th></tr></thead>
        <tbody>
$(New-TableRows $byModel)
        </tbody>
      </table>
    </div>
  </section>

  <section>
    <h2>By project</h2>
    <div class="scroll">
      <table>
        <thead><tr><th>Project</th><th class="num">Runs</th><th class="num">Turns</th><th class="num">Tokens</th><th class="num">Cost</th><th>Share</th></tr></thead>
        <tbody>
$(New-TableRows $byProject)
        </tbody>
      </table>
    </div>
  </section>

  <section>
    <h2>Costliest single runs</h2>
    <p class="lede">Individual subagent dispatches, orchestrator excluded. These are the concrete candidates for a narrower prompt or a cheaper model.</p>
    <div class="scroll">
      <table>
        <thead><tr><th>Agent</th><th>Project</th><th class="num">Turns</th><th class="num">Tokens</th><th class="num">Cost</th><th>Dispatch</th></tr></thead>
        <tbody>
$($topRunRows.ToString())
        </tbody>
      </table>
    </div>
  </section>

  <footer>
    <p>$qualityNote</p>
    <p>Generated by <code>Get-AgentTokenStats.ps1 -Html</code>. Prices from the Claude API pricing page; the 1M context window is billed at standard rates, so a <code>[1m]</code> model id costs the same as its base model.</p>
  </footer>
</div>

<script>
  (function () {
    var wrap = document.querySelector('.chart-wrap');
    if (!wrap) return;
    var tip = wrap.querySelector('.chart-tip');
    tip.hidden = true;

    wrap.querySelectorAll('.pt').forEach(function (pt) {
      function show() {
        var box = pt.getBoundingClientRect();
        var host = wrap.getBoundingClientRect();
        tip.innerHTML = pt.dataset.day + ' &middot; <strong>' + pt.dataset.usd + '</strong> &middot; ' + pt.dataset.tok;
        tip.hidden = false;
        tip.style.left = (box.left + box.width / 2 - host.left) + 'px';
        tip.style.top = (box.top - host.top) + 'px';
      }
      function hide() { tip.hidden = true; }
      pt.addEventListener('mouseenter', show);
      pt.addEventListener('mouseleave', hide);
      pt.addEventListener('focus', show);
      pt.addEventListener('blur', hide);
      pt.setAttribute('tabindex', '0');
    });
  })();
</script>
"@

$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# UTF-8 without a BOM: a BOM ahead of the markup shows up as a stray glyph when
# the file is served or embedded.
[System.IO.File]::WriteAllText($OutputPath, $html, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "HTML report written to $OutputPath" -ForegroundColor DarkGray

}
