param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "ShareWorkin\ShareWorkin.csproj"
$publishDir = Join-Path $root "dist\publish\ShareWorkin"
$installerDir = Join-Path $root "dist\installer"
$innoScript = Join-Path $root "installer\ShareWorkin.iss"
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup compiler was not found: $iscc"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerDir | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    --output $publishDir

& $iscc $innoScript

$installer = Join-Path $installerDir "ShareWorkin1.01_install.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer was not created: $installer"
}

Write-Host "Created installer: $installer"
