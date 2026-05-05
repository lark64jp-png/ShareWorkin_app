param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "ShareWorkin\ShareWorkin.csproj"
$publishDir = Join-Path $root "dist\publish\ShareWorkin"
$innoScript = Join-Path $root "ShareWorkin.iss"
$readmeName = -join ([char[]](0x3054, 0x5229, 0x7528, 0x306b, 0x3042, 0x305f, 0x3063, 0x3066)) + ".txt"
$readme = Join-Path $root $readmeName
$runtimeInstallerName = "windowsdesktop-runtime-8.0.24-win-x64.exe"
$runtimeInstaller = Join-Path $root $runtimeInstallerName
$hashFile = Join-Path $root "ShareWorkin_v1.08_SHA256.txt"
$zipFile = Join-Path $root "ShareWorkin_v1.08_Setup.zip"
$installer = Join-Path $root "ShareWorkin_v1.08_install.exe"
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup compiler was not found: $iscc"
}
if (-not (Test-Path -LiteralPath $runtimeInstaller)) {
    throw "Runtime installer was not found: $runtimeInstaller"
}
if (-not (Test-Path -LiteralPath $readme)) {
    throw "Readme file was not found: $readme"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
$cleanupPatterns = @(
    "ShareWorkin_v1.07_install*.exe",
    "ShareWorkin_v1.07_SHA256*.txt",
    "ShareWorkin_v1.07_Setup.zip",
    "ShareWorkin_v1.06_install*.exe",
    "ShareWorkin_v1.06_SHA256*.txt",
    "ShareWorkin_v1.06_Setup.zip",
    "ShareWorkin_v1.05_install*.exe",
    "ShareWorkin_v1.05_SHA256*.txt",
    "ShareWorkin_v1.05_Setup.zip",
    "ShareWorkin_v1.04_install*.exe",
    "ShareWorkin_v1.04_SHA256*.txt",
    "ShareWorkin_v1.04_Setup.zip",
    "ShareWorkin_v1.03_install*.exe",
    "ShareWorkin_v1.03_SHA256*.txt",
    "ShareWorkin_v1.03_Setup.zip",
    "ShareWorkin_v1.02_install*.exe",
    "ShareWorkin_v1.02_SHA256*.txt",
    "ShareWorkin_v1.02_Setup.zip",
    "ShareWorkin_v1.02_package*.zip",
    "ShareWorkin1.02_install.exe",
    "ShareWorkin1.02_package.zip",
    "ShareWorkin1.02_SHA256.txt"
)
foreach ($pattern in $cleanupPatterns) {
    Get-ChildItem -LiteralPath $root -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
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

$items = @($installer, (Join-Path $publishDir "ShareWorkin.exe"), $readme, $runtimeInstaller)
$lines = @(
    "ShareWorkin 1.08 SHA-256",
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

$zipItems = @($installer, $runtimeInstaller, $readme)
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
