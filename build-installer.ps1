param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "ShareWorkin\ShareWorkin.csproj"
$publishDir = Join-Path $root "dist\publish\ShareWorkin"
$innoScript = Join-Path $root "ShareWorkin.iss"
$readme = Join-Path $root "ご利用にあたって.txt"
$runtimeInstallerName = "windowsdesktop-runtime-8.0.24-win-x64.exe"
$runtimeInstaller = Join-Path $root $runtimeInstallerName
$hashFile = Join-Path $root "ShareWorkin_v1.04_SHA256-fixed.txt"
$zipFile = Join-Path $root "ShareWorkin_v1.04_Setup.zip"
$installer = Join-Path $root "ShareWorkin_v1.04_install-fixed.exe"
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup compiler was not found: $iscc"
}
if (-not (Test-Path -LiteralPath $runtimeInstaller)) {
    throw "Runtime installer was not found: $runtimeInstaller"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
foreach ($oldOutput in @(
    $installer,
    $hashFile,
    $zipFile,
    (Join-Path $root "ShareWorkin_v1.03_SHA256-fixed.txt"),
    (Join-Path $root "ShareWorkin_v1.03_Setup.zip"),
    (Join-Path $root "ShareWorkin_v1.03_install-fixed.exe"),
    (Join-Path $root "ShareWorkin_v1.02_SHA256-fixed.txt"),
    (Join-Path $root "ShareWorkin_v1.02_Setup.zip"),
    (Join-Path $root "ShareWorkin_v1.02_install-fixed.exe"),
    (Join-Path $root "ShareWorkin_v1.02_package-fixed.zip"),
    (Join-Path $root "ShareWorkin1.02_install.exe"),
    (Join-Path $root "ShareWorkin1.02_package.zip"),
    (Join-Path $root "ShareWorkin1.02_SHA256.txt")
)) {
    if (Test-Path -LiteralPath $oldOutput) {
        Remove-Item -LiteralPath $oldOutput -Force
    }
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    --output $publishDir

& $iscc $innoScript

if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer was not created: $installer"
}

$items = @($installer, (Join-Path $publishDir "ShareWorkin.exe"), $readme, $runtimeInstaller) | Where-Object { Test-Path -LiteralPath $_ }
$lines = @(
    "ShareWorkin 1.04 SHA-256",
    "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    ""
)

foreach ($item in $items) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $item).Hash.ToLowerInvariant()
    $lines += "[$([IO.Path]::GetFileName($item))]"
    $lines += $hash
    $lines += ""
}

Set-Content -LiteralPath $hashFile -Value $lines -Encoding UTF8

$zipItems = @($installer, $runtimeInstaller, $readme) | Where-Object { Test-Path -LiteralPath $_ }
Compress-Archive -LiteralPath $zipItems -DestinationPath $zipFile -Force

Write-Host "Created installer: $installer"
Write-Host "Created SHA-256 file outside package: $hashFile"
Write-Host "Created package zip: $zipFile"

$buildDir = Join-Path $root "build"
$distDir = Join-Path $root "dist"
foreach ($dir in @($buildDir, $distDir)) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}

Write-Host "Cleaned build intermediates: $buildDir, $distDir"
