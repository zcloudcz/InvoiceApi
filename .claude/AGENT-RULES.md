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

These rules cover the multi-developer flow (`/tick-devs`, `agent-dev`
Step 2c rebase mode, `agent-ops` Step 1b conflict kickback).

**Read scope (broadened for rebase mode):**
- In Step 2c, `agent-dev` may read other PRs' issues and diffs — but
  ONLY the diffs of files that conflict on the current PR. Do not pull
  the entire competing PR diff. Token cost matters; conflict
  resolution rarely needs more than the conflicting hunks.

**Worktrees:**
- Parallel devs work in `C:/TEMP/agentic-worktrees/<repo>-task<N>`.
  Two devs must never share a worktree. Each owns its own.
- Sequential dev (single `/pickup-task` or `/tick`) stays in the main
  checkout — no worktree.
- Reviewer and ops always stay in the main checkout, regardless of how
  the dev was dispatched.

**Conflict resolution boundaries:**
- `agent-dev` resolves merge conflicts in rebase mode. After 2 failed
  rebase attempts on the same PR, escalate via `dev:blocked` — do not
  loop indefinitely.
- `agent-ops` does NOT attempt to resolve conflicts itself. On
  unmergeable PR, kick back to `agent-dev` with `needs:rebase`.
- After 3 conflict round-trips on the same PR (ops → dev → ops → dev →
  ops), `agent-ops` stops kicking back and leaves the PR with
  `dev:blocked` for human input.

**Approval invalidation after rebase:**
- Rebase changes the diff. Stale approvals must be dismissed —
  `agent-dev` does this in Step 2c.4 via `gh pr review --request-changes`
  on itself, or via the dismissals API. Reviewer must re-review the
  rebased diff before merge.

## 8. When in doubt

Ask in the issue / PR thread or in the active session. Stalling is
cheaper than a bad mutation. Record blockers in the repo's `MEMORY.md`
under "Open questions" so the next agent or human can pick up.
