---
description: One orchestration pass — perform the first ready action on the board. Designed for /loop.
---

## Preflight — gh scopes

Before any board read, verify gh has the `project` scope. If it does
not, every `gh project` call below returns 401 with a misleading
"missing required scopes [read:project]" error.

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If the check fails, STOP — surface the exact command above and do not
continue. Do not retry board reads "in case it works" — they will not.

## Orchestration

Scan the project board in the following priority order and take the FIRST
applicable action. Do exactly ONE action, then stop. If nothing is ready,
report "board idle" and stop.

Priority order (top first):

1. A card in `Implemented` with label `role:ops`
   -> invoke `agent-ops` with that PR number.

2. A card in `Test` with label `role:tester`
   -> invoke `agent-tester` with that PR number.

3. A card in `CodeReview` with label `role:reviewer`
   -> dispatch the reviewer role for that PR number. Use
      `subagent_type: "hydra"` with `.claude/agents/agent-reviewer.md`
      as the instruction set, not `subagent_type: "agent-reviewer"`
      (see "Role runners" in BOARD-OPS.md).

4. A card in `Progress` with label `role:dev`
   -> invoke `agent-dev` with that issue number (re-dispatch — review or
      test kickback on an existing PR; drain WIP before starting new work).

5. A card in `ToDo` with label `role:dev`
   -> invoke `agent-dev` with that issue number (fresh implementation).

6. A card in `Decomposed` with labels `role:analyst` AND `analyst:approved`
   -> invoke `agent-analyst` with that story issue number (Step 4 — materialize sub-issues).

7. A card in `Analysis` with label `role:analyst` and WITHOUT `blocked:question`
   -> invoke `agent-analyst` with that story issue number (Step 2 — continue conversation).

8. No card is in `ToDo`/`Progress`/`CodeReview`/`Test`/`Implemented` awaiting
   a role AND the `Backlog` column is non-empty
   -> run `/pickup-task`.

9. No story currently carries the `role:analyst` label AND the `StoryNew`
   column is non-empty
   -> claim the oldest item in `StoryNew`: move it to `Analysis`, add
      label `role:analyst` to the linked issue, then invoke `agent-analyst`
      with that story issue number.

   (A story that is in `Decomposed` without `role:analyst` is no longer
   "in flight" for the analyst — `agent-analyst` finished its work and
   the story is just waiting for child PRs to merge. Picking up a new
   story in parallel is fine.)

10. Nothing above fired AND there is at least one card in
    `ToDo`/`Progress`/`CodeReview`/`Test`/`Implemented`/`Analysis`/`Decomposed`
    -> run `/tick-warden`. Warden sweeps the board for state
       inconsistencies (status ↔ role mismatch, PR state drift, stuck
       roll-ups, stale WIP). If warden auto-fixes any card, the next
       `/tick` pass will pick it up via rules 1-9. If warden reports
       `board healthy`, treat this tick as idle.

    This is the lowest priority — only run when no role agent has
    work to do. On a healthy busy board it never fires. On a stuck
    board it unblocks role agents that would otherwise idle forever.

For parallel processing of multiple `Test` cards in one batch (each in
its own git worktree), use `/tick-tests` instead of running `/tick` in
a loop. Same outcomes, no waiting between testers. `/ticks` invokes
`/tick-tests` automatically as Phase A of each pass.

Skip rules:

- Cards in `Blocked` (label `blocked:question`) are skipped — a human
  must answer and re-queue them. Same applies to stories in `Analysis`
  with `blocked:question`.
- Cards in `Decomposed` WITHOUT `analyst:approved` are skipped — they are
  waiting on the human to approve the proposed decomposition.
- Cards in `Approved` are terminal — the human's final acceptance.
  Never act on them.

Within each priority level: `type:bug` cards first, then oldest-first by
`createdAt`. See "Task priority — bugs jump the queue" in BOARD-OPS.md.
