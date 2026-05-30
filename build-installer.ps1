param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$AllowDirtyGit
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "ShareWorkin\ShareWorkin.csproj"
$trayProject = Join-Path $root "ShareWorkinTray\ShareWorkinTray.csproj"
$publishDir = Join-Path $root "dist\publish\ShareWorkin"
$innoScript = Join-Path $root "ShareWorkin.iss"
$readmeName = -join ([char[]](0x3054, 0x5229, 0x7528, 0x306b, 0x3042, 0x305f, 0x3063, 0x3066)) + ".txt"
$readme = Join-Path $root $readmeName
$runtimeInstallerName = "windowsdesktop-runtime-8.0.24-win-x64.exe"
$runtimeInstaller = Join-Path $root $runtimeInstallerName
$hashFile = Join-Path $root "ShareWorkin_v1.23_SHA256.txt"
$zipFile = Join-Path $root "ShareWorkin_v1.23_Setup.zip"
$installer = Join-Path $root "ShareWorkin_v1.23_install.exe"
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
$appVersion = "1.23"
$informationalVersion = $appVersion
$gitWorkTree = $root
$gitDir = $null

function Resolve-GitContext([string]$Path) {
    try {
        $topLevel = (git -C $Path rev-parse --show-toplevel 2>$null).Trim()
        if (-not $topLevel) {
            return $null
        }

        $resolvedTopLevel = [System.IO.Path]::GetFullPath($topLevel)
        $candidateGitDir = Join-Path $resolvedTopLevel ".git"
        if (Test-Path -LiteralPath $candidateGitDir) {
            return @{
                WorkTree = $Path
                GitDir = $candidateGitDir
            }
        }

        $rawGitDir = (git -C $Path rev-parse --git-dir 2>$null).Trim()
        if (-not $rawGitDir) {
            return $null
        }

        $resolvedGitDir = if ([System.IO.Path]::IsPathRooted($rawGitDir)) {
            $rawGitDir
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $Path $rawGitDir))
        }

        return @{
            WorkTree = $Path
            GitDir = $resolvedGitDir
        }
    }
    catch {
        return $null
    }
}

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & git "--git-dir=$gitDir" "--work-tree=$gitWorkTree" @Arguments
}

function Test-GitRepository([string]$Path) {
    return $null -ne (Resolve-GitContext $Path)
}

if (-not (Test-GitRepository $root)) {
    throw "Git repository was not found at '$root'. Run git init or clone the repository, then commit your changes before building the installer."
}

$gitContext = Resolve-GitContext $root
$gitWorkTree = $gitContext.WorkTree
$gitDir = $gitContext.GitDir

try {
    $headCommit = (Invoke-Git rev-parse --verify HEAD 2>$null).Trim()
}
catch {
    $headCommit = ""
}

if (-not $headCommit) {
    throw "Git commit was not found at '$root'. Create at least one commit before building the installer."
}

if (-not $AllowDirtyGit) {
    $dirty = Invoke-Git status --porcelain --untracked-files=no
    if ($dirty) {
        throw "Uncommitted Git changes were found. Commit your changes before building the installer, or use -AllowDirtyGit only when you intentionally want to bypass this guard."
    }
}

try {
    $gitShort = (Invoke-Git rev-parse --short HEAD 2>$null).Trim()
    if ($gitShort) {
        $dirtyPaths = @(
            "README.md",
            "ShareWorkin.iss",
            "build-installer.ps1",
            "Directory.Build.props",
            "ご利用にあたって.txt",
            "ShareWorkin"
        )
        $dirty = Invoke-Git status --porcelain --untracked-files=no -- $dirtyPaths
        $suffix = if ($dirty) { ".dirty" } else { "" }
        $informationalVersion = "$appVersion+$gitShort$suffix"
    }
}
catch {
    $informationalVersion = $appVersion
}

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup compiler was not found: $iscc"
}
if (-not (Test-Path -LiteralPath $runtimeInstaller)) {
    throw "Runtime installer was not found: $runtimeInstaller"
}
if (-not (Test-Path -LiteralPath $readme)) {
    throw "Readme file was not found: $readme"
}

foreach ($procName in @("ShareWorkin", "ShareWorkinTray")) {
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
        Write-Host "終了中: $procName"
        Stop-Process -Name $procName -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Milliseconds 800

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
$cleanupPatterns = @(
    "ShareWorkin_v1.20_install*.exe",
    "ShareWorkin_v1.20_SHA256*.txt",
    "ShareWorkin_v1.20_Setup.zip",
    "ShareWorkin_v1.19_install*.exe",
    "ShareWorkin_v1.19_SHA256*.txt",
    "ShareWorkin_v1.19_Setup.zip",
    "ShareWorkin_v1.18_install*.exe",
    "ShareWorkin_v1.18_SHA256*.txt",
    "ShareWorkin_v1.18_Setup.zip",
    "ShareWorkin_v1.17_install*.exe",
    "ShareWorkin_v1.17_SHA256*.txt",
    "ShareWorkin_v1.17_Setup.zip",
    "ShareWorkin_v1.14_install*.exe",
    "ShareWorkin_v1.14_SHA256*.txt",
    "ShareWorkin_v1.14_Setup.zip",
    "ShareWorkin_v1.15_install*.exe",
    "ShareWorkin_v1.15_SHA256*.txt",
    "ShareWorkin_v1.15_Setup.zip",
    "ShareWorkin_v1.13_install*.exe",
    "ShareWorkin_v1.13_SHA256*.txt",
    "ShareWorkin_v1.13_Setup.zip",
    "ShareWorkin_v1.11_install*.exe",
    "ShareWorkin_v1.11_SHA256*.txt",
    "ShareWorkin_v1.11_Setup.zip",
    "ShareWorkin_v1.10_install*.exe",
    "ShareWorkin_v1.10_SHA256*.txt",
    "ShareWorkin_v1.10_Setup.zip",
    "ShareWorkin_v1.09_install*.exe",
    "ShareWorkin_v1.09_SHA256*.txt",
    "ShareWorkin_v1.09_Setup.zip",
    "ShareWorkin_v1.08_install*.exe",
    "ShareWorkin_v1.08_SHA256*.txt",
    "ShareWorkin_v1.08_Setup.zip",
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
    /p:InformationalVersion=$informationalVersion `
    --output $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish ShareWorkin failed (exit $LASTEXITCODE)" }

dotnet publish $trayProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:InformationalVersion=$informationalVersion `
    --output $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish ShareWorkinTray failed (exit $LASTEXITCODE)" }

& $iscc $innoScript
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer was not created: $installer"
}

$items = @($installer, (Join-Path $publishDir "ShareWorkin.exe"), (Join-Path $publishDir "ShareWorkinTray.exe"), $readme, $runtimeInstaller)
$lines = @(
    "ShareWorkin 1.21 SHA-256",
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
