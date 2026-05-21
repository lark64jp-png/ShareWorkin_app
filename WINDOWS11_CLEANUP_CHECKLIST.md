# Windows 11 起動時の ShareWorkin 掃除メモ

## 目的

Windows 10 側で ShareWorkin のアンインストール相当の掃除を実施済みです。

ただしこの PC はデュアルブート構成のため、Windows 11 側にも以下の System 管理物が別に残っている可能性があります。

- スケジュールタスク `\ShareWorkin\ShareWorkinTray`
- ShareWorkin が作成した SMB 共有
- ローカルユーザー `swkguest`

新規インストールの理想状態から作業を始めるため、Windows 11 側でも同じ確認と削除を行います。

## 先に Codex が掃除済みの範囲

Codex 側で実施済みの内容:

- `ShareWorkin.exe` / `ShareWorkinTray.exe` の停止
- `C:\MyApps\ShareWorkin` の削除
- `%LocalAppData%\ShareWorkin` の削除確認
- `%LocalAppData%\Programs\ShareWorkin` の削除確認

ただし、以下は管理者権限が必要だったため、手動削除が必要でした。

- スケジュールタスク
- SMB 共有
- `swkguest`

## Windows 11 側で最初に行う確認

管理者 PowerShell を開いて、次を実行します。

```powershell
Get-ScheduledTask -TaskPath "\ShareWorkin\" -ErrorAction SilentlyContinue
Get-SmbShare | Where-Object { $_.Description -like 'ShareWorkin:*' }
Get-LocalUser -Name 'swkguest' -ErrorAction SilentlyContinue
Get-ChildItem -Force C:\MyApps\ShareWorkin -ErrorAction SilentlyContinue
Get-ChildItem -Force "$env:LOCALAPPDATA\ShareWorkin" -ErrorAction SilentlyContinue
Get-ChildItem -Force "$env:LOCALAPPDATA\Programs\ShareWorkin" -ErrorAction SilentlyContinue
```

何も出なければ、その項目は掃除済みです。

## 残っていた場合の削除コマンド

```powershell
schtasks /Delete /TN "ShareWorkin\ShareWorkinTray" /F
net share 共有 /delete /y
net user swkguest /delete
```

必要ならフォルダーも削除します。

```powershell
Remove-Item -LiteralPath 'C:\MyApps\ShareWorkin' -Recurse -Force
Remove-Item -LiteralPath "$env:LOCALAPPDATA\ShareWorkin" -Recurse -Force
Remove-Item -LiteralPath "$env:LOCALAPPDATA\Programs\ShareWorkin" -Recurse -Force
```

## 最終確認

もう一度、次を実行します。

```powershell
Get-ScheduledTask -TaskPath "\ShareWorkin\" -ErrorAction SilentlyContinue
Get-SmbShare | Where-Object { $_.Description -like 'ShareWorkin:*' }
Get-LocalUser -Name 'swkguest' -ErrorAction SilentlyContinue
Get-ChildItem -Force C:\MyApps\ShareWorkin -ErrorAction SilentlyContinue
Get-ChildItem -Force "$env:LOCALAPPDATA\ShareWorkin" -ErrorAction SilentlyContinue
Get-ChildItem -Force "$env:LOCALAPPDATA\Programs\ShareWorkin" -ErrorAction SilentlyContinue
```

すべて空になれば、Windows 11 側でもアンインストール相当の初期状態を確保できています。

## この後の前提

ここまで終わったら、次の方針で見直しを進めます。

1. インストールアプリホルダー内で完結する構成を優先する
2. ただし SMB 共有、ACL、Firewall、DPAPI など System 依存が避けられない部分は例外として整理する
3. 新規インストールで理想状態になるよう、インストーラーと削除ロジックを見直す
4. 開始時点の再現は「共有フォルダー + アプリホルダーのバックアップ復元」で成立させる

## 再インストール後の確認

今回の Windows 11 確認では、掃除後に最新版インストーラーを入れたうえで、次を確認します。

### 1. ワンストップ起動

- ShareWorkin のショートカットまたは `ShareWorkin.exe` 起動 1 回で、Tray が自動起動し、UI まで開けるか
- 途中で「少し待ってから、もう一度起動してください」とならないか
- Windows 11 側の確認表示が出ても、手順を分けずに再開できるか

### 2. Tray 起動経路

- UI 起動時、まずスケジュールタスク `\ShareWorkin\ShareWorkinTray` 経由で Tray 起動を試みること
- インストール直後も `ShareWorkinTray.exe` 直起動ではなく、同タスク経由で起動すること
- Tray 起動待ちは 20 秒以内で収まるか

### 3. 無署名前提の不審さ低減

- `ShareWorkin.exe` のファイルプロパティに会社名、製品名、説明が入っているか
- `ShareWorkinTray.exe` のファイルプロパティに会社名、製品名、説明が入っているか
- 会社名が `株式会社メディアハウス` として見えるか
- インストーラーの発行元情報、サポート URL、更新 URL が設定されているか

### 4. 実機確認時の記録

- 起動確認は `アプリ都合の失敗` と `Windows 11 の警戒表示` を分けて記録する
- 「ワンストップ不成立」の判定は、Windows の確認表示込みで体感評価する
- 無署名前提で確認回数を 1 回でも減らせたかを重視する
