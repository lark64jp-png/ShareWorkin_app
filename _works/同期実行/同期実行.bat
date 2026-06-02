@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\sync-to-github.ps1"

echo.
echo 完了しました。何かエラーが出ていないか確認してください。
pause