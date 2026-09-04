---
description: Run AgentWarden — board sweep that detects state inconsistencies, auto-fixes safe cases, escalates ambiguous ones with needs:human.
---

## Preflight — gh scopes

Before any board read, verify gh has the `project` scope. If it does
not, every `gh project` call returns 401 with a misleading
"missing required scopes [read:project]" error.

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If the check fails, STOP — surface the exact command above.

## What this does

Invoke subagent `agent-warden`. No arguments — warden sweeps the
entire board.

The warden runs the invariant catalog from
`~/.claude/agents/agent-warden.md`:

- role label ↔ status column mismatch
- PR state ↔ column mismatch
- stale `needs:rebase` without `role:dev`
- story roll-up gaps (all children closed, story still `Decomposed`)
- abandoned `Blocked` cards (>14 days)
- stale WIP (>7 days no activity)
- orphan `analyst:approved`
- agent-set `Approved` (policy violation)

Auto-fixes only mechanically-safe cases (cap: 10 per sweep). Anything
ambiguous gets a `needs:human` label and a diagnostic comment.

## When to use

- **Manually** — when you notice a card looks stuck and want a global
  consistency check (`/tick-warden`).
- **As part of `/loop`** — `/loop 30m /tick-warden` keeps the board
  clean semi-autonomously.
- **Before a long parallel drain** — `/ticks` invokes `/tick-warden`
  as Phase 0 to clean up stale state before dispatching parallel devs
  and testers.

## When NOT to use

- During active human review of a `Decomposed` story — warden honors
  skip rules but the noise of a fresh comment thread may distract the
  human reviewer. Wait for the story to enter Backlog children flow.
- Inside another agent's workflow. Warden is invoked by humans or by
  the `/tick` / `/ticks` orchestrator, never by a role agent.
