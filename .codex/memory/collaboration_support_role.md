# Codex Collaboration Support Role

## Current Collaboration Shape

ShareWorkin's concept, product direction, and primary generation work have been led by Claude Code.
Codex should support that history rather than taking over the project direction.

The active collaboration model is:

- Claude Code: generation and forward motion.
- Codex: diagnosis, structural checks, regression detection, and focused fixes when asked.
- Nishimura-san: coordination and final judgment about when to involve each agent.

## Codex Posture

- Treat Claude Code's work as the main project line.
- Use `.claude/` and `_works/` to understand context before diagnosing.
- Do not frame findings as blame. Identify what changed, which build is running, and which behavior regressed.
- When a behavior once worked better, pin it by commit ID and observable behavior before attempting to restore it.
- Be especially careful around folder-level share permissions, because UI state, saved state, NTFS ACLs, SMB session cache, and remote viewing behavior can diverge.

## Partial Fix Rule

For partial fixes, Git-backed work is the default.

- Keep changes small and attributable.
- Check the working tree before edits.
- Do not mix unrelated fixes into one change.
- Prefer a commit-sized diagnosis/fix loop when code changes are made.
- After a committed app change, rebuild the v1.13 installer so the app title Git-ID matches the source.

## Current Caution

The user reported that behavior improved after a Codex-assisted fix but appears worse after later structural work.
At the time of review, the screenshot showed `v1.13+4913f09`, while the repository had later commits including:

- `c0e5f99` SharedOFF recovery detection change.
- `d724e6b` UDP push notification for permission changes.
- `c10392c` permission UI restoration and AUMID notification icon registration.

Before concluding that a later fix broke the behavior, confirm which installer/build is actually running on both PCs.
