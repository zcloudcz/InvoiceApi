---
description: Parallel developer dispatch — process all eligible parallel:safe Backlog/ToDo tasks at once via per-task git worktrees.
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Find every task-track card sitting in `Backlog` or `ToDo` with label
`parallel:safe` and dispatch them **in parallel** in a single batch.
Each dev runs in its own git worktree under
`C:\TEMP\agentic-worktrees\<repo>-task<N>` (see `BOARD-OPS.md` →
"Worktree isolation"), so they do not fight over the main checkout and
do not block each other.

This complements:

- `/tick` (one card, sequential, all roles)
- `/tick-tests` (parallel testers on `Test` cards)
- `/tick-stories` (parallel analysts on `StoryNew` / `Analysis`)
- `/ticks` (drain — runs phases in order)

Use it when several independent backlog items are ready and you want
implementation work to start on all of them at once.

## Why parallel devs are safe (with caveats)

Each parallel dev has:

- its own task issue (one card, one PR target)
- its own feature branch `feature/issue-<N>-<slug>`
- its own worktree directory `C:/TEMP/agentic-worktrees/<repo>-task<N>`
- its own remote PR after Step 2a opens it

Race surfaces:

- **File overlap with another in-flight PR.** Not pre-checked. Resolved
  reactively via the rebase loop: `agent-ops` detects merge conflict at
  merge time, sets `needs:rebase`, kicks back to `agent-dev` Step 2c.
- **Semantic conflict** (one PR refactors API another consumes). Not
  detected here. Falls out in tests / review or in `dev:blocked` during
  a non-trivial rebase.
- **MEMORY.md writes.** Each parallel dev appends one short line on
  handoff. Conflicts rare and recoverable.

## Eligible cards

Both buckets, in priority order:

1. Cards in `Backlog` with label `parallel:safe`, no `role:*` label
   yet, and no open `depends-on:` blockers.
2. Cards in `ToDo` with label `parallel:safe` and `role:dev`, that are
   NOT also labeled `needs:rebase` (those go through `/tick` /
   sequential dev — they are mid-PR, not eligible for the parallel
   batch).

A card with `role:dev` and `needs:rebase` is handled by `/tick`
priority for re-dispatch, not by this command.

## Skip rules

- Without `parallel:safe` — sequential pickup via `/pickup-task` /
  `/tick`.
- With `blocked:question` — waiting on a human.
- With `depends-on: #M` where #M is still open — wait until #M closes.
  Resolve `depends-on:` lines from the issue body:

      gh issue view <N> --json body --jq '.body' \
        | grep -oP 'depends-on:\s*#\K[0-9]+' \
        | while read DEP; do
            gh issue view "$DEP" --json state --jq '.state'
          done

  Skip the card if any dep is `OPEN`.
- Already in `Progress` / `CodeReview` / `Test` — out of scope.
- `type:story` — analysts handle stories.

## Algorithm

1. **Scan**: one `gh project item-list` call, filter to `Backlog` and
   `ToDo` columns. For each item resolve labels and (for `Backlog`)
   the `depends-on:` lines from the issue body.
2. **Build the dispatch list** of issue numbers passing the
   eligibility rules. Sort with `type:bug` first, then oldest-first by
   `createdAt` (per "Task priority — bugs jump the queue" in
   BOARD-OPS.md). If empty:

       echo "no parallel:safe tasks ready — nothing to do"

   STOP.
3. **Cap to `MAX_PARALLEL_DEVS`** (env var, default 4). If more
   eligible cards exist than the cap, dispatch the top of the sorted
   list and print:

       N dispatched, M still queued — re-run /tick-devs after they merge

4. **Move cards `Backlog` -> `ToDo`** for any not yet there, add
   `role:dev`. Idempotent.
5. **Parallel dispatch**: in **one** Claude Code message, issue ONE
   `Task` tool call per task, all targeting the `agent-dev` subagent.
   Each prompt must be self-contained and include:
   - the issue number
   - the repo (`<owner>/<name>`)
   - the explicit string `dispatch-mode: parallel` so `agent-dev`
     Step 2a uses the worktree pattern, not the main checkout
   - reminder to read `MEMORY.md`, `BOARD-OPS.md`, and `AGENT-RULES.md`
     first
   - reminder that **all work happens in the per-task worktree**
     (`C:/TEMP/agentic-worktrees/<repo>-task<N>`)

   Multiple `Task` calls in a single assistant message run **in
   parallel** in Claude Code — that is the parallelism.
6. **Aggregate results** when all subagents return. Each one will
   either:
   - have opened a draft PR and moved its card to `CodeReview`
     (`role:reviewer`)
   - have stalled with `blocked:question` (missing acceptance criteria
     or design call)
   Print a one-line summary per task.

## Caps

- **`MAX_PARALLEL_DEVS = 4`** by default. Override in `.claude/settings.json`
  env block. Each dev runs the repo's full local build/test, which is
  heavier than a tester worktree (tester only runs the test command;
  dev does build + test + edit cycles).
- If a card has been parallel-dispatched 2 times in a row and ended in
  `blocked:question` both times, surface as a stall — the issue itself
  needs sharper acceptance criteria, more dispatches will not help.
- No retry on `dev:blocked` (escalated rebase failure). That label
  means the dev already gave up; `/tick-devs` does not pick those up.

## What this does NOT do

- Does NOT pre-flight check file overlap between in-flight PRs. Conflict
  resolution is reactive, in `agent-dev` Step 2c.
- Does NOT dispatch reviewers / testers / ops in parallel. Use
  `/tick-tests` for testers; reviewers and ops are cheap and stay
  sequential in `/tick`.
- Does NOT pick cards out of `Progress`, `CodeReview`, `Test`, or
  `Implemented`. Only `Backlog` and (eligible) `ToDo`.
- Does NOT clean up stale worktrees from previous runs — `agent-dev`
  Step 2a end-of-flow owns cleanup. If you need to garbage-collect
  manually, run `git worktree prune` inside the repo and `rm -rf`
  orphans under `C:\TEMP\agentic-worktrees\` that no longer have a
  matching open issue or PR.
