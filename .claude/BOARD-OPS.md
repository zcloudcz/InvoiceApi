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

MCP (preferred):

    mcp__plugin_github_github__issue_read
      method: "get_sub_issues", owner: "$OWNER", repo: "$REPO",
      issue_number: <S>, perPage: 100

`gh` fallback:

    gh api repos/:owner/:repo/issues/<S>/sub_issues

### Count still-open children (used by agent-ops for last-child detection)

MCP: filter the `get_sub_issues` response in code, keep entries with
`state == "open"`, take its length.

`gh` fallback:

    gh api repos/:owner/:repo/issues/<S>/sub_issues \
      --jq '[.[] | select(.state=="open")] | length'

### Find the parent of an issue

The MCP plugin does not expose a parent lookup. `agent-ops` reads the
parent number from the `Parent story:` line in `MEMORY.md` instead.
If that is unavailable:

    gh api repos/:owner/:repo/issues/<N>/parent_issue --jq '.number' 2>/dev/null

### Add a child to a parent

MCP (preferred):

    mcp__plugin_github_github__sub_issue_write
      method: "add", owner: "$OWNER", repo: "$REPO",
      issue_number: <S>, sub_issue_id: <CHILD_ID>

`sub_issue_id` is the **internal numeric `id`** of the child issue, not
its issue number. The `issue_write` MCP call returns it on `create`.

`gh` fallback:

    PARENT_ID=$(gh api repos/:owner/:repo/issues/<S>   --jq '.id')
    CHILD_ID=$(gh api  repos/:owner/:repo/issues/<NEW> --jq '.id')
    gh api -X POST repos/:owner/:repo/issues/<S>/sub_issues \
           -f sub_issue_id="$CHILD_ID"

Note: if the target GitHub instance has no sub-issues feature at all,
fall back to a `Parent story: #<S>` line in the body and a checklist on
the parent — agents should still parse the body for parent linkage.

## Worktree isolation (testers and parallel devs)

`agent-reviewer` and `agent-ops` always work in the main checkout
(`C:\GIT\ZCLOUD\<repo>`). `agent-tester` and `agent-dev` (when dispatched
in parallel) run against branches other than the main checkout's HEAD,
so they need isolation — otherwise they steal the main checkout from
each other and from the human.

Solution: `git worktree`. Single shared `.git`, one extra working
directory per active branch. Cheap (no re-fetch of objects), isolated
(separate index, HEAD, untracked files), and the main checkout stays on
its current branch.

Convention:

- Location:
  - Tester: `C:/TEMP/agentic-worktrees/<repo>-pr<N>`
  - Parallel dev: `C:/TEMP/agentic-worktrees/<repo>-task<N>`
  (`C:\TEMP\` is explicitly writable per AGENT-RULES §3.)
- Lifetime:
  - Tester: one PR test pass. Created in `agent-tester` Step 0, removed
    in Step 4 — even on handoff or failure.
  - Parallel dev: one task. Created when dispatched via `/tick-devs`,
    removed when the PR is opened (handoff to reviewer in main checkout)
    or on `dev:blocked` escalation. Sequential `agent-dev` (single
    `/pickup-task`) keeps using the main checkout — no worktree.
- Branch:
  - Tester: the PR's `headRefName`.
  - Dev: the new feature branch `feature/issue-<N>-<slug>`.
  The worktree owns its branch locally; do not check it out elsewhere.
- Cleanup:   `git worktree remove <path> --force && git worktree prune`.
             If unpushed commits exist, do NOT remove — flag on the PR
             or task issue.

Create (tester pattern):

    REPO_NAME=$(basename "$(git rev-parse --show-toplevel)")
    PR_BRANCH=$(gh pr view "$PR" --json headRefName --jq .headRefName)
    WT_DIR="C:/TEMP/agentic-worktrees/${REPO_NAME}-pr${PR}"
    mkdir -p "$(dirname "$WT_DIR")"
    git fetch origin "$PR_BRANCH"
    git worktree add -B "$PR_BRANCH" "$WT_DIR" "origin/$PR_BRANCH"
    cd "$WT_DIR"

Create (parallel dev pattern):

    REPO_NAME=$(basename "$(git rev-parse --show-toplevel)")
    INTEGRATION="${AGENTIC_INTEGRATION_BRANCH:-develop}"
    BRANCH="feature/issue-${N}-${SLUG}"
    WT_DIR="C:/TEMP/agentic-worktrees/${REPO_NAME}-task${N}"
    mkdir -p "$(dirname "$WT_DIR")"
    git fetch origin "$INTEGRATION"
    git worktree add -b "$BRANCH" "$WT_DIR" "origin/$INTEGRATION"
    cd "$WT_DIR"

Inspect / repair:

    git worktree list                          # all active worktrees
    git worktree prune                         # drop registry entries for deleted dirs

Hard rules:

- Worktrees are for testers and parallel devs only. Reviewer and ops
  stay in the main checkout. Sequential dev (single `/pickup-task`)
  also stays in the main checkout.
- Never check out the integration branch or `master` in a worktree.
- Never delete a worktree that is not yours (different repo, different
  PR / task).
- Two devs must never share a worktree. Each parallel dev owns its own
  `<repo>-task<N>` directory.

## Parallel-dev workflow labels

Used by `agent-analyst`, `agent-dev`, `agent-ops`, and `/tick-devs`.

| Label              | Set by         | Meaning                                                                 |
|--------------------|----------------|-------------------------------------------------------------------------|
| `parallel:safe`    | agent-analyst  | Sub-issue is independent of others under the same story; safe to start  |
|                    |                | concurrently with other `parallel:safe` siblings.                       |
| `needs:rebase`     | agent-ops      | Merge into integration branch failed due to conflict with another PR.   |
|                    |                | `agent-dev` must rebase with full context of the competing merged PR.   |
| `dev:blocked`      | agent-dev      | Rebase / conflict resolution beyond automatic capability (hard         |
|                    |                | conflict, retry exhausted). Awaits human.                               |
| `dev:conflict`     | agent-dev      | Reserved — pre-flight conflict signal if a future variant chooses to    |
|                    |                | block parallel dispatch instead of resolving reactively. Not used by    |
|                    |                | the default reactive flow.                                              |

Issue-body convention for dependencies (no GitHub-native field):

    depends-on: #<issue>

`agent-analyst` writes one `depends-on:` line per blocking sibling in a
sub-issue body. `/tick-devs` skips any task whose `depends-on:` targets
are still open. `parallel:safe` may coexist with `depends-on:` — the
task is parallel-safe relative to siblings *not* listed.

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
