---
description: Drain the board — repeat /tick sequentially until idle.
---

## Preflight — gh scopes

Before any board read, verify gh has the `project` scope. If it does
not, every `gh project` call below returns 401.

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If the check fails, STOP — surface the exact command above.

## What this does

Repeatedly run the same priority logic as `/tick` (one action per pass)
until the board is **idle** (no priority rule fires) OR a hard cap is
reached. Same priority order, same skip rules, same single-action-per-
pass discipline. The only difference vs `/tick` is the outer loop.

This is intentionally **sequential**, not parallel. Use `/tick-stories`
when you want analysts dispatched in parallel.

## Loop

Repeat:

1. Apply the `/tick` priority order from `.claude/commands/tick.md`.
   Take the FIRST applicable action. After it completes (the dispatched
   role-agent returns or a board move finishes), continue to step 2.
2. If the previous pass reported "board idle" — STOP. Print a short
   summary of what was done across all passes.
3. If no progress was made in two consecutive passes (same card stuck
   in the same column with the same labels), STOP and report it as a
   **stall** with the card's URL — this is a bug or a blocking edge
   case that needs human eyes, not more loops.
4. **Hard cap: 30 passes per `/ticks` invocation.** If reached, STOP
   and report. Prevents runaway when something keeps re-queuing itself.

## Reporting

Print one line per pass while running:

    [pass 3] Implemented #42 → role:ops dispatched → merged
    [pass 4] Test #51 → role:tester dispatched → CI green → Implemented
    [pass 5] board idle

Final summary: how many cards advanced, which ended in `Blocked`, which
need human attention, total wall-clock.

## When NOT to use

- During active human review of a `Decomposed` story — `/ticks` will not
  touch it (skip rule covers `analyst:approved` gating), but it will
  process unrelated items in parallel-in-time which may be confusing if
  you wanted everything paused.
- When you want one specific role to work through a backlog without
  touching others — use the role-specific slash command directly
  (`/pickup-task`, `/review N`, etc.) in a `/loop`.
