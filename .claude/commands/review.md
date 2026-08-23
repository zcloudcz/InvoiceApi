---
description: Run AgentReviewer on a PR. Usage: /review <pr-number>
---

Dispatch the reviewer role for PR number: $ARGUMENTS

Use `subagent_type: "hydra"` with `.claude/agents/agent-reviewer.md` as
the instruction set — NOT `subagent_type: "agent-reviewer"`. See
"Role runners" in `.claude/BOARD-OPS.md` for why. Dispatching
`agent-reviewer` directly skips the Codex second opinion.

If `$ARGUMENTS` is empty, report "usage: /review <pr-number>" and stop.
