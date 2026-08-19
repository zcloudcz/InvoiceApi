---
description: Promote develop to master — opens a release PR (or finalizes a merged one and batch-moves Implemented cards to Approved).
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Releases the current state of the integration branch (default
`develop`) to `master`. This is **the only command** that touches
master. Smart state machine — pick the right action based on the
current state of the two branches and any in-flight release PR.

If `$AGENTIC_INTEGRATION_BRANCH` equals `master` (a repo opted out of
the integration-branch model), this command is a no-op: print a
notice and stop.

## State machine

Resolve:

    INTEGRATION="${AGENTIC_INTEGRATION_BRANCH:-develop}"
    OWNER_REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)

If `$INTEGRATION == "master"`: print `repo opted out of integration
branch model — /release is a no-op` and stop.

Then pick the FIRST matching state:

### State A — `develop` is up-to-date with `master`

    BEHIND=$(gh api repos/$OWNER_REPO/compare/master...$INTEGRATION --jq .ahead_by)
    if [ "$BEHIND" -eq 0 ]; then echo "Nothing to release — develop is at master."; exit 0; fi

Stop. Nothing was merged into develop since the last release.

### State B — A merged release PR exists since last finalization

Search for a CLOSED + MERGED PR with base=master, head=$INTEGRATION.
For each such PR that we have not yet finalized (heuristic: any card
on the board still in `Implemented` after a merged release PR's merge
timestamp), do the **finalize** step:

1. List all cards in the `Implemented` column of the project board.
   If there are **more than 5**, AGENT-RULES §6 applies (mass state
   change): print the full list and ask for confirmation before
   touching anything. The `/release` invocation authorizes the release,
   not an unbounded board rewrite. On a non-interactive run, stop and
   report the count instead of guessing.
2. For each, move card status `Implemented` → `Approved`.
3. For each story card in `Implemented`, also move it to `Approved`
   (story rollup at release time).
4. Comment on the merged release PR:
   `Finalized: N task cards + M story cards moved to Approved.`
5. Stop. Print a summary.

This step is idempotent — re-running with all cards already in
`Approved` is a no-op.

### State C — An open release PR already exists

If there is an OPEN PR with base=master, head=$INTEGRATION:

    gh pr list --base master --head $INTEGRATION --state open --json number,url,title

Print:

    Release PR is already open: <URL>
    Merge it in GitHub (or `gh pr merge <N> --merge`), then re-run
    /release to finalize the board.

Stop. Do not create a duplicate.

### State D — No release PR yet, develop is ahead

Default case: develop has commits master does not.

1. Compose a release title from the date and short summary of what
   merged since last release:

       TITLE="Release: $(date +%Y-%m-%d)"
       BODY=$(gh api repos/$OWNER_REPO/compare/master...$INTEGRATION \
              --jq '.commits | map("- " + (.commit.message | split("\n")[0])) | .[]')

2. Open the release PR:

       gh pr create --base master --head $INTEGRATION \
         --title "$TITLE" \
         --body "$(printf '## Release contents\n\n%s\n\n## Notes\n- Promoting develop to master.\n- After merging this PR, re-run /release to batch-move Implemented cards to Approved.\n' "$BODY")"

3. Print the PR URL and stop.

   Do NOT auto-merge. The release event is the user's call — they
   merge the PR in GitHub when satisfied (CI green, optional manual
   smoke test). After merge, they re-run `/release` and we land in
   State B (finalize).

## Merge style for the release PR

When the release PR is merged in GitHub, use **Create a merge commit**
(not squash, not rebase). This preserves the squashed feature commits
on master, so master's log reads as a sequence of release commits each
fanning into per-feature commits.

Configure once per repo if needed:

    gh repo edit $OWNER_REPO --enable-merge-commit --enable-squash-merge --enable-rebase-merge=false

(Enable merge commit + squash, disable rebase. agent-ops uses squash
for feature PRs into develop; this command's release PR uses merge
commit.)

## Hard rules

- Never force-push, never `git reset --hard` on master.
- Never bypass branch protection on master.
- Never create more than one open release PR at a time.
- The card moves in State B happen ONLY after the release PR is
  actually merged. Never pre-emptively move cards to Approved.
- This command is idempotent. Running it twice in any state must not
  duplicate PRs or double-move cards.
