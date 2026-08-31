---
description: Promote develop to TEST-ENV — rolls the release notes over and opens the promotion PR. Production is a separate step, /release-prod.
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Promotes the current state of the integration branch (default
`develop`) to **`TEST-ENV`**, the staging branch that deploys to the
test environment. This is **the only command** that touches `TEST-ENV`.

It does **not** touch `master` and it does **not** move any board card.
Both of those belong to `/release-prod`, which runs after the test
environment has been checked. Promotion is three-staged:

    develop  ──/release──▶  TEST-ENV  ──/release-prod──▶  master

If `$AGENTIC_INTEGRATION_BRANCH` equals `master` (a repo opted out of
the integration-branch model), this command is a no-op: print a
notice and stop.

## State machine

Resolve:

    INTEGRATION="${AGENTIC_INTEGRATION_BRANCH:-develop}"
    OWNER_REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)

If `$INTEGRATION == "master"`: print `repo opted out of integration
branch model — /release is a no-op` and stop.

### Step 0 — make sure `TEST-ENV` exists

Same legacy pattern `agent-dev` uses for a missing integration branch:
a repo that predates the staging stage simply does not have the branch
yet, so create it from `master` once. This is branch *creation*, not a
commit — nothing is ever committed directly to `TEST-ENV`.

    git fetch origin
    git ls-remote --exit-code --heads origin TEST-ENV >/dev/null 2>&1 \
      || git push origin origin/master:refs/heads/TEST-ENV

Then pick the FIRST matching state:

### State A — `TEST-ENV` is already up-to-date with `develop`

    AHEAD=$(gh api repos/$OWNER_REPO/compare/TEST-ENV...$INTEGRATION --jq .ahead_by)
    if [ "$AHEAD" -eq 0 ]; then echo "Nothing to promote — TEST-ENV is at develop."; exit 0; fi

Stop. Nothing was merged into develop since the last promotion. This is
also the state you land in right after the promotion PR was merged —
there is no finalize step here, the board moves happen in
`/release-prod`.

### State B — A promotion PR is already open

If there is an OPEN PR with base=TEST-ENV, head=$INTEGRATION:

    gh pr list --base TEST-ENV --head $INTEGRATION --state open --limit 1 \
      --json number,url,title --jq '.[0]'

Print:

    Promotion PR is already open: <URL>
    Merge it in GitHub (or `gh pr merge <N> --merge`), then verify the
    test environment and run /release-prod to ship to production.

Stop. Do not create a duplicate.

### State C — No promotion PR yet, develop is ahead

Default case: develop has commits `TEST-ENV` does not.

1. Compose a title from the date, and take the body from
   `release-notes.md` rather than from raw commit subjects:

       TITLE="Release: $(date +%Y-%m-%d)"

   `agent-ops` wrote one reader-facing line per merged task under
   `## Nevydáno` (see `.claude/agents/agent-ops.md` Step 2a). That section
   **is** the release body — it says what changed and why it matters, which
   commit subjects do not. Read it here, **before** step 1b renames the
   heading:

       BODY=$(awk '/^## Nevydáno$/{f=1;next} /^## /{f=0} f' release-notes.md)

   Use the raw compare output only as a cross-check:

       gh api repos/$OWNER_REPO/compare/TEST-ENV...$INTEGRATION \
         --jq '.commits | map("- " + (.commit.message | split("\n")[0])) | .[]'

   If a merged task has no line under `## Nevydáno`, that is a gap in the
   record — say so in the PR body and name the issue, rather than silently
   filling it in from the commit subject.

1b. **Roll the notes over on `develop`, before opening the PR — unless it
   has already been rolled.**

   Guard first. This command must not double-roll the notes (see Hard
   rules), and an earlier run that was interrupted after the rollover
   commit but before `gh pr create` leaves exactly that trap: rolling
   again would cut a second, empty version. Roll over only when
   `## Nevydáno` still has at least one entry under it:

       ENTRIES=$(awk '/^## Nevydáno$/{f=1;next} /^## /{f=0} f && /^- /' release-notes.md | wc -l)

   `ENTRIES` = 0 means the section is already empty — the rollover has
   happened (or there is genuinely nothing to cut). Skip it, say so in
   the run output, and continue to step 2 with the version that is
   already at the top of the file.

   Otherwise, in `release-notes.md`, rename `## Nevydáno` to
   `## <verze> — <YYYY-MM-DD>` and insert a fresh empty `## Nevydáno`
   above it. Commit as:

       chore(release-notes): close <verze>

   Without this the unreleased section grows forever and stops meaning
   anything. The rollover belongs **here and only here**: it is a commit
   on `develop`, so cutting the version at promotion time keeps
   `develop → TEST-ENV` a fast-forwardable, conflict-free merge.
   `/release-prod` only promotes what this step already wrote — it never
   edits `release-notes.md`.

   This commit lands directly on the integration branch, which is the
   second (and last) named exception in `AGENT-RULES.md` §1.

2. Open the promotion PR:

       gh pr create --base TEST-ENV --head $INTEGRATION \
         --title "$TITLE" \
         --body "$(printf '## Release contents\n\n%s\n\n## Notes\n- Promoting develop to TEST-ENV (test environment).\n- After merging this PR, verify the test environment, then run /release-prod to promote TEST-ENV to master.\n' "$BODY")"

3. Print the PR URL and stop.

   Do NOT auto-merge. The promotion event is the user's call — they
   merge the PR in GitHub when satisfied (CI green, optional manual
   smoke test).

## Merge style for the promotion PR

When the promotion PR is merged in GitHub, use **Create a merge commit**
(not squash, not rebase). This preserves the squashed feature commits
on `TEST-ENV`, so its log reads as a sequence of promotion commits each
fanning into per-feature commits.

Configure once per repo if needed:

    gh repo edit $OWNER_REPO --enable-merge-commit --enable-squash-merge --enable-rebase-merge=false

(Enable merge commit + squash, disable rebase. agent-ops uses squash
for feature PRs into develop; this command and `/release-prod` use
merge commits.)

## Hard rules

- Never force-push, never `git reset --hard` on `TEST-ENV` or `master`.
- Never commit directly to `TEST-ENV`. A fix for something found on the
  test environment goes into `develop` through a normal feature PR, and
  `/release` is run again.
- Never touch `master` — that is `/release-prod`.
- Never move board cards. `Implemented → Approved` happens in
  `/release-prod`, after production, and nowhere else.
- Never create more than one open promotion PR at a time.
- This command is idempotent. Running it twice in any state must not
  duplicate PRs or double-roll the release notes.
