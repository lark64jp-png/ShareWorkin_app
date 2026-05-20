# Memory Index — ShareWorkin コンテンツ層

## 最優先参照
- 新セッション着手前に必ず読む: [_app制作基準_草案6.md](../../_works/_app制作基準_草案6.md)
- 現在地・次の作業: [project_current_state.md](project_current_state.md)
- 最新テストレポート: `_works/` 直下の最新ファイルを確認（0505-1 が現在の最新）

## Feedback
- [対話優先](feedback_dialogue_first.md) — **最上位**。動く前に提案形で確認。指示待ちも独走もNG
- [アンインストーラーは作らない](feedback_no_uninstaller.md) — **絶対原則**。`Uninstallable=no` 固定。§D の「hidden配置」記述と矛盾あり（§D 側が要修正）
- [バージョン番号は指示があるまで触るな](feedback_version_bump.md) — インストーラー生成≠バージョンアップ。番号変更は西村さんの明示指示のみ
- [ログを軽視するな](feedback_log_first.md) — テスト→修正サイクルの基盤。全失敗パスにログ必須。画面のエラーは事実として受け取る
- [お店比喩は導入のみ](feedback_metaphor_only_for_intro.md) — UI表現はWindows標準語彙。「お店/開店/閉店」はトップレベル導線のみ
- [訂正の意図を伝播](feedback_propagate_revision_intent.md) — 訂正の意図を関連箇所全体に揃えてから次へ進む
- [SID境界は同意UX](feedback_sid_boundary.md) — SID違い=別人と機械的に弾かない。デュアルブート等は明示的同意UXで救う
- [コンポーネント分割却下→直しミス](feedback_component_split_proposal.md) — 重複ロジック統一の提案却下が後の修正漏れバグにつながった事例
- [インストーラー時はクリーンビルド＋SHA検証](feedback_installer_build_workflow.md) — 増分ビルドの罠を避ける毎回固定の手順
- [**更新履歴の記録方針**](feedback_history_recording_philosophy.md) — 網羅性・密度の2軸。「全部出す」と「大量を避ける」は矛盾しない。機械的適用禁止
- [**UIテキストはヒエラルキーで段階的に小さく**](feedback_ui_text_hierarchy.md) — 親画面→ポップアップ→ボタンと1段ずつ。毎回聞くべき原則ではなく身体化する
- [**ポップアップ自体がコンテキスト**](feedback_popup_is_context.md) — 吹き出しの矢印が対象を指している。タイトル・対象名・説明文・重複ボタンを足さない
- [**確認コスト << 作り直しコスト**](feedback_consultation_cost_economics.md) — 「先に動く方が速い」は錯覚。2倍想定が3倍に膨らんだ実例。Auto Mode でも UI 設計判断は必ず確認

## Project
- [現在地](project_current_state.md) — v1.06作成済み・Win11実機確認待ち・次は課題1〜10
- [アイコン機能 v1.08](project_icon_feature_v108.md) — ユーザー情報の構造変更とアイコン実装。残宿題3点（一覧反映・削除掃除・微調整）
- [v1.17 cert診断・userlist-state追加](project_v1.17_cert_diagnosis.md) — cert persistence全台失敗判明・診断用state snapshot実装(2026-05-14)
- [**v1.19 履歴強化セッション**](project_v1.19_history_session.md) — 記録漏れ修正・選択色統一・PermissionCascade再帰化。インストーラー未生成
