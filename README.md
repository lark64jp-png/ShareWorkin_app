# ShareWorkin

ShareWorkin は、Windows 10 / 11 の同一 LAN 環境で共有フォルダーを扱いやすくするためのローカルアプリです。

このリポジトリの現在の版では、まずインストール可能な Windows アプリとして成立させることを目的にしています。

## 現在の状態

- WPF / .NET 8 の最小アプリ
- バージョン: 1.01
- Inno Setup によるインストーラー作成
- 生成ファイル名: `ShareWorkin1.01_install.exe`

## ビルド

Inno Setup 6 と .NET 8 SDK が入っている Windows PC で、リポジトリ直下から実行します。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

生成先:

```text
dist\installer\ShareWorkin1.01_install.exe
```

## 方針メモ

初版では、通知、権限設定、パスワード設定、アプリ内エクスプローラーなどは確定仕様として扱いません。
機能ごとに小さく仕様を切り出してから追加します。
