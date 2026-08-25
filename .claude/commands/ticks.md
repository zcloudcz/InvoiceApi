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

**Focus gate — applies to every phase below.** Only cards carrying a
`theme:*` label named in `$AGENTIC_FOCUS`, or the label
`focus:override`, are eligible. Parked cards are skipped silently: do
not move, close or relabel them, and do not count them as blocked. Among
eligible cards, theme order in `$AGENTIC_FOCUS` outranks `type:bug` and
age. See BOARD-OPS.md -> "Focus". Without this gate a drain finishes
work nobody asked for — on 2026-08-22 it produced 19 new issues, of
which one was on an active priority.

Drain the board until **idle** (no priority rule fires) OR a hard cap
is reached. Keep going while there is *any* actionable **eligible** card — do not
stop because a pass was cheap, because the queue looks long, or because
the same role fired several passes in a row. Four phases per pass:

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
  at `MAX_PARALLEL_DEVS` (default 3).
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

   **Reviewer batching.** One action per pass is the rule, with one
   exception: when **4 or more** cards sit in `CodeReview` with
   `role:reviewer`, dispatch reviewers as a parallel batch instead of
   one per pass, capped at `MAX_PARALLEL_REVIEWERS` (default 3).
   Reviews are the deepest queue on a wide board and are not cheap
   here — a single review runs a trial merge plus a full build and test
   suite and takes ~15 minutes, so serialising eight of them costs two
   hours of wall-clock for work that shares no state. Distinct PRs,
   distinct worktrees, read-only on source: batching them is safe.
   Below the threshold, stay sequential — the batch only pays off when
   the queue is actually deep.

   This exception is for **reviewers only**. `agent-ops` is never
   dispatched twice at once: merges mutate the integration branch, and
   two concurrent merges race.

   That is a limit on parallelism, not on throughput. When several PRs
   carry `role:ops`, dispatch **one** ops agent and hand it the ordered
   list — it drains the whole queue sequentially inside a single run
   (see "Drain the whole queue" in `.claude/agents/agent-ops.md`). One
   ops dispatch per pass, however many PRs are waiting.

   **GraphQL budget — the real ceiling on parallelism.** `gh project`
   runs on GitHub's GraphQL API, which has its own 5000-point/hour quota
   separate from REST. A single `gh project item-list --limit 300` is
   expensive, and every agent re-reads the board several times, so a
   batch of 6 can exhaust the hourly quota in well under an hour.
   Observed on 2026-08-20: 4989/5000 consumed with six agents in flight.

   When it runs out, `gh project` calls fail with `API rate limit
   exceeded` while `gh issue` / `gh pr` keep working — so agents finish
   their code work but silently fail their board transitions, leaving
   cards stranded in the wrong column.

   Therefore: check `gh api rate_limit --jq '.resources.graphql'` before
   dispatching a batch. Below ~1500 remaining, drop to sequential or
   wait for the reset. After any batch that hit the limit, run
   `/tick-warden` once the quota recovers — reconciling stranded cards
   is exactly what it is for.
4b. **Phase R — retrospective, once, when the drain ends.** Not per pass.
   When the loop is about to stop (idle, stall, or hard cap), invoke
   `/retro` before printing the completion summary. It reads the whole
   run — kickback reasons, rework from parallelism, misattributed causes,
   warden's escalation trail, infrastructure losses — and turns the
   **recurring** ones into edits where the relevant role will actually
   read them, plus one `## Běh <date>` section in `.claude/FLOW-NOTES.md`.

   Skip it only if the drain did nothing at all (Phase 0/A1/A2/B all idle
   on the first pass) — there is no run to reflect on. A short retro after
   a clean drain is a correct outcome; do not skip it just because the
   iteration went well.

5. If Phase B reported "board idle" AND Phase A1 + A2 did nothing AND
   Phase 0 reported `board healthy` — STOP. Print the completion
   summary described under "Done — what it means" below.
6. If no progress was made in two consecutive passes, STOP and report
   it as a **stall** with the card's URL — this is a bug or a blocking
   edge case that needs human eyes, not more loops.

   "No progress" is judged against a snapshot you must actually keep:
   at the end of every pass, record `(issue number, status, sorted
   labels)` for every non-`Approved` card. Two consecutive passes whose
   snapshots are identical, with both Phase A batches empty and Phase 0
   reporting no fixes, is a stall. Without the snapshot this rule is
   unenforceable — do not claim it fired if you were not keeping one.
7. **Hard cap: 200 passes per `/ticks` invocation.** If reached, STOP
   and report. This is a runaway backstop, not a work budget: Phase B
   performs ONE action per pass, and a single card typically needs four
   (dev → review → test → ops), so a 20-card backlog legitimately costs
   ~80 passes plus whatever Phase A parallelises. A cap that bites
   before the board drains turns a normal run into a false "stopped
   early".

## Done — what it means

`/ticks` **cannot** move a card to `Approved`. `Implemented → Approved`
happens only in `/release-prod` (TEST-ENV → master), which a human
triggers; `/ticks` never touches `TEST-ENV` or `master`. So "drained"
here means:

> every **eligible** card is either in `Implemented` (waiting for the
> next `/release`), in `Approved` already, or parked on a human.

Cards outside the active themes are **not** part of "drained" and must
not keep the loop running. The final summary states how many were
skipped and under which themes, so the owner can see what the focus
setting excluded rather than having to guess.

Cards parked on a human are **not** failures and must not keep the loop
spinning — they are the skip rules: `Blocked` / `blocked:question`,
`Decomposed` without `analyst:approved`, and anything carrying
`needs:human`.

But they are also not invisible. The final summary MUST list, by issue
number and with the reason:

- cards sitting in `Implemented`, i.e. what the next `/release` would
  promote to the test environment (and `/release-prod` to production);
- cards parked on a human, and what each is waiting for;
- cards that ended in `Blocked` during this run.

Reporting "board idle" without that breakdown reads as "everything is
finished" when it is not.

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
