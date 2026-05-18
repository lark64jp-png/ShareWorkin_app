---
name: project_v1.18_ui_dragdrop_session
description: v1.18 セッション実装内容（アクションバー移動・ラバーバンド選択・複数移動・UIPI修正）
metadata:
  type: project
---

2026-05-16 セッションで実装した内容（インストーラー生成済み・テスト待ち）

## 実装内容

1. **アクションバーをリスト下部へ移動**
   - `SelectionActionBar` を Grid.Row 0→1 に移動（ListView は Row 0 に）
   - BorderThickness 下罫線→上罫線に変更
   - FontSize 11→10、ボタン高さ 22→20、ボタン間隔 4→8px

2. **ラバーバンド選択**（MainWindow.xaml + .cs）
   - `RubberBandCanvas`（IsHitTestVisible=False, ZIndex=10）+ `RubberBandRect` を ListView 上に追加
   - `_isRubberBanding`, `_rubberBandOrigin` フィールド追加
   - 空白クリック → ラバーバンド開始
   - **未選択アイテム行クリック → ラバーバンド開始**（選択済みアイテムクリックのみD&D）
   - `UpdateRubberBand` / `EndRubberBand` / `SelectItemsInRect` ヘルパー追加

3. **複数選択での一括移動**
   - `MoveSelectedItemsToFolder()` 共通メソッドに統合
   - `ActionBarMoveButton.Visibility` から `singleSelect` 条件を削除
   - コンテキストメニュー「フォルダーへ移動」を複数選択時にも表示

4. **外部DragDrop UIPI 解除**（requireAdministrator マニフェストが原因）
   - `ChangeWindowMessageFilterEx` P/Invoke 追加
   - `AllowExternalDragDrop()` を `MainWindow_Loaded` 先頭で呼び出し
   - WM_DROPFILES (0x0233), WM_COPYGLOBALDATA (0x0049), WM_COPYDATA (0x004A) を MSGFLT_ALLOW

## テスト待ち確認項目
- ラバーバンド：行中からの開始・D&Dとの切り替え
- デスクトップ→アプリへのドロップ（UIPI修正効果）
- 複数選択→一括移動
- 選択済みアイテムのD&D（フォルダーへの移動）が従来通り動くか

**Why:** 複数機能を同一セッションで実装したため、テスト前に整理が必要と西村さん判断。
**How to apply:** 次セッション開始時にこのメモを参照し、テスト結果のフィードバックを受けて対処する。
