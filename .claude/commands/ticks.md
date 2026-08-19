---
description: Drain the board — parallel devs via /tick-devs and parallel testers via /tick-tests, then sequential /tick for the rest, until idle.
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

Drain the board until **idle** (no priority rule fires) OR a hard cap
is reached. Four phases per pass:

- **Phase 0 — warden sweep.** Run `/tick-warden` once at the start of
  each pass. Cleans up state inconsistencies (status ↔ role mismatch,
  stuck roll-ups, stale `needs:rebase`, abandoned cards) so the
  parallel batches in Phase A see a coherent board. Auto-fixes are
  capped at 10 cards per sweep — anything beyond that, or anything
  ambiguous, is left tagged `needs:human`. Cheap when the board is
  healthy (one read-only scan).
- **Phase A1 — parallel devs.** Run `/tick-devs`: every
  `Backlog` / `ToDo` card with label `parallel:safe` and no open
  `depends-on:` blockers is dispatched in one batch (each in its own
  worktree under `C:\TEMP\agentic-worktrees\<repo>-task<N>`). Capped
  at `MAX_PARALLEL_DEVS` (default 4).
- **Phase A2 — parallel testers.** Then `/tick-tests`: every `Test`
  card with `role:tester` is dispatched in one batch (each in its own
  worktree `<repo>-pr<N>`). Removes the slowest individual step from
  the sequential loop.
- **Phase B — sequential rest.** Then apply the standard `/tick`
  priority order for everything else (ops, reviewer, sequential
  pickup of non-`parallel:safe` cards, analyst re-enters, story-claim)
  one action at a time. Same skip rules.

Analysts are NOT auto-batched here — `/tick-stories` is a separate,
explicit command (the human approval gate around decompositions makes
silent parallel analyst work undesirable as a default).

## Loop

Repeat:

1. **Phase 0 — warden sweep.** Invoke `/tick-warden`. Wait for it to
   return. Continue regardless of what it found — the warden does not
   block. If it reports `board healthy`, the parallel batches in
   Phase A see a clean board; if it auto-fixed a card, that card
   becomes eligible for Phase A or B in this same pass.
2. **Phase A1 — parallel dev drain.** Invoke `/tick-devs`. Wait for
   all dispatched devs to return. Each will have either opened a
   draft PR (`CodeReview` / `role:reviewer`) or stalled with
   `blocked:question`. If `/tick-devs` reports `nothing to do`, skip
   to Phase A2 without counting this as a pass.
3. **Phase A2 — parallel tester drain.** Invoke `/tick-tests`. Wait for
   all dispatched testers to return. Each will have either advanced
   its card to `Implemented` (`role:ops`) or kicked it back to
   `Progress` (`role:dev`). If `/tick-tests` reports `nothing to do`,
   skip to Phase B without counting this as a pass.
4. **Phase B — one sequential action.** Apply the `/tick` priority
   order from `.claude/commands/tick.md`, but **skip rule #2** (Test
   cards) since Phase A2 handled them. Sequential pickup of
   `parallel:safe` Backlog cards is also skipped — those went through
   Phase A1. Skip rule #10 (warden) — Phase 0 already ran. Take the
   FIRST other applicable action (ops, reviewer, sequential dev for
   non-`parallel:safe` cards, analyst, story claim).
5. If Phase B reported "board idle" AND Phase A1 + A2 did nothing AND
   Phase 0 reported `board healthy` — STOP. Print a short summary of
   what was done across all passes.
6. If no progress was made in two consecutive passes (same card stuck
   in the same column with the same labels, AND both Phase A batches
   produced no transitions, AND Phase 0 produced no fixes), STOP and
   report it as a **stall** with the card's URL — this is a bug or a
   blocking edge case that needs human eyes, not more loops.
7. **Hard cap: 30 passes per `/ticks` invocation.** If reached, STOP
   and report. Prevents runaway when something keeps re-queuing itself.

## Reporting

Print one line per phase while running. Phase 0 / A1 / A2 summarize
the batch on a single line; Phase B prints per-action lines as before:

    [pass 3 0]  tick-warden: 2 auto-fixed, 1 escalated
    [pass 3 A1] tick-devs:  4 dispatched → 3 CodeReview, 1 blocked:question (#62)
    [pass 3 A2] tick-tests: 3 dispatched → 2 Implemented, 1 → Progress (#54)
    [pass 3 B]  Implemented #42 → role:ops dispatched → merged
    [pass 4 0]  tick-warden: board healthy
    [pass 4 A1] tick-devs:  nothing to do
    [pass 4 A2] tick-tests: nothing to do
    [pass 4 B]  CodeReview #50 → role:reviewer dispatched → approved → Test
    [pass 5 0]  tick-warden: board healthy
    [pass 5 A1] tick-devs:  nothing to do
    [pass 5 A2] tick-tests: nothing to do
    [pass 5 B]  board idle

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
