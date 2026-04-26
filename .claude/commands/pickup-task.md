---
description: Claim the oldest Backlog issue, move it to ToDo, and hand it off to agent-dev.
---

Follow these steps exactly:

1. Using `gh` + `jq` as shown in `.claude/BOARD-OPS.md`, find the item with
   the **oldest** `createdAt` in the `Backlog` status column of project
   `$AGENTIC_PROJECT_NUMBER` (owner `$AGENTIC_PROJECT_OWNER`).

2. If no such item exists, report "Backlog empty" and stop.

3. Move that card from `Backlog` to `ToDo`.

4. Add label `role:dev` to the linked issue.

5. Invoke subagent `agent-dev` with the issue number as its input.
