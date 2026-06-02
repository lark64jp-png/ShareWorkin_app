# ShareWorkin _works GitHub sync helper
# 実行位置:
# H:\ShareWorkin_app\_works\同期実行\sync-to-github.ps1

$ErrorActionPreference = "Stop"

# このps1の位置から Git ルート H:\ShareWorkin_app へ戻る
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")

Set-Location $ProjectRoot

Write-Host "=== ShareWorkin _works GitHub sync ==="
Write-Host "ProjectRoot: $ProjectRoot"
Write-Host ""

# Gitリポジトリ確認
git rev-parse --show-toplevel | Out-Null

# 同期対象
$SyncTarget = "_works"
$ScriptTarget = "_works/同期実行"

if (!(Test-Path $SyncTarget)) {
    New-Item -ItemType Directory -Path $SyncTarget | Out-Null
    Write-Host "作成: $SyncTarget"
}

Write-Host "現在の状態を確認します..."
git status --short

Write-Host ""
Write-Host "同期対象をステージします..."

# 同期用フォルダーと実行スクリプトだけを対象にする
git add ".gitignore"
git add "$SyncTarget"
git add "$ScriptTarget/*.ps1"
git add "$ScriptTarget/*.bat"

Write-Host ""
Write-Host "ステージ後の状態:"
git status --short

$HasStaged = git diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "コミット対象の変更はありません。"
    exit 0
}

$Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm"
$CommitMessage = "Update _works files $Timestamp"

Write-Host ""
Write-Host "コミットします: $CommitMessage"
git commit -m "$CommitMessage"

Write-Host ""
Write-Host "GitHubへpushします..."
git push origin main

Write-Host ""
Write-Host "完了しました。最終状態:"
git status
