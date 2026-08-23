# Board operations — shared reference for all agents

The GitHub Project (v2) board is the source of truth for workflow state.
All agents use the commands below via `gh` + `jq`. Prefer these patterns
over inventing new ones.

Environment variables (from `.claude/settings.json`):

- `$AGENTIC_PROJECT_NUMBER`     — the project's numeric ID (from its URL)
- `$AGENTIC_PROJECT_OWNER`      — `@me` or an org login
- `$AGENTIC_INTEGRATION_BRANCH` — branch where feature PRs are merged
                                  (default `develop`); see "Integration
                                  branch model" below
- `$AGENTIC_AUTO_MERGE`         — `"true"` lets agent-ops merge feature
                                  PRs without human approval; default
                                  `"false"`

## Scheduled tick logs (Windows)

When `/tick` runs unattended via Windows Task Scheduler (registered via
`.claude/scripts/Register-AgenticTick.ps1`), each run appends timestamped
output to:

    %LOCALAPPDATA%\AgenticTeam\<repo-slug>.log

`<repo-slug>` is the absolute repo path with `\`, `/`, `:` replaced by `_`.
A `.lock` file in the same directory holds the PID of the currently
running tick (if any). When the user asks why a card has not advanced,
or what the last automated tick did, grep this log file for the most
recent `START` / `END` block.

## Focus — themes, and priority between them

`priority:*` ranks one card against another. It says nothing about
whether the card is part of what the project is trying to finish right
now, so a board drain will happily spend a day on high-priority work
nobody asked for. That happened on 2026-08-22: of 19 issues opened
during a drain, one belonged to an active priority.

**A theme is a `theme:*` label.** Active themes and their relative
priority are one ordered list in `.claude/settings.json`:

    "AGENTIC_FOCUS": "db-switch,ai-first"

Earlier in the list wins. A theme that is not listed is **parked** — its
cards stay on the board and stay visible, they are simply not pulled.

### Eligibility

A card may be picked up by `/tick`, `/ticks`, `/tick-devs`,
`/tick-tests` or `/pickup-task` only if it carries either:

- a `theme:*` label naming an active theme, **or**
- the label `focus:override` — a deliberate, human-approved exception
  for something outside the themes that still has to be done now.

Everything else is skipped. This is a **skip rule, not a status**: do
not move, close or relabel a parked card, and do not report it as
blocked. It is simply not this week's work.

### Ordering among eligible cards

1. Theme position in `AGENTIC_FOCUS` (earlier first). `focus:override`
   sorts after all active themes — it is an exception, not a priority.
2. Then the existing rules below: `type:bug` first, then oldest by
   `createdAt`.

### Who assigns a theme

`agent-analyst` labels every sub-issue it creates with the parent
story's theme. A standalone bug filed mid-flight has no parent, so it
gets a theme only if it plainly belongs to one; otherwise it stays
unthemed and parked until a human decides. **Agents do not add
`focus:override` to their own findings** — that label is the owner's
call, and letting an agent grant itself an exception would restore
exactly the drift this rule exists to stop.

## Task priority — bugs jump the queue

`type:bug` outranks every other priority signal. Whenever multiple cards
are eligible for the same action (same status column, same role label,
same `/tick` priority level), pick the `type:bug` card first regardless
of `createdAt` or `priority:*`. Within `type:bug` cards, oldest-first;
within non-bug cards, oldest-first. `priority:high` only breaks ties
*after* `type:bug` has been applied.

Applies to: `/pickup-task`, `/tick`, `/tick-tests`, `/tick-stories`, and
any agent that picks "the next" item from a column. A `type:bug` story
in `StoryNew` is also claimed first by the analyst.

## Find the oldest item in a given status column (bugs first)

    gh project item-list "$AGENTIC_PROJECT_NUMBER" \
        --owner "$AGENTIC_PROJECT_OWNER" --format json --limit 200 \
      | jq -r --arg S "Backlog" '
          .items
          | map(select(.status == $S))
          | sort_by([(.labels | index("type:bug") | not), .createdAt])
          | .[0] // empty'

The compound sort key puts `type:bug` cards (where `index("type:bug")`
is non-null, so `| not` is `false`) ahead of non-bug cards, then
`createdAt` ascending within each group.

## Resolve the IDs needed to move a card

Project node id + Status field id + option ids (one-time per session;
cache in shell variables):

    gh project view "$AGENTIC_PROJECT_NUMBER" \
        --owner "$AGENTIC_PROJECT_OWNER" --format json \
      | jq -r '.id'                                  # PROJECT_NODE_ID

    gh project field-list "$AGENTIC_PROJECT_NUMBER" \
        --owner "$AGENTIC_PROJECT_OWNER" --format json \
      | jq -r '.fields[] | select(.name=="Status") | .id'   # STATUS_FIELD_ID

    gh project field-list "$AGENTIC_PROJECT_NUMBER" \
        --owner "$AGENTIC_PROJECT_OWNER" --format json \
      | jq -r '.fields[]
               | select(.name=="Status").options[]
               | "\(.name)\t\(.id)"'                 # option name -> option id

## Move a card to a new Status

    gh project item-edit \
      --id "$ITEM_ID" \
      --project-id "$PROJECT_NODE_ID" \
      --field-id   "$STATUS_FIELD_ID" \
      --single-select-option-id "$OPTION_ID"

If `gh project item-edit` feels clunky, the equivalent GraphQL mutation is
`updateProjectV2ItemFieldValue` — both are fine.

## Swap role labels on the linked issue / PR

Role labels are how agents know whose turn it is. Always swap atomically:

    gh issue edit <N> --add-label "role:reviewer" --remove-label "role:dev"
    gh pr    edit <PR> --add-label "role:tester"  --remove-label "role:reviewer"

## Sub-issues (parent user story <-> child task)

The story flow uses GitHub's native sub-issues relation. Every task that
`agent-analyst` produces is linked as a sub-issue of the parent story.

Two surfaces are supported. **Prefer the github MCP plugin** when it is
installed in the local Claude Code config (cleaner API, returns full
issue objects in one call). **Fall back to `gh api`** otherwise — works
in any target repo, regardless of plugin availability.

### List sub-issues of a story

    mcp__plugin_github_github__issue_read
      method: "get_sub_issues", owner: "$OWNER", repo: "$REPO",
      issue_number: <S>, perPage: 100
    # Fallback: gh api repos/:owner/:repo/issues/<S>/sub_issues

### Count still-open children

Filter `get_sub_issues` response for `state == "open"`, take length.
Fallback: `gh api repos/:owner/:repo/issues/<S>/sub_issues --jq '[.[] | select(.state=="open")] | length'`

### Find the parent of an issue

Read `Parent story:` line from `MEMORY.md`. Fallback: `gh api repos/:owner/:repo/issues/<N>/parent_issue --jq '.number'`

### Add a child to a parent

    mcp__plugin_github_github__sub_issue_write
      method: "add", owner: "$OWNER", repo: "$REPO",
      issue_number: <S>, sub_issue_id: <CHILD_ID>

`sub_issue_id` is the **internal numeric `id`** (not the issue number).
Fallback: `gh api -X POST repos/:owner/:repo/issues/<S>/sub_issues -F sub_issue_id="$CHILD_ID"` (typed `-F` required — string `-f` gets 422 "not of type integer")

## Worktree isolation → see agent-dev Step 2a/2c and agent-tester Step 0/4

## Parallel-dev labels → see agent-analyst Step 3 and AGENT-RULES §7

## MEMORY.md format

Every target repo keeps working memory at `MEMORY.md` in its root. Agents
read it in Step 0 and update it at each handoff. Use this structure
exactly — do not invent alternates. Keep it compact and trim as work
progresses.

    # Working memory

    ## Current task
    Issue #<N> — "<one-line goal>"
    PR: #<PR> (<draft|open|merged>)
    Parent story: #<S> (or "none")

    ## Plan
    1. <step>
    2. <step>

    ## Progress
    - [x] agent-dev: <what it did>
    - [ ] agent-reviewer
    - [ ] agent-tester
    - [ ] agent-ops

    ## Open questions
    (none) — or numbered list with who is waiting on whom

    ## Next step
    <the one concrete action to take on resuming>

    ## Last updated
    <ISO date> by <agent-name>

When the active work item is a user story being analyzed (not yet
decomposed), `## Current task` uses the story number with no PR:

    Issue #<S> — "<story title>" [type:story]
    PR: none
    Parent story: self

Rules:

- Update on handoffs (role swap) and on blocks / failures. Do NOT update
  after every tool call.
- When a task is merged / closed, agent-ops clears the content under each
  heading (leaves the headings). Next task starts on a clean slate.
- Same language as the repo's code comments (per root `CLAUDE.md`).
- If `MEMORY.md` does not exist when you first need it, create it with
  these headings populated for the current task.

## Verdict markers — the load-bearing first lines

Every role verdict is a **fixed first line** on a PR comment or review.
Those lines are the only machine-readable record of the review and test
gates: `role:*` labels are also set by pickup and by warden, board moves
fail whenever the GraphQL quota is out, and on a single-account repo
GitHub refuses formal `APPROVE` / `REQUEST_CHANGES`, so `reviewDecision`
and `state=="CHANGES_REQUESTED"` are permanently useless here.

Reword the rest of the body freely. Never the first line.

| Marker (exact first line) | Posted by | Where |
|---|---|---|
| `AgentReviewer verdict: APPROVED` | agent-reviewer | `gh pr review --comment` |
| `AgentReviewer verdict: CHANGES REQUESTED` | agent-reviewer | `gh pr review --comment` |
| `AgentTester verdict: PASS` | agent-tester | `gh pr comment` |
| `AgentTester kickback: implementation` | agent-tester | `gh pr comment` |

**The collection is part of the convention.** Reviews
(`pulls/<PR>/reviews`) and issue comments (`issues/<PR>/comments`) are
separate collections and neither query sees the other. Reviewer markers
live in reviews, tester markers in comments — always. On 2026-08-23 the
reviewer used both at random (reviews on #244/#256/#258/#259/#273/#277/
#280/#284, comments on #246/#260/#278/#281) and ops looking in the wrong
one on #260 nearly read a merged approval as missing.

Counters — use these, do not invent a variant:

    KICKBACK_COUNT=$(gh api "repos/:owner/:repo/pulls/${PR}/reviews" --paginate \
      --jq '[.[] | select(.body | startswith("AgentReviewer verdict: CHANGES REQUESTED"))] | length')

    TESTER_KICKBACKS=$(gh api "repos/:owner/:repo/issues/${PR}/comments" \
      --paginate \
      --jq '[.[] | select(.body | startswith("AgentTester kickback: implementation"))] | length')

Both counters cover the 2nd-round diagnostic and the 3rd-round
escalation summary too — those carry the same first line.

## Counting rebase rounds — one canonical query

`agent-dev`, `agent-ops` and `agent-warden` all gate on "has this PR
been through the rebase loop twice already?". They must count the same
thing, or one of them escalates while another keeps looping.

The count is **how many times the `needs:rebase` label has been applied**
to the PR. `agent-ops` Step 1b applies it on every kickback, so the
label-event log is an exact, wording-independent record:

    REBASE_ROUNDS=$(gh api "repos/:owner/:repo/issues/${PR}/events" \
      --paginate \
      --jq '[.[] | select(.event=="labeled" and .label.name=="needs:rebase")] | length')

Threshold, identical for all three roles: `REBASE_ROUNDS >= 2` means the
loop has run twice and must not run a third time — escalate with
`dev:blocked` + `needs:human` instead.

Never count comment bodies for this. Comment wording drifts; a reworded
template silently zeroes the counter and the escalation never fires.

## Role runners — which subagent_type actually executes a role

Most roles are dispatched as themselves: `subagent_type: "agent-dev"`,
`"agent-tester"`, `"agent-ops"`, `"agent-analyst"`, `"agent-warden"`.

**`agent-reviewer` is the exception.** It carries no `model:` in its
frontmatter because it does not run on its own. Dispatch it as:

    Agent(subagent_type: "hydra",
          prompt: "<contents of .claude/agents/agent-reviewer.md as your
                   instruction set> ... review PR #<PR>")

`hydra` is a user-global agent that delegates the actual review to the
Codex plugin and filters its feedback before reporting. The model comes
from hydra's own definition.

A caller that dispatches `subagent_type: "agent-reviewer"` literally
still works, but silently bypasses the Codex second opinion — which is
the whole point of the reviewer role. Every dispatch site must use the
form above.

## Review gate on single-account repos

GitHub refuses a PR approval from the PR's own author, so with one
account `reviewDecision == "APPROVED"` is impossible and the markers
above carry the gate instead. If the repo ever gains a second
(bot/machine) review account, drop the convention and require the
formal approval again.

Autonomy: with `AGENTIC_AUTO_MERGE=true`, agents act on passed gates
without asking for extra confirmation — the env flags in
`.claude/settings.json` ARE the human authorization. Agents ask only
when a gate genuinely fails or a rule conflict has no defined path.

## Integration branch model

AgenticTeam uses two long-lived branches:

- `master` — release branch. Stable, deployable, what a fresh `git
  clone` gets. Updated only by the `/release` slash command.
- `$AGENTIC_INTEGRATION_BRANCH` (default `develop`) — integration
  branch. Where feature PRs land. Cards in `Implemented` are sitting
  here, waiting to be released.

Lifecycle of a feature:

    feature/issue-N-foo  ── PR ──▶  develop  ── /release PR ──▶  master
        ↑                              ↑                            ↑
        agent-dev                      agent-ops merges               human triggers
        branches off develop           (squash, --base develop)     /release; Implemented
                                                                    cards batch-move to
                                                                    Approved

Column meanings on the board:

- `Implemented`   feature PR is merged into the integration branch
                  (develop). Issue is closed. Code is integrated but
                  not yet released.
- `Approved`      release happened — the develop→master PR was merged
                  and `/release` (or the user) batch-moved cards from
                  Implemented to Approved.

Merge styles:

- feature PR → develop  : **squash** (one commit per feature on develop)
- develop PR → master   : **merge commit** (preserves the squashed
                          feature commits in master's history; release
                          shows up as a single readable rollup)

Legacy / migration:

- If the integration branch does not exist on origin (existing repo
  predating this convention), `agent-dev` creates it from `master` on
  first use and pushes it. No manual migration required.
- If `$AGENTIC_INTEGRATION_BRANCH` is unset or empty, agents fall back
  to `develop` (not master — never master). To opt out of the model
  for a single repo, set `AGENTIC_INTEGRATION_BRANCH=master` and
  agent-ops + agent-dev will treat master as the integration target
  and `/release` becomes a no-op.

## Transition cheat sheet

Task flow (sub-issues created from a story, or standalone backlog items):

    Backlog     -> ToDo        : `/pickup-task` adds `role:dev`
    ToDo        -> Progress    : agent-dev when it starts implementation
    Progress    -> CodeReview  : agent-dev on draft PR open, label -> role:reviewer
    CodeReview  -> Progress    : agent-reviewer on changes requested, label -> role:dev
    CodeReview  -> Test        : agent-reviewer on approve,          label -> role:tester
    Test        -> Progress    : agent-tester on failing impl,       label -> role:dev
    Test        -> Implemented : agent-tester on green CI,           label -> role:ops
                                 (PR target is develop, not master)
    Implemented -> Approved    : `/release` merges develop -> master, batch-moves all
                                 Implemented cards to Approved
    any         -> Blocked     : agent-dev when it must ask a question, label +blocked:question
    Blocked     -> ToDo        : human after answering (manual)
    Implemented -> Progress    : agent-ops on merge conflict, +needs:rebase, label -> role:dev
                                 (parallel-dev rebase loop; PR stays open)

Story flow (a `type:story` issue, before and around its task children):

    StoryNew    -> Analysis    : tick claims oldest StoryNew, adds `role:analyst`
    Analysis    -> Analysis*   : agent-analyst posts question, +blocked:question (waits for human)
    Analysis    -> Decomposed  : agent-analyst posts decomposition proposal (waits for analyst:approved)
    Decomposed  -> Decomposed* : agent-analyst materializes sub-issues into Backlog
                                 (story stays in Decomposed throughout child execution)
    Decomposed  -> Implemented : agent-ops when it merges the LAST open child of the story
                                 into develop
    Implemented -> Approved    : `/release` (alongside the child task cards)

Approval / blocking labels on a story:

    +blocked:question   analyst is waiting for the human to answer
    +analyst:approved   human accepted the proposed decomposition
    +role:analyst       analyst is the active role on this story
