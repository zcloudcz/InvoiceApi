---
description: Show token and cost spend broken down per agent for this repo
argument-hint: "[-GroupBy Agent|Model|Project|Day] [-Since <date>] [-Agent <name>] [-Html <path>] [-Csv <path>]"
allowed-tools: PowerShell, Bash, Read
---

Report how many tokens each agent burned.

## Why this exists

`ccusage` groups by day, session, project and model, but it cannot attribute
anything to an individual subagent: Claude Code never writes a subagent's turns
into the session transcript, so every subagent's spend is invisible to it. The
script below reads the per-agent summary block the transcript *does* keep and
reconstructs the rest.

## Run it

The transcript directory for the current repo is derived from its absolute path:
drive colon and every separator become `-`, so `C:\GIT\ZCLOUD\InvoiceApi` becomes
`C--GIT-ZCLOUD-InvoiceApi`.

1. Determine the current working directory.
2. Build the project slug from it with that rule.
3. Run, passing through any arguments the user gave in `$ARGUMENTS`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "<repo>\.claude\scripts\Get-AgentTokenStats.ps1" `
  -Project "<slug>" <arguments>
```

If the repo has no `.claude\scripts\Get-AgentTokenStats.ps1`, fall back to the
AgenticTeam template copy at
`C:\GIT\ZCLOUD\AgenticTeam\.claude\scripts\Get-AgentTokenStats.ps1`.

Drop `-Project` entirely when the user asks about *all* projects.

## Reading the output

- `Measured` is `<measured>/<runs>`. A measured row read the run's own transcript
  turn by turn and is exact. The remainder predate the subagent transcripts Claude
  Code has kept since mid-July 2026 and are reconstructed from peak context, which
  understates long runs. Say which case applies rather than presenting every figure
  as equally solid.
- `__main__` is the orchestrator itself — the `/tick` session, not a subagent. It
  is usually the largest single consumer; say so plainly rather than burying it.
- `Turns` is the number of API round-trips. Cost scales with turns times context,
  so a high-turn agent is expensive even on a small context.
- `Reads` / `Bash` / `Edits` say *why* an agent is expensive: a read-heavy agent
  is fixed by narrowing its scope, an edit-heavy one is not. They are `0` when the
  run has no parent record to read them from — that is missing data, not zero work.

Present the table, then name the top consumer and one concrete lever for it
(cheaper model for that role, narrower prompt, fewer parallel dispatches). Keep
it short; do not restate every row in prose.
