# 3m_tool 全量编译脚本
# 编译: ZIP (x64+arm64) + MSIX (x64+arm64) + MSI (x64+arm64)
# 首次使用请先安装 WiX: winget install WiXToolset.WiXToolset
param(
    [string]$Config = "Release"
)
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$startTime = Get-Date

Write-Host "╔════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║     3m_tool Full Build Pipeline       ║" -ForegroundColor Cyan
Write-Host "║     ZIP + MSIX + MSI  (x64 + arm64)   ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── 0. Check tools ──────────────────────────────────────────────
Write-Host "[0] Checking tools..." -ForegroundColor Gray

# Rust
$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if (-not $cargo) { Write-Host "  ERROR: Rust not found. Install from https://rustup.rs/" -ForegroundColor Red; exit 1 }
Write-Host "  Rust: $(cargo --version)" -ForegroundColor Gray

# .NET
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "  ERROR: .NET SDK not found" -ForegroundColor Red; exit 1 }
Write-Host "  .NET: $(dotnet --version)" -ForegroundColor Gray

# MSIX check — built into .NET/Windows App SDK, always available
Write-Host "  MSIX: supported (Windows App SDK)" -ForegroundColor Gray

# WiX check (only needed for MSI)
$wixOk = $false
$wixFound = Get-ChildItem "${env:ProgramFiles}\WiX Toolset v*" -ErrorAction SilentlyContinue
if (-not $wixFound) { $wixFound = Get-ChildItem "${env:ProgramFiles(x86)}\WiX Toolset v*" -ErrorAction SilentlyContinue }
if ($wixFound) { $wixOk = $true; Write-Host "  WiX: found" -ForegroundColor Gray }
else { Write-Host "  WiX: NOT FOUND — MSI will be skipped" -ForegroundColor Yellow }

Write-Host ""

# ── Build queue ─────────────────────────────────────────────────
$queue = @()

# 1. ZIP
Write-Host "── [1/3] ZIP Packages ──" -ForegroundColor Yellow
foreach ($arch in @("x64", "arm64")) {
    Write-Host "  Building ZIP ($arch)..." -ForegroundColor Gray
    $zipScript = Join-Path $PSScriptRoot "build.ps1"
    & pwsh -File $zipScript -Arch $arch -Config $Config
    if ($LASTEXITCODE -eq 0) { Write-Host "  -> 3m_tool_$arch.zip" -ForegroundColor Green }
    else { Write-Host "  FAILED" -ForegroundColor Red }
}

# 2. MSIX
Write-Host ""
Write-Host "── [2/3] MSIX Packages ──" -ForegroundColor Yellow
$msixScript = Join-Path $PSScriptRoot "build_msix.ps1"
& pwsh -File $msixScript -Archs @("x64", "arm64") -Config $Config
if ($LASTEXITCODE -eq 0) { Write-Host "  -> dist/msix/" -ForegroundColor Green }
else { Write-Host "  FAILED" -ForegroundColor Red }

# 3. MSI (only if WiX available)
Write-Host ""
Write-Host "── [3/3] MSI Packages ──" -ForegroundColor Yellow
if ($wixOk) {
    $msiScript = Join-Path $PSScriptRoot "build_msi.ps1"
    & pwsh -File $msiScript -Archs @("x64", "arm64") -Config $Config
    if ($LASTEXITCODE -eq 0) { Write-Host "  -> dist/msi/" -ForegroundColor Green }
    else { Write-Host "  FAILED" -ForegroundColor Red }
}
else {
    Write-Host "  SKIPPED (WiX not installed)" -ForegroundColor Yellow
    Write-Host "  Install: winget install WiXToolset.WiXToolset" -ForegroundColor Gray
}

# ── Summary ─────────────────────────────────────────────────────
$elapsed = [math]::Round(((Get-Date) - $startTime).TotalMinutes, 1)
Write-Host ""
Write-Host "╔════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║     3m_tool Full Build Complete       ║" -ForegroundColor Green
Write-Host "║     Elapsed: ${elapsed}min                     ║" -ForegroundColor White
Write-Host "╚════════════════════════════════════════╝" -ForegroundColor Cyan

$outRoot = Join-Path $root "dist"
$items = @()
if (Test-Path $outRoot) {
    Get-ChildItem -Recurse -File $outRoot | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 2)
        $items += "  $($_.Name) ($size MB)"
    }
}
if ($items.Count -gt 0) {
    Write-Host "Artifacts:" -ForegroundColor White
    $items | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
}
