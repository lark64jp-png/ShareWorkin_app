# Codex Working Rules

## Collaboration Rules

- Respect Claude Code's `.claude/` notes as another agent's working memory.
- Keep Codex-specific memory under `.codex/`.
- Treat `_works/` as user-facing reports and handoff notes, not as stable private memory.
- If a rule is unclear, explain the uncertainty to the user before making a broad change.

## Explanation Style

- The user has said code-level explanations are not directly useful to them.
- Explain with concrete outcomes:
  - what the app shows,
  - what it saves,
  - what Windows actually enforces,
  - what the installer contains.
- File and line references are useful as support, but not as the main explanation.

## ShareWorkin Build Rule

- The user's preferred handoff is an installer.
- Do not bump versions repeatedly during iterative fixes.
- Current target: v1.13.
- It is acceptable to rebuild v1.13 multiple times.
- If Git commit happens after a build, rebuild the installer so the displayed Git-ID is correct.

## Important Project Rule

Read `_works/_app制作基準_草案6.md` before significant packaging or installer work.

