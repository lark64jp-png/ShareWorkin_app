# Share Permission Model Rules

This note is a structural rule for future Codex work on ShareWorkin permissions.
Read it before touching `許可指定`, SMB access, friend visibility, or folder ownership code.

## Layer Separation

ShareWorkin permission behavior has multiple layers. Do not assume they are the same thing.

- UI state: what the list shows as `全員`, `全員R`, `指定`, `指定R`, `OFF`, `非公開`.
- Saved app state: `permissions.json`, per-item `AllowedUsers`, `IsReadOnly`, `IsSharedOff`.
- Owner-side manifest: `.swk_permissions.json` in the shop root, used by ShareWorkin friend views.
- Windows NTFS ACL: what Windows permits on the owner machine's folder tree.
- SMB share permission: what Windows permits at the network share entry.
- SMB session identity: currently all friends connect with the shared `swkguest` credential.
- Friend-side app filtering: what the remote ShareWorkin UI chooses to hide or show.

When diagnosing a bug, identify which layer is wrong. Do not fix one layer and claim the whole permission model is fixed.

## Current v1.13 Meaning

- `全員`: owner-side app should make the target and descendants inherit the shop's full shared access.
- `全員R`: owner-side app should make the target readable but not writable through `swkguest`, with children inheriting that rule.
- `共有OFF`: owner-side app should remove `swkguest` access at that target, with children inheriting that rule; friend-side UI should hide it.
- `指定`: because all friends still use one `swkguest` account, Windows ACL cannot distinguish NEC from Win10. Therefore v1.13 implements `指定` as ShareWorkin UI visibility control via `.swk_permissions.json`, not as true Windows-level per-friend security.
- `指定R`: same as `指定`, plus the friend-side ShareWorkin UI should show read-only for allowed machines.

## Important Constraint

True per-friend Windows/Explorer enforcement is impossible while all remote PCs use the same `swkguest` account.

Do not promise OS-level isolation for `指定` until the design introduces one of:

- per-friend Windows accounts,
- per-friend credentials/keys,
- or an app-mediated access model that does not expose the same SMB credential to every friend.

## Owner-Side Responsibilities

When the owner runs `共有を開く`:

- The shop root and children must become manageable as that user's shop.
- If needed, internally align ownership to the current Windows user.
- Apply ShareWorkin ACL rules before showing/publishing the opened shop.
- Use user-facing wording like `アクセス設定を整える`, not Windows ownership jargon.

When `許可指定` changes:

- Save the app state to `permissions.json`.
- Apply the relevant owner-side ACL rule for `全員`, `全員R`, and `共有OFF`.
- Publish `.swk_permissions.json` for friend-side ShareWorkin filtering.
- If `共有OFF` is selected, clear the left allowed-user list immediately and reload the right unset-user list.

When files/folders are placed, moved, renamed, or externally created while the shop is open:

- Align ownership and inherited ACLs through `SharePolicyRepair`.
- Do not leave brought-in folders with old non-inherited ACLs.

## Friend-Side Responsibilities

When viewing another shop:

- Never show `保留`.
- Never show `.swk_permissions.json`.
- Apply direct access checks for `全員R` and `共有OFF`.
- Load `.swk_permissions.json` and compare entries against `Environment.MachineName`.
- If the current machine is not allowed by a `指定` entry, hide that item in the ShareWorkin UI.
- If the current machine is allowed by `指定R`, show it as read-only in the ShareWorkin UI.

## Communication Rule

Explain this to Nishimura-san in operational terms:

- `全員R` and `共有OFF` are Windows ACL-backed.
- `指定` is currently ShareWorkin UI visibility control.
- Explorer-level per-friend blocking needs a future identity-separated design.

This distinction is not a weakness to hide; it is the honest current model and prevents misleading fixes.
