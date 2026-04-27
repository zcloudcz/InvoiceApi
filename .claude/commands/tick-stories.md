---
description: Parallel analyst dispatch — process all eligible story cards (StoryNew + Analysis + Decomposed/approved) at once.
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Find every story-track card that is ready for `agent-analyst` and
dispatch them **all in parallel** in a single batch. Stories are
independent of each other — different issues, different conversations,
no shared state — so parallelism is safe here in a way it is NOT for
the task-track roles (dev/reviewer/tester/ops, where one PR has one
owner per phase).

This complements `/tick` (one card, sequential) and `/ticks` (drain,
sequential). Use it when you have multiple stories piling up and want
to wake up `agent-analyst` on all of them at once.

## Eligible cards

Three buckets, all dispatched to the same `agent-analyst` subagent
(the agent itself decides which step to execute based on the card's
column + labels):

1. **StoryNew, no `role:analyst` yet** — claim phase. Move card to
   `Analysis`, add `role:analyst` to the linked issue, then dispatch.
2. **Analysis, `role:analyst`, NOT `blocked:question`** — continue
   conversation phase (Step 2 of agent-analyst).
3. **Decomposed, `role:analyst` AND `analyst:approved`** — sub-issue
   materialization phase (Step 4 of agent-analyst).

## Skip rules (same as `/tick`)

- `Analysis` with `blocked:question` — waiting on a human answer.
- `Decomposed` without `analyst:approved` — waiting on human approval
  of the proposed decomposition.
- `Approved` cards — terminal.

## Algorithm

1. **Scan**: list all cards in `StoryNew`, `Analysis`, `Decomposed`
   from the project board (one `gh project item-list` call). Resolve
   each linked issue's labels to apply the eligibility rules above.
2. **Claim StoryNew items first, sequentially** (board moves are
   cheap, but two concurrent claims on the same item would race). For
   each StoryNew that has no `role:analyst`:
   - move card `StoryNew` → `Analysis`
   - add `role:analyst` label to the linked issue
   - record the issue number for batch dispatch
3. **Build the dispatch list** of issue numbers from buckets 1+2+3.
   If empty: print `no analyst-eligible stories — nothing to do` and
   STOP.
4. **Parallel dispatch**: in **one** Claude Code message, issue ONE
   `Task` tool call per story, all targeting the `agent-analyst`
   subagent. The Task `prompt` for each must be self-contained and
   include:
   - the story issue number
   - the repo (`<owner>/<name>`)
   - the current column + labels (so the agent knows which step it
     is in: claim, conversation, decomposition, or materialization)
   - reminder to read `MEMORY.md` and `BOARD-OPS.md` first

   Multiple `Task` calls in a single assistant message run **in
   parallel** in Claude Code — that is the parallelism.
5. **Aggregate results** when all subagents return. Each one will
   either:
   - have posted a question on the issue and added `blocked:question`
   - have posted a decomposition proposal on the issue (waits for
     human `analyst:approved`)
   - have materialized N sub-issues into Backlog
   Print a one-line summary per story.

## Caps

- **Max 8 parallel analysts per invocation.** More than that will
  start saturating the orchestrator's context window with their
  return summaries. If there are 12 eligible stories, do 8 now and
  print: `8 dispatched, 4 still queued — re-run /tick-stories`.
- If a single story has been parallel-dispatched 3 times in a row
  with the same `blocked:question` outcome, surface it as a stall.

## What this does NOT do

- Does NOT dispatch dev/reviewer/tester/ops in parallel. Those have
  per-PR ownership and harder coordination — use `/ticks` or per-PR
  slash commands.
- Does NOT advance the human-gated steps. Stories in `Decomposed`
  without `analyst:approved` stay there until you approve.
- Does NOT create stories. Story creation is still a human job
  (file a `type:story` issue, drop it into `StoryNew`).
