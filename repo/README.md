# ShareWorkin

ShareWorkin は、Windows 10 / 11 の同一 LAN 環境で、共有フォルダーを「あなたのお店」として開くためのローカルアプリです。

このリポジトリの現在の版では、まずインストール可能な Windows アプリとして成立させることを目的にしています。

## 現在の状態

- WPF / .NET 8 アプリ
- 現行版: **1.18**
- 「わたしのお店」として共有する場所を1つ指定
- 「お店の中身」として選んだフォルダーの内容を表示
- フォルダーをダブルクリックして中に入れる
- ファイルをダブルクリックして開ける
- 戻る / 進むでお店の中を移動できる
- 外からファイルやフォルダーを置ける（同名がある場合は置かない）
- 新しいものが届いたときに通知を表示
- 画面から共有する場所へ見に行ける
- ウィンドウを閉じてもタスクトレイに常駐し、お店は開いたまま
- LAN 不安定時は気配の届け方を内部で切り替える（店主の操作不要）
- Inno Setup によるインストーラー作成
- インストール先: `C:\MyApps\ShareWorkin\`

### 配布物

| 種類 | 1.18 |
|---|---|
| インストーラー | `ShareWorkin_v1.18_install.exe` |
| 配布 ZIP | `ShareWorkin_v1.18_Setup.zip` |
| ハッシュ一覧（ZIP 同梱しない） | `ShareWorkin_v1.18_SHA256.txt` |
| Inno Setup スクリプト | `ShareWorkin.iss` |
| 利用案内 | `ご利用にあたって.txt` |

## ビルド

### Windows

Inno Setup 6 と .NET 8 SDK が入っている Windows 環境で、`repo/` 直下から実行します。

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

### Linux

Linux Server 上でのビルドは、WPF を扱える Windows 向け .NET SDK 経路が別途用意されている場合に限ります。
通常の Linux 用 .NET SDK だけでは `Microsoft.NET.Sdk.WindowsDesktop` が含まれないため、このプロジェクトはコンパイルできません。

前提:

- WPF を扱える Windows 向け .NET SDK/toolchain が利用可能であること
- Wine 上に `Inno Setup 6` の `ISCC.exe` があること

実行:

```bash
./build-installer-linux.sh
```

`ISCC.exe` が標準パスにない場合は、`ISCC_PATH` を指定します。

```bash
ISCC_PATH="$HOME/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe" ./build-installer-linux.sh
```

WindowsDesktop SDK が見つからない場合、スクリプトはその時点で明示的に停止します。

ビルド成果物は `repo/builds/` に出力されます。

## 方針メモ

実装判断は、プロジェクト管理ルートの `autosync/_works/0428-1生成レポート.md` と `autosync/_works/ShareWorkin_仕様書_v2_1.md` を参照します。
1.18 はフォルダー単位の共有状態を「全員 / 読みのみ / 共有OFF」で確実に揃えつつ、履歴機能の基盤を整える開発版として扱います。
機能を増やすことより、共有フォルダーを「お店」として扱う体験を崩さないことを重視します。

## 配布メモ

- 現行版は「共有フォルダーをあなたのお店として開く」ために、まずお店の中身を見て扱えるようにする版です
- コード署名は未対応です
- 配布時は `SHA-256` を配布ページ側に掲載してください。ZIP にはハッシュ一覧を同梱しません。
