---
name: feedback-no-uninstaller
description: アンインストーラーは作らない。これはShareWorkinの絶対設計原則。
metadata:
  type: feedback
---

ShareWorkin のインストーラーには **アンインストーラーを作らない**。

これは西村さんとの絶対合意であり、インストーラー修正のたびに必ず守る。

**Why:**  
西村さんが何度も説明してきた絶対原則。「InnoSetupの標準機能には一切頼ってはいけない」方針の直接の帰結。2026-05-15 のセッションで文書への記録漏れが判明し、§D に追記した（起草者 ClaudeCode の落ち度）。

**How to apply:**
- Inno Setup の `Uninstallable=no` を必ず設定する
- `[UninstallDelete]` セクション・`CurUninstallStepChanged` ハンドラは書かない
- `unins000.exe` / `unins000.dat` が生成・配置されないことをビルド後に確認する
- §D の「hidden で配置」という記述は、内部用 `.ico` 等の他の副産物に適用される。`unins000.exe` には適用しない

**See also:** [[feedback-installer-is-a-contract]]
