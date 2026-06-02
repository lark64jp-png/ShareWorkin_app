# ShareWorkin _works GitHub sync helper

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$ScriptTarget = $ScriptDir.Substring($ProjectRoot.Path.Length + 1).Replace('\', '/')

Set-Location $ProjectRoot

Write-Host "=== ShareWorkin _works GitHub sync ==="
Write-Host "ProjectRoot: $ProjectRoot"
Write-Host ""

# Verify git repository
git rev-parse --show-toplevel | Out-Null

$SyncTarget = "_works"

if (!(Test-Path $SyncTarget)) {
    New-Item -ItemType Directory -Path $SyncTarget | Out-Null
    Write-Host "Created: $SyncTarget"
}

Write-Host "Current status:"
git status --short

Write-Host ""
Write-Host "Staging sync target..."

# Stage _works plus the sync scripts. .gitignore exclusions still apply.
git add ".gitignore"
git add "$SyncTarget"
git add "$ScriptTarget/*.ps1"
git add "$ScriptTarget/*.bat"

Write-Host ""
Write-Host "Status after staging:"
git status --short

$HasStaged = git diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "No staged changes."
    exit 0
}

$Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm"
$CommitMessage = "Update _works files $Timestamp"

Write-Host ""
Write-Host "Committing: $CommitMessage"
git commit -m "$CommitMessage"

Write-Host ""
Write-Host "Pushing to GitHub..."
git push origin main

Write-Host ""
Write-Host "Done. Final status:"
git status
