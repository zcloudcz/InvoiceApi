---
description: Promote TEST-ENV to master — opens the production release PR (or finalizes a merged one and batch-moves Implemented cards to Approved).
---

## Preflight — gh scopes

    gh auth status 2>&1 | grep -q "'project'" || {
      echo "MISSING gh scope 'project'. Run interactively:"
      echo "  gh auth refresh -s project,workflow,read:org --hostname github.com"
      exit 1
    }

If missing, STOP and surface the command above.

## What this does

Promotes **`TEST-ENV`** to `master` — the production release. This is
**the only command** that touches `master`, and the only one that moves
cards from `Implemented` to `Approved`.

It is the second half of the three-stage promotion:

    develop  ──/release──▶  TEST-ENV  ──/release-prod──▶  master

The trigger is always human, never a tick: a human has looked at the
test environment and decided the build is good. Nothing here is
automatic on green CI — CI cannot tell you the test environment
behaved.

`release-notes.md` is **not** touched here. The version was already cut
on `develop` by `/release`; this command only promotes what is already
written.

If `$AGENTIC_INTEGRATION_BRANCH` equals `master` (a repo opted out of
the integration-branch model), this command is a no-op: print a notice
and stop.

## State machine

Resolve:

    OWNER_REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)

If `$AGENTIC_INTEGRATION_BRANCH == "master"`: print `repo opted out of
integration branch model — /release-prod is a no-op` and stop.

If `TEST-ENV` does not exist on origin, nothing was ever promoted to
it. Print `TEST-ENV does not exist — run /release first` and stop:

    git ls-remote --exit-code --heads origin TEST-ENV >/dev/null 2>&1 || stop

Then pick the FIRST matching state. **Finalization is checked before
"nothing to release"** — right after a release PR is merged `master`
and `TEST-ENV` are identical, so an "up-to-date, nothing to do" check
placed first would swallow the pending board moves.

### State A — the last merged release PR still has cards to finalize

Find the most recently merged PR with base=master, head=TEST-ENV:

    RELEASE_PR=$(gh pr list --base master --head TEST-ENV --state merged \
      --limit 1 --json number,url --jq '.[0]')

`gh pr list` returns an **array**, so take `.[0]`; it comes back empty
when nothing was ever released. No merged release PR ⇒ State A does not
apply.

Otherwise build the **finalize set**: the cards still in `Implemented`
whose code is actually in `master`.

Do not filter by time. "Issue closed at or before the release PR merged"
answers a different question — a card merged into `develop` *while* the
release PR was open closed before that merge and still missed the
promotion, so a timestamp filter would approve a card whose code is not
in `master`. The test is ancestry:

    # merge commit of the card's own feature PR (squashed onto develop)
    MERGE_SHA=$(gh pr list --state merged --search "in:body \"Closes #<issue>\"" \
      --limit 1 --json mergeCommit --jq '.[0].mergeCommit.oid')

    # in this release if and only if that commit is an ancestor of master
    git fetch origin master
    git merge-base --is-ancestor "$MERGE_SHA" origin/master   # exit 0 -> in the release

Without a local checkout the same question is one API call — `status` is
`identical` or `behind` when the commit is already in `master`, `ahead`
or `diverged` when it is not:

    gh api repos/$OWNER_REPO/compare/master...$MERGE_SHA --jq .status

**If the finalize set is empty, State A does not apply — fall through to
B / C / D.** That empty check *is* the condition: a merged release PR
never disappears, so its mere existence is not a state; only "it still
has cards waiting" is. Keying State A off the PR alone would make every
run after the first production release stop here, and a second release
PR would never be opened.

With a non-empty finalize set:

1. If the set holds **more than 5** cards, AGENT-RULES §6 applies (mass
   state change): print the full list and ask for confirmation before
   touching anything. The `/release-prod` invocation authorizes the
   release, not an unbounded board rewrite. On a non-interactive run,
   stop and report the count instead of guessing.
2. For each card in the set, move card status `Implemented` → `Approved`.
3. For each story card in `Implemented`, apply the same ancestry test to
   its children: move the story only when every child task card is in the
   finalize set or already `Approved`. A story with a child that is not
   yet in `master` stays in `Implemented` and rolls up with the next
   release.
4. Comment on the merged release PR:
   `Finalized: N task cards + M story cards moved to Approved.`
5. Stop. Print a summary.

This step is idempotent — once the cards are in `Approved` the finalize
set is empty, so the next run falls through instead of re-moving them.

### State B — `master` is up-to-date with `TEST-ENV`

    AHEAD=$(gh api repos/$OWNER_REPO/compare/master...TEST-ENV --jq .ahead_by)
    if [ "$AHEAD" -eq 0 ]; then echo "Nothing to release — master is at TEST-ENV."; exit 0; fi

Stop. Nothing has been promoted to `TEST-ENV` since the last release.

### State C — A release PR is already open

If there is an OPEN PR with base=master, head=TEST-ENV:

    gh pr list --base master --head TEST-ENV --state open --limit 1 \
      --json number,url,title --jq '.[0]'

Print:

    Release PR is already open: <URL>
    Merge it in GitHub (or `gh pr merge <N> --merge`), then re-run
    /release-prod to finalize the board.

Stop. Do not create a duplicate.

### State D — No release PR yet, `TEST-ENV` is ahead

Default case: `TEST-ENV` has commits `master` does not.

1. Compose the title and body. The body is the set of `release-notes.md`
   sections that `master` has not seen yet — `/release` already cut them
   into versioned sections on `develop`, so read them from `TEST-ENV`:

       TITLE="Production release: $(date +%Y-%m-%d)"

       # The lines release-notes.md gained between master and TEST-ENV are
       # exactly the versions master has not seen yet.
       git fetch origin master TEST-ENV
       BODY=$(git diff origin/master:release-notes.md origin/TEST-ENV:release-notes.md \
              | grep '^+[^+]' | cut -c2-)

   Cross-check against the raw compare output:

       gh api repos/$OWNER_REPO/compare/master...TEST-ENV \
         --jq '.commits | map("- " + (.commit.message | split("\n")[0])) | .[]'

2. Open the release PR:

       gh pr create --base master --head TEST-ENV \
         --title "$TITLE" \
         --body "$(printf '## Release contents\n\n%s\n\n## Notes\n- Promoting TEST-ENV to master (production).\n- Verified on the test environment before opening this PR.\n- After merging this PR, re-run /release-prod to batch-move Implemented cards to Approved.\n' "$BODY")"

3. Print the PR URL and stop.

   Do NOT auto-merge. The release event is the user's call — they merge
   the PR in GitHub once the test environment has been verified.

## Merge style for the release PR

**Create a merge commit** (not squash, not rebase), same as `/release`.
Squashing here would flatten the whole release into one commit and
`master` would lose the per-feature history that `TEST-ENV` carries.

## Hard rules

- Never force-push, never `git reset --hard` on `master` or `TEST-ENV`.
- Never bypass branch protection on `master`.
- Never commit directly to `master` or `TEST-ENV`. A fix goes into
  `develop`, then `/release`, then here.
- Never create more than one open release PR at a time.
- The card moves in State A happen ONLY after the release PR is
  actually merged. Never pre-emptively move cards to Approved.
- Never edit `release-notes.md` — that is `/release`'s job.
- This command is idempotent. Running it twice in any state must not
  duplicate PRs or double-move cards.
