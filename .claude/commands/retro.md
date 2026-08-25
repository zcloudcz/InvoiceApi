---
description: Run AgentRetro — retrospective over the last iteration; turns confirmed patterns into process changes. Usage: /retro [since <date|sha>]
---

## What this does

Invoke subagent `agent-retro`. Optional argument: a window
(`since 2026-08-23`, `since <sha>`). Default window is "since the last
`## Běh` entry in `.claude/FLOW-NOTES.md`".

The retro reads what the iteration actually cost — kickback reasons,
rework caused by parallelism, misattributed causes, warden escalations,
infrastructure losses — and converts **recurring** patterns into edits in
the place the relevant role reads: role definitions, commands, rules, or
`MEMORY.md`'s environment-traps section. Anything needing code becomes an
issue instead.

It appends one `## Běh <date>` section to `.claude/FLOW-NOTES.md`.

## When to use

- **After a drain** — `/ticks` ends with it automatically. Run it manually
  when you drained by hand or interrupted the loop.
- **After anything that hurt** — a broken integration branch, a PR that
  needed three rounds, a day lost to infrastructure. Do not wait for a
  tidy iteration boundary; the evidence is freshest now.
- **Before touching `../AgenticTeam/`** — the retro record is the argument
  for what to port upstream.

## When NOT to use

- Mid-drain. It reads the run as a whole; halfway through it sees noise.
- To settle a disagreement about a specific PR. That is the reviewer's
  job — the retro asks why the disagreement was possible, not who was right.

## What it may and may not do

May: edit `.claude/agents/*.md` (+ the `.codex` mirror),
`.claude/commands/*.md`, `AGENT-RULES.md`, `BOARD-OPS.md`, `FLOW-NOTES.md`,
`MEMORY.md`; file issues. Capped at 5 process edits per run.

May not: touch product source, move cards, merge, or **weaken any quality
gate** — no relaxing review/test requirements, no lowering coverage or
mutation expectations, no deleting a rule because agents kept tripping on
it. A gate it believes is counterproductive gets an argument in
FLOW-NOTES and a `needs:human` issue, not a quiet edit.

## Note on scope

Retro is deliberately **not** part of `agent-warden`. Warden runs as Phase 0
of every `/ticks` pass and is intentionally judgment-free (it fixes only
mechanically-derivable board state). Retro runs once per iteration and
holds the pen over the rules every other agent follows. Same read-only
posture on source, very different cadence and blast radius — warden feeds
retro its escalation trail, and that is the whole coupling.
