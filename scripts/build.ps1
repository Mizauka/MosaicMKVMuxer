param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",

    [ValidateSet("Release", "Debug")]
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $root "build" $Arch
$publishDir = Join-Path $buildDir "publish"
$dotnetRid = "win-$Arch"

Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  3m_tool Build — $Arch / $Config" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ── 1. Rust 后端（复用 core 仓库的编译脚本）─────────────────────
Write-Host ""
Write-Host "[1/3] 编译 Rust 后端 (win-$Arch)..." -ForegroundColor Yellow
$coreScript = Join-Path $root "3m_core" "scripts" "build.ps1"
& pwsh -File $coreScript -Targets "win-$Arch" -Config $Config
if ($LASTEXITCODE -ne 0) { throw "Rust build failed" }

# 复制到 publish 目录
$coreBin = Join-Path $root "3m_core" "build" "win-$Arch" "m3_core.exe"
Copy-Item $coreBin $publishDir
Write-Host "  -> m3_core.exe copied" -ForegroundColor Green

# ── 2. C# 前端 ──────────────────────────────────────────────────
Write-Host ""
Write-Host "[2/3] 编译 C# 前端 ($dotnetRid)..." -ForegroundColor Yellow
Push-Location (Join-Path $root "3m_gui")

$publishArgs = @(
    "publish",
    "-p:Platform=$Arch",
    "-p:RuntimeIdentifier=$dotnetRid",
    "-c", $Config,
    "-o", $publishDir,
    "--self-contained", "true"
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
Write-Host "  -> ThreeMGui.exe copied" -ForegroundColor Green

# 生成 run.bat（Debug 模式下从命令行启动，日志输出到控制台）
if ($Config -eq "Debug") {
    $batPath = Join-Path $publishDir "run.bat"
    @"
@echo off
title 3m_tool Debug Console
echo ==================================
echo   3m_tool Debug — All logs below
echo ==================================
echo.
ThreeMGui.exe
echo.
echo ==================================
echo   Exited with code %%ERRORLEVEL%%
pause
"@ | Out-File -FilePath $batPath -Encoding ASCII
    Write-Host "  -> run.bat generated" -ForegroundColor Green
}

Pop-Location

# ── 4. 生成 run.bat（Debug） ────────────────────────────────────
if ($Config -eq "Debug") {
    $batPath = Join-Path $publishDir "run.bat"
    @"
@echo off
title 3m_tool Debug Console
echo ==================================
echo   3m_tool Debug — All logs below
echo ==================================
echo.
ThreeMGui.exe
echo.
echo ==================================
echo   Exited with code %%ERRORLEVEL%%
pause
"@ | Out-File -FilePath $batPath -Encoding ASCII
    Write-Host "  -> run.bat generated" -ForegroundColor Green
}

# ── 5. 打包 ZIP ─────────────────────────────────────────────────
Write-Host ""
Write-Host "[3/3] 打包..." -ForegroundColor Yellow
$zipName = "3m_tool_$Arch.zip"
$zipPath = Join-Path $root $zipName
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

$files = Get-ChildItem $publishDir -File | Where-Object {
    $_.Extension -in '.exe','.dll','.json','.xaml','.pri' -or
    $_.Name -in 'app.manifest','Package.appxmanifest'
}
Compress-Archive -Path $files.FullName -DestinationPath $zipPath -Force

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ✅ Build complete!" -ForegroundColor Green
Write-Host "  Output : $buildDir" -ForegroundColor White
Write-Host "  Zip    : $zipName ($zipSize MB)" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
