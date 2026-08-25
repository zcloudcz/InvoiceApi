---
description: Create a new user story issue and drop it into the StoryNew column of the board.
argument-hint: [krátký popis story]
---

Create a new `type:story` issue and put it on the AgenticTeam board in
`StoryNew`, where `/tick-stories` (or `/tick`) will pick it up and hand it
to `agent-analyst` for decomposition into tasks.

User input for this invocation: `$ARGUMENTS`

Follow these steps exactly:

## 0. Pre-flight

Verify the GitHub CLI is authenticated with a token that can see Projects.
Without the `project` scope every `gh project` call below returns 401:

    gh auth status

If the `project` scope is missing, stop and tell the user to run
`gh auth refresh -s project`. Do not attempt the refresh yourself — it is
interactive.

**Run the `jq` pipelines through the Bash tool, not PowerShell.** `jq` is
installed (1.8.2), but PowerShell strips the inner double quotes when it
passes a filter like `select(.status=="StoryNew")` to a native exe, and jq
then fails with `StoryNew/0 is not defined`. In Bash the same filter works
verbatim. If a filter must run from PowerShell, pass the value with
`--arg` instead of inlining it:

    jq -r --arg S StoryNew '.items | map(select(.status==$S)) | length'

## 1. Collect the story content

If `$ARGUMENTS` is empty, ask the user for a one-line story title and stop
until they answer. Never invent a story.

If `$ARGUMENTS` is present, treat it as the raw story idea. Derive from it:

- **Title** — short, imperative, no issue-number prefix.
- **Body** — see the template in step 2.
- **Labels** — pick from the labels that already exist in this repo
  (`gh label list`). Do not create new labels. Concretely:
  - always `type:story`
  - exactly one `area:*` that best matches the described work
    (`area:ui`, `area:backend`, `area:api`, `area:database`, …)
  - exactly one `priority:*` (default `priority:medium` when the user did
    not signal urgency)
  - add `type:bug` as well when the story describes broken behavior
    rather than new functionality

Before creating anything, search for duplicates and show the user what you
are about to file:

    gh issue list --label "type:story" --state open --limit 30 \
      --json number,title --jq '.[] | "#\(.number) \(.title)"'

Print the proposed title, labels and body, plus any near-duplicate you
found, and ask for confirmation. Proceed only after the user confirms.
This is the one human gate in this command — story content is the input
the whole downstream pipeline trusts, so a wrong title costs more than the
question does.

## 2. Create the issue

Body template — keep the headings, `agent-analyst` reads them:

    ## Story

    <the user's idea, cleaned up into 2-5 sentences; keep their wording
    where it is already precise>

    ## Motivation

    <why this is worth doing — from the user's input; write
    "(to be clarified with the analyst)" when they did not say>

    ## Acceptance criteria

    - <criterion, as concrete as the input allows>
    - <criterion>

    ## Out of scope

    - <anything the user explicitly excluded, otherwise "(nothing stated)">

Create it:

    gh issue create --title "<title>" --body "<body>" \
      --label "type:story" --label "area:<x>" --label "priority:<y>"

Capture the resulting issue number and URL.

Do NOT add `role:analyst`. Claiming the story is `/tick-stories`' job — it
adds that label when it moves the card `StoryNew` → `Analysis`. Adding it
here would make the board think an analyst is already working.

## 3. Put the card on the board in StoryNew

Add the item, then set its Status. Field-id resolution and the
`item-edit` pattern are in `.claude/BOARD-OPS.md`:

    STORY_URL=<url from step 2>
    gh project item-add "$AGENTIC_PROJECT_NUMBER" \
      --owner "$AGENTIC_PROJECT_OWNER" --url "$STORY_URL"

A freshly added card lands in the project's default column, which is not
necessarily `StoryNew` — always set the Status explicitly:

    gh project item-edit \
      --id "$ITEM_ID" \
      --project-id "$PROJECT_NODE_ID" \
      --field-id   "$STATUS_FIELD_ID" \
      --single-select-option-id "$STORYNEW_OPTION_ID"

Resolve `$ITEM_ID` by matching the new issue number in
`gh project item-list ... --format json`.

## 4. Verify

Re-read the card and confirm it really sits in `StoryNew` — an
`item-edit` against a stale field id silently leaves the card where it
was:

    gh project item-list "$AGENTIC_PROJECT_NUMBER" \
      --owner "$AGENTIC_PROJECT_OWNER" --format json --limit 200 \
      | jq -r --arg N "<issue number>" \
          '.items[] | select(.content.number == ($N|tonumber))
           | "\(.content.number)\t\(.status)\t\(.content.title)"'

If the status is not `StoryNew`, report the actual value and stop — do not
retry blindly.

## 5. Report

Print one line: `#<N> <title> → StoryNew [<labels>]` plus the issue URL,
and remind the user that `/tick-stories` (or `/tick`) is what starts the
analyst on it.

## Scope limits

- This command creates **stories only**. A story is decomposed into
  `type:task` children by `agent-analyst` — do not create task issues here.
- It does not dispatch any agent. Story stays in `StoryNew` until a tick
  claims it.
- It does not modify `MEMORY.md`. Nothing is in progress yet.
