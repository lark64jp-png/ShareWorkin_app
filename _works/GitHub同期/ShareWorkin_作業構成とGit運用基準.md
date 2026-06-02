# ShareWorkin 作業構成とGit運用基準

この文書は、ShareWorkin プロジェクトにおける Codex 作業時の基準を整理したものです。今後のセッションでは、本書の内容を前提として作業を行います。

## 1. 現在のフォルダー構成

基準となる構成は以下のとおりです。

- Git/GitHub 管理本体: `H:\ShareWorkin_app\repo`
- GitHub remote: `https://github.com/lark64jp-png/ShareWorkin_app.git`
- GitHub repository: Private
- 親フォルダー: `H:\ShareWorkin_app`

Git 管理外の資料・成果物領域:

- `H:\ShareWorkin_app\_works`
- `H:\ShareWorkin_app\builds`
- `H:\ShareWorkin_app\backup`
- 必要に応じて `.codex`、`.claude`、レビュー用フォルダー

## 2. Git管理対象

Git で管理する対象は、原則として `H:\ShareWorkin_app\repo` 内の以下です。

- プログラムコード
- ソリューションファイル: `.sln`
- プロジェクトファイル: `.csproj`
- インストーラー定義: `.iss`
- README
- ビルドスクリプト
- Git 運用に必要な設定ファイル
- 上記に準ずる、再現可能な開発資産

## 3. Git管理外の自動同期・資料・成果物領域

以下は Git 管理外として扱い、GitHub へ push しません。

- `H:\ShareWorkin_app\builds`
- `H:\ShareWorkin_app\_works`
- `H:\ShareWorkin_app\backup`
- exe
- zip
- sha256
- スクリーンショット
- レビュー資料
- AI 受け渡し資料
- 一時生成物や配布用成果物

## 4. Codexが作業してよい場所

Codex がプログラム修正作業を行ってよい場所は、必ず以下とします。

- 作業ルート: `H:\ShareWorkin_app\repo`

補足:

- コード修正、設定修正、README 更新、ビルドスクリプト修正は `repo` 配下で行う
- 新規作成する運用文書や開発補助文書も、Git 管理対象である必要がある場合は `repo` 配下に置く

## 5. Codexが触らない場所

Codex は以下を通常の修正対象にしません。

- `H:\ShareWorkin_app` 直下を Git 管理ルートとして扱うこと
- `H:\ShareWorkin_app\builds`
- `H:\ShareWorkin_app\_works`
- `H:\ShareWorkin_app\backup`
- Git 管理外の `.codex`、`.claude`、レビュー用フォルダー
- 配布済み成果物、レビュー提出物、バックアップ類

## 6. 修正前後のGit確認手順

Codex 作業時は、修正前後に必ず Git 状態を確認します。

1. 作業開始前に `H:\ShareWorkin_app\repo` で `git status` を確認する
2. 既存の未コミット変更がある場合は、今回の作業対象と衝突しないか確認する
3. 修正後に再度 `git status` を確認する
4. 必要に応じて `git diff` で差分内容を確認する
5. ユーザーへ変更内容の概要と差分状況を報告する

## 7. コミット・push時の注意

- commit は、差分確認後に候補として整理して提示する
- push は必ずユーザー確認後に行う
- `builds`、`_works`、`backup` など Git 管理外領域の内容を push 対象に含めない
- 成果物バイナリやレビュー資料を誤って Git 管理対象に入れない
- コミット対象は、コード・設定・文書など再現可能な管理資産に限定する

## 8. 今後のセッション開始時に確認すべき事項

新しい Codex セッションを始める際は、以下を確認すること。

1. 作業ルートが `H:\ShareWorkin_app\repo` になっているか
2. `H:\ShareWorkin_app` 直下を Git ルートとして誤認していないか
3. GitHub remote が `https://github.com/lark64jp-png/ShareWorkin_app.git` であるか
4. 対象リポジトリが Private である前提を維持しているか
5. Git 管理外領域として `builds`、`_works`、`backup` を除外する認識があるか
6. 修正前に `git status` を確認したか
7. 作業後に差分確認と報告を行う前提になっているか

## 9. 運用上の基本原則

- Codex は `repo` を唯一のプログラム修正作業ルートとして扱う
- Git 管理対象と Git 管理外成果物を明確に分離する
- 変更の前後で Git 状態を確認し、差分を可視化する
- commit / push は無条件では行わず、ユーザー確認を前提とする

