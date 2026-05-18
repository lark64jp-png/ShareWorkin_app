---
name: project_v1.19_history_session
description: v1.19 セッション実装内容（履歴記録強化・選択色統一・PermissionCascade再帰化）インストーラー未生成
metadata:
  type: project
---

2026-05-18 セッション（v1.19 履歴強化）

## 実装内容

### 履歴ウィンドウ（選択色統一）
- アクティブ・非アクティブ行選択色を `#C8D8F0` に統一
- 濃い青では文字が見づらくコピー操作に支障があったため
- `HistoryDataGrid.Resources` に HighlightBrushKey / HighlightTextBrushKey / Inactive 版を追加

### GetEventTypeText 追加訳語
- `PermissionChanged` → 共有設定変更
- `PermissionCascade` → 共有設定変更（連動）
- `Copy` → コピー
- `Log` → 記録

### 移動時パーミッション変化の履歴記録（MainWindow.cs）
`PreservePermissionOnMove` に2ケース追加：
1. 移動前に制限なし（effectiveSource == null）かつ移動先に制限がある場合
2. 移動先の制限レベルが移動元以上の場合

`AppendPermissionChangedByMoveHistory` ヘルパー追加。`PermissionToStatusText` 静的ヘルパー追加。

### コピー・配置時パーミッション継承の履歴記録
`MaybeAppendPermissionInheritedOnArrival` 追加。
呼び出し元: `CopyInternalDraggedItem`（source: MainWindow.copy）/ `PlaceExternalFiles`（source: MainWindow.place）

### PermissionCascade を再帰化
`SearchOption.AllDirectories` に変更。pathText を per-item（childParent）に変更。

**設計注記**: 大量ファイル移動時に大量エントリになる可能性を認識した上で実装。
「その事象をまず作る → そこからどうすると有効な履歴になるか考える」が方針。現状は全記録。

### 友達削除の履歴記録（FriendsWindow.cs）
`DeleteButton_Click` に `AppendFriendHistory` 追加（eventType: Delete, outcome: Success）

### CreateFolder の Note 追加（ExplorerActionService.cs）
`PathText` を `destinationPath` → `request.ParentFolder` に修正。
`Note` に親フォルダーの共有状況（parentStatus）を追加。

## テスト状況
- git clean（main ブランチ）
- **インストーラー未生成**

## 残課題
- PermissionCascade の粒度設計（大量ファイル移動3パターンの最適解）
  - パターン1: フォルダーのアクセス権変更（再帰全件）
  - パターン2: 大量ファイルをフォルダーへ移動（1ファイル1エントリ）
  - パターン3: 中身が入ったフォルダーをフォルダーへ移動（現在: 再帰全件）
- PipeServer.Start() が RestoreOpenShopIfNeeded() より先に走るレースコンディション（別セッション）

**Why:** 西村さんの「更新履歴の方針」意識テストで複数の記録漏れが発見されたセッション。
**How to apply:** 次セッション着手前にインストーラー生成・実機テストが必要。PermissionCascade 設計は実機で大量ファイル移動を試してから判断。
