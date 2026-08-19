# AGENT-RULES — safety policy for AgenticTeam subagents

This file is a behavioral policy. The harness's `permissions.allow` says
what the user has pre-authorized (no prompt); this file says what agents
*should* actually do with that authority. Read it together with the
repo's `CLAUDE.md` and `BOARD-OPS.md`.

If a rule here conflicts with a stricter rule in a repo's own `CLAUDE.md`,
the repo wins.

---

## 1. Branches and history

**Allowed:**
- Create feature branches off the configured integration branch (default
  `develop`).
- `git add`, `git commit`, `git push` (non-force) on feature branches.
- `git fetch`, `git checkout`, `git stash`.
- Open / edit / comment / mark-ready / merge own draft PRs (per role).

**Forbidden — never bypass even if shell allows it:**
- Force-push (`--force`, `-f`) on any branch. **Exception:**
  `--force-with-lease` is allowed on the agent's own feature branch in
  one specific case — `agent-dev` Step 2c (rebase after parallel-merge
  conflict), and only after a clean rebase + re-test in the same
  working directory. Never `--force` (without lease), never on shared
  branches (`master`, `main`, integration branch), never to bypass a
  failing test.
- `git reset --hard` against anything you did not create in this session.
- Rewriting published history outside the rebase exception above —
  `rebase -i` against pushed commits, `commit --amend` after push.
- Direct commits / pushes to `master`, `main`, or the integration branch.
  Always go through a PR.
- Deleting branches you did not create. Deleting `master` / `main` /
  `develop` under any circumstance.
- `git clean -fdx` outside a freshly-cloned scratch dir.

## 2. GitHub (issues, PRs, board, labels)

**Allowed:**
- Read anything (`gh issue/pr/repo/run/api GET ...`).
- Edit / comment on issues and PRs the agent owns or was assigned.
- Move project board cards within the documented columns (see BOARD-OPS).
- Create labels listed in the repo's label set; do not invent new ones.
- Merge a PR only when **all** gates documented for the agent's role pass
  (CI green, review approved, story-link present, etc.).

**Forbidden:**
- `gh repo delete`, `gh repo archive`, `gh repo rename`.
- Closing issues / PRs you did not open, except as part of an explicit
  human-approved workflow (e.g. `/release` finalization).
- Bulk operations across many issues / PRs without an explicit
  human-issued instruction in this session.
- Touching repos outside the current working tree.

## 3. Filesystem

**Allowed:**
- Read / write anywhere under `C:\GIT\ZCLOUD\` (working area).
- Read / write anywhere under `C:\TEMP\` (scratch / extraction).
- Run `mkdir` / `rm` inside `C:\TEMP\`.

**Forbidden:**
- Writes outside `C:\GIT\ZCLOUD\` or `C:\TEMP\` without explicit user OK.
- `rm -rf` against `/`, `~`, drive roots, or any path that is not a
  freshly-created scratch dir owned by this session.
- Modifying global config files (`~/.gitconfig`, `~/.ssh/`, system PATH).

## 4. Code execution and build tools

**Allowed:**
- Run the repo's documented build / test / lint commands
  (`dotnet build`, `dotnet test`, `npm run <script>`, `pytest`, etc.).
- Run small one-off `powershell -NoProfile -Command "..."` invocations
  for read-only orchestration (gh wrappers on Windows).
- Always invoke CLI tools by short name (`gh`, `git`, `jq`, `dotnet`).
  Never use absolute Windows paths like
  `"/c/Program Files/GitHub CLI/gh.exe"`, the `.exe` suffix (`gh.exe`),
  or wrap `gh` inside `PowerShell(...)` patterns — they bypass
  `Bash(gh:*)` permission patterns and trigger an approval prompt
  every call.

**Forbidden:**
- `dotnet run`, `node <script>`, `python <script>`, `bun run <script>`,
  `npx`, `bunx`, etc., on code the agent has just generated *without*
  the user explicitly asking the agent to run it. Build/test/lint = yes;
  arbitrary execution of authored code = ask first.
- Long-running daemons / servers without `run_in_background` and without
  cleanup at end of task.
- Network calls outside `gh`, `git`, package managers in their normal
  install/restore flows, and `WebFetch` to documented domains.

## 5. Secrets

- Never read or echo `~/.claude/settings.json` env values, `.env` files,
  `*.pfx`, `id_rsa*`, `appsettings.*.json` containing secrets, or
  anything matching `**/secrets/**`.
- Never paste tokens into PRs, issues, commits, or comments.
- If a secret is needed, ask the user — do not invent or harvest one.

## 6. Irreversible actions — always confirm

Even when permissions allow it, **stop and ask the user** before:
- Merging a PR into `master` / `main`.
- Running `/release`.
- Deleting any GitHub artifact (issue, PR, branch on remote, label).
- Reverting a merged commit.
- Mass label / state changes on more than 5 items.

Confirmation can be implicit if the user explicitly invoked the relevant
slash command (`/merge 42` = OK to merge #42).

## 7. Parallel-dev specifics

- In rebase mode (agent-dev Step 2c), read only diffs of conflicting
  files from competing PRs — not the full PR. Token budget matters.
- Parallel devs use worktrees under `C:/TEMP/agentic-worktrees/`.
  Two devs never share a worktree. Sequential dev stays in main checkout.
- `agent-dev` resolves merge conflicts via rebase. After 2 failed attempts,
  escalate via `dev:blocked`.
- `agent-ops` does NOT resolve conflicts — kicks back with `needs:rebase`.
  After 3 round-trips, stops and leaves `dev:blocked` for human input.
- Rebase invalidates prior approvals — `agent-dev` dismisses them in Step 2c.

## 8. Warden specifics → see `.claude/agents/agent-warden.md`

## 9. Development standards

All agents that write or review code must enforce these principles.
They are listed in priority order — when two principles conflict, the
higher one wins.

### Core principles

| Principle | Rule |
|-----------|------|
| **KISS** | Simplest solution that meets the requirement. No unnecessary complexity. |
| **DRY** | Every piece of logic exists in one place. Duplicated code = duplicated bugs. |
| **YAGNI** | Only implement what the task currently requires. No speculative features. |
| **Fail Fast** | Validate inputs at system boundaries. Fail explicitly and early, not deep inside the call stack. |
| **SoC** | Separate concerns. UI does not contain business logic; business logic does not handle persistence. |
| **CoC** | Convention over Configuration. Sensible defaults; configure only the exceptions. |

### SOLID (object-oriented code)

| Letter | Principle | Rule |
|--------|-----------|------|
| **S** | Single Responsibility | One class / method = one reason to change. |
| **O** | Open / Closed | Open for extension, closed for modification. |
| **L** | Liskov Substitution | A subtype must be fully substitutable for its parent. |
| **I** | Interface Segregation | Many small interfaces beat one large one. |
| **D** | Dependency Inversion | Depend on abstractions, not on concrete implementations. |

### Clean Code practices

| Practice | Rule |
|----------|------|
| **Meaningful names** | Variables, methods, and classes have descriptive names that reveal intent. |
| **Small functions** | A function does one thing and is short (aim for ≤ 20 lines). |
| **Boy Scout Rule** | Leave the code cleaner than you found it — but within the scope of the current task, not as scope creep. |
| **No magic numbers** | Use named constants or enums. `if (status == OrderStatus.Shipped)` not `if (status == 3)`. |
| **Early return** | Use guard clauses instead of deep nesting. |

### How agents apply these

- **agent-dev**: follows these standards when writing code. Self-checks
  before committing.
- **agent-reviewer**: evaluates the diff against these standards. A
  violation of KISS, DRY, YAGNI, SOLID, or Clean Code is a blocking
  finding — request changes.
- **agent-tester**: does not enforce standards on production code (it
  does not edit it), but applies them to test code it writes.

## 10. Repeated kickback escalation

When a reviewer or tester returns a task to `agent-dev` more than once,
the repeated cycle signals a deeper problem. Escalation rules:

### Counting kickbacks

A "kickback" is any transition that moves the card from `CodeReview` →
`Progress` or from `Test` → `Progress`. Count them per PR by counting
`CHANGES_REQUESTED` reviews (reviewer) and "Handing back to agent-dev"
comments (tester) on the PR.

### Thresholds

| Kickback # | Action |
|------------|--------|
| 1st | Normal flow — agent-dev addresses findings, no extra comment needed. |
| 2nd | The agent returning the task (reviewer or tester) **must** add a diagnostic comment explaining the pattern: what recurring issue it sees and why the previous fix was insufficient. Label the PR `quality:recurring`. |
| 3rd+ | The agent returning the task adds `needs:human` to the issue and PR, moves the card to `Blocked`, and posts a summary comment listing all rounds of findings and what was attempted. **No more agent-dev dispatches** until a human reviews and unblocks. |

## 11. When in doubt

Ask in the issue / PR thread or in the active session. Stalling is
cheaper than a bad mutation. Record blockers in the repo's `MEMORY.md`
under "Open questions" so the next agent or human can pick up.
