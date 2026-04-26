---
description: One orchestration pass — perform the first ready action on the board. Designed for /loop.
---

Scan the project board in the following priority order and take the FIRST
applicable action. Do exactly ONE action, then stop. If nothing is ready,
report "board idle" and stop.

Priority order (top first):

1. A card in `Implemented` with label `role:ops`
   -> invoke `agent-ops` with that PR number.

2. A card in `Test` with label `role:tester`
   -> invoke `agent-tester` with that PR number.

3. A card in `CodeReview` with label `role:reviewer`
   -> invoke `agent-reviewer` with that PR number.

4. A card in `ToDo` with label `role:dev`
   -> invoke `agent-dev` with that issue number.

5. A card in `Decomposed` with labels `role:analyst` AND `analyst:approved`
   -> invoke `agent-analyst` with that story issue number (Step 4 — materialize sub-issues).

6. A card in `Analysis` with label `role:analyst` and WITHOUT `blocked:question`
   -> invoke `agent-analyst` with that story issue number (Step 2 — continue conversation).

7. No card is in `ToDo`/`Progress`/`CodeReview`/`Test`/`Implemented` awaiting
   a role AND the `Backlog` column is non-empty
   -> run `/pickup-task`.

8. No story currently carries the `role:analyst` label AND the `StoryNew`
   column is non-empty
   -> claim the oldest item in `StoryNew`: move it to `Analysis`, add
      label `role:analyst` to the linked issue, then invoke `agent-analyst`
      with that story issue number.

   (A story that is in `Decomposed` without `role:analyst` is no longer
   "in flight" for the analyst — `agent-analyst` finished its work and
   the story is just waiting for child PRs to merge. Picking up a new
   story in parallel is fine.)

Skip rules:

- Cards in `Blocked` (label `blocked:question`) are skipped — a human
  must answer and re-queue them. Same applies to stories in `Analysis`
  with `blocked:question`.
- Cards in `Decomposed` WITHOUT `analyst:approved` are skipped — they are
  waiting on the human to approve the proposed decomposition.
- Cards in `Approved` are terminal — the human's final acceptance.
  Never act on them.

Oldest-first within each priority level (by card `createdAt`).
