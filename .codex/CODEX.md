# Codex Notes for ShareWorkin

This folder is Codex's project memory area for ShareWorkin.

## Boundaries

- `.claude/` is Claude Code's memory area. Codex may read it for context when asked or when needed, but must not edit it unless the user explicitly requests it.
- `.codex/` is Codex's memory area. Other agents may read it for context, but should not edit it without the user's instruction.
- `_works/` is a communication and report area between AI agents and the user. The user may reorganize it, including moving items to `Trash`.

## User Communication

- The user does not read code directly. Explain behavior in plain operational terms, not only with file names or line numbers.
- Keep the distinction clear between what the UI shows, what the app stores, and what Windows/SMB actually enforces.
- Do not treat quick patches, notification loops, or reload buttons as substitutes for grounding the state model.

## Build And Delivery

- The expected handoff is an installer, not only source edits.
- For this session's work, v1.13 is the active version. Rebuild repeatedly as v1.13 if needed; do not bump to v1.14 just because another correction is made.
- After committing changes, rebuild the v1.13 installer so the app's top UI Git-ID matches the committed source.

## Key References

- `.codex/memory/working_rules.md`
- `.codex/memory/collaboration_support_role.md`
- `.codex/memory/share_permission_model_rules.md`
- `.codex/memory/v1.13_share_permission_notes.md`
- `_works/_app制作基準_草案6.md`
