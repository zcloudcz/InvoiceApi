---
description: Parallel tester dispatch — process all eligible Test cards at once via per-PR git worktrees.
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Find every task-track card sitting in `Test` with `role:tester` and
dispatch them **all in parallel** in a single batch. Each tester runs
in its own git worktree under `C:\TEMP\agentic-worktrees\<repo>-pr<N>`
(see `BOARD-OPS.md` → "Worktree isolation for testers"), so they do not
fight over the main checkout and do not block `agent-dev`.

This complements `/tick` (one card, sequential), `/ticks` (drain,
sequential rest after testers) and `/tick-stories` (parallel analysts).
Use it when several PRs are queued in `Test` and you want CI coverage
work to start on all of them at once.

## Why testers are parallel-safe

Each PR has:

- its own feature branch (`gh pr view <PR> --json headRefName`)
- its own worktree directory (`C:/TEMP/agentic-worktrees/<repo>-pr<PR>`)
- its own `MEMORY.md` lines (single repo, but tester only appends its
  own progress line — orderings interleave but do not corrupt)
- its own remote PR for `gh pr checks --watch` and push

No two testers touch the same branch or the same worktree. Race
surfaces left:

- `MEMORY.md` writes from concurrent testers in the same repo. Mitigate
  by appending atomically (single short line per agent) and by reading
  immediately before writing. Conflicts are rare and recoverable —
  worst case is a re-edit by a human.
- Board moves on the same item. Not possible in practice — each tester
  owns one card.

## Eligible cards

One bucket only:

- Cards in `Test` with label `role:tester` and WITHOUT `blocked:question`.

## Skip rules

- `Test` with `blocked:question` — waiting on a human.
- `Test` without `role:tester` — not yet claimed; `/tick` will hand it
  off (or this command was raced by another tick).
- Anything not in the `Test` column.

## Algorithm

1. **Scan**: one `gh project item-list` call, filter to `Test` column.
   For each item, resolve the linked PR and check labels.
2. **Build the dispatch list** of PR numbers passing the eligibility
   rules. If empty: print `no tester-eligible PRs — nothing to do`
   and STOP.
3. **Parallel dispatch**: in **one** Claude Code message, issue ONE
   `Task` tool call per PR, all targeting the `agent-tester` subagent.
   Each `Task` `prompt` must be self-contained and include:
   - the PR number
   - the repo (`<owner>/<name>`)
   - the linked issue number (so MEMORY.md updates target the right
     task)
   - reminder to read `MEMORY.md`, `BOARD-OPS.md`, and `AGENT-RULES.md`
     first
   - reminder that **all work happens in the per-PR worktree**, not in
     the main checkout (Step 0 of `agent-tester`)

   Multiple `Task` calls in a single assistant message run **in
   parallel** in Claude Code — that is the parallelism.
4. **Aggregate results** when all subagents return. Each one will
   either:
   - have pushed test commits and watched CI to green → moved card to
     `Implemented`, swapped label to `role:ops`
   - have flagged an implementation bug → moved card back to
     `Progress`, swapped label to `role:dev`
   - have stalled on a CI failure or environmental issue → reported
     the PR URL
   Print a one-line summary per PR.

## Caps

- **Max 6 parallel testers per invocation.** Each tester runs the
  repo's full test suite, which is heavier than an analyst Q&A turn.
  Six is the upper bound on disk + CPU saturation on a typical
  developer box; lower it via the env var `AGENTIC_TICK_TESTS_MAX`
  if needed. If there are more eligible PRs, dispatch the cap now
  and print: `6 dispatched, N still queued — re-run /tick-tests`.
  When more eligible PRs than the cap exist, sort the dispatch list
  with `type:bug` PRs first, then oldest-first by card `createdAt`,
  per "Task priority — bugs jump the queue" in BOARD-OPS.md.
- If a single PR has been parallel-dispatched 3 times in a row with
  the same outcome (CI red, same failing check), surface it as a
  stall — pinging the same tester at it will not help.

## What this does NOT do

- Does NOT dispatch dev/reviewer/ops in parallel. Dev edits the main
  checkout; reviewer and ops have one PR each at a time and are cheap
  enough to leave sequential in `/ticks`.
- Does NOT pick cards out of `CodeReview` or `Backlog`. Only `Test`.
- Does NOT clean up stale worktrees from previous runs — `agent-tester`
  Step 4 owns cleanup. If you need to garbage-collect manually, run
  `git worktree prune` inside the repo and `rm -rf` orphans under
  `C:\TEMP\agentic-worktrees\` that no longer have a matching open PR.
