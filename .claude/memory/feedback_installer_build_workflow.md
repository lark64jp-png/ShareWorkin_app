---
name: インストーラー作成時はクリーンビルド＋SHA検証で固定
description: ShareWorkin インストーラー生成の運用フロー。増分ビルドでSHAが焼き込み更新されない罠を避けるため、毎回クリーンビルドしHEADと一致を検証する。
type: feedback
---

ShareWorkin のインストーラー作成時は、以下の順序を必ず守る。途中省略は禁止。

```
1. git status で変更が全てコミット済みであることを確認
2. dotnet clean H:\ShareWorkin_app\ShareWorkin\ShareWorkin.csproj
3. dotnet build H:\ShareWorkin_app\ShareWorkin\ShareWorkin.csproj -c Release
4. ShareWorkin.dll の InformationalVersion を読み出し、git rev-parse HEAD と一致するか検証
5. インストーラー生成スクリプト実行
```

**Why:**
- 増分ビルドはソース未変更時にコンパイルをスキップするため、以前のSHAが焼き込まれたDLLが使い回されることがある（HEADを進めてもバイナリが古いまま）
- 「ビルド→コミット」順序ミスでも同じ事故になる
- 2026-05-06 のアイコン機能セッションでも、増分ビルドの結果として古いSHA `7dad7296` が表示される現象が出た（実機確認時に発覚、その時点でcsproj側も1.06のまま放置されていた問題と重なった）

**How to apply:**
- インストーラーを作る指示が出たら、Bash/PowerShellでこの順序を機械的に実行する
- 4の検証はスクリプトに組み込んで失敗時はビルドを止める設計が望ましい（本来 §H ゲート相当）
- csproj の `<Version>` ／ `<InformationalVersion>` を更新したコミットの中で、その更新もまとめて行う（メッセージとcsprojを揃える）
- 通常開発の `dotnet build`（dev実行向け）はクリーン不要、インストーラー時のみクリーン強制
