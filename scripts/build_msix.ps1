# 3m_tool MSIX 打包脚本
# 需要: .NET 8 SDK, Windows App SDK
param(
    [ValidateSet("x64", "arm64")]
    [string[]]$Archs = @("x64", "arm64"),

    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$guiDir = Join-Path $root "3m_gui"
$coreDir = Join-Path $root "3m_core"
$outDir = Join-Path $root "dist" "msix"
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  3m_tool MSIX Build" -ForegroundColor Cyan
Write-Host "  Architectures: $($Archs -join ', ')" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

foreach ($arch in $Archs) {
    $rid = "win-$arch"
    $archDir = Join-Path $outDir $arch
    $publishDir = Join-Path $archDir "publish"
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Write-Host ""
    Write-Host "── [$arch] Building ... ──" -ForegroundColor Yellow

    # 1. Build Rust backend
    Write-Host "  [1] Rust backend ($rid) ..." -ForegroundColor Gray
    $coreBuildScript = Join-Path $coreDir "scripts" "build.ps1"
    if (Test-Path $coreBuildScript) {
        & pwsh -File $coreBuildScript -Targets $rid -Config $Config
        if ($LASTEXITCODE -ne 0) { throw "Rust build failed for $arch" }
    }
    else {
        # Fallback: direct cargo build
        Push-Location $coreDir
        cargo build --release --target $rid
        Pop-Location
    }
    $coreExe = Join-Path $coreDir "target" $rid "release" "m3_core.exe"
    if (-not (Test-Path $coreExe)) {
        $coreExe = Join-Path $coreDir "build" $rid "m3_core.exe"
    }
    if (Test-Path $coreExe) {
        Copy-Item $coreExe $publishDir
        Write-Host "    -> m3_core.exe" -ForegroundColor Green
    }
    else {
        Write-Host "    WARN: m3_core.exe not found, MSIX will lack backend" -ForegroundColor Yellow
    }

    # 2. Build C# GUI with MSIX packaging
    Write-Host "  [2] C# GUI MSIX ($rid) ..." -ForegroundColor Gray
    Push-Location $guiDir

    $msixArgs = @(
        "publish",
        "-p:Platform=$arch",
        "-p:RuntimeIdentifier=$rid",
        "-c", $Config,
        "-p:AppxPackageDir=$archDir",
        "-p:AppxBundle=Always",
        "-p:AppxBundlePlatforms=$arch",
        "-p:UapAppxPackageBuildMode=SideloadOnly",
        "-p:SelfContained=true",
        "-o", $publishDir
    )
    dotnet @msixArgs
    if ($LASTEXITCODE -ne 0) { throw "MSIX build failed for $arch" }
    Pop-Location

    Write-Host "  Done: $archDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  MSIX builds complete!" -ForegroundColor Green
Write-Host "  Output: $outDir" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
