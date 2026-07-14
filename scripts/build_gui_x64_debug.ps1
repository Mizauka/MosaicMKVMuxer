# 3m_gui x64 build
param([string]$Config = "Debug")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "build" "x64" "publish"

# 1. Build core
& "$root\3m_core\scripts\windows\build_x64.ps1" -Config $Config

# 2. Clean & create publish dir
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory $publishDir -Force | Out-Null

# 3. Copy core exe
$coreBin = Join-Path $root "3m_core" "build" "win-x64" "m3_core.exe"
if (Test-Path $coreBin) { Copy-Item $coreBin $publishDir -Force }

# 4. Publish GUI
Push-Location (Join-Path $root "3m_gui")
dotnet publish -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -c $Config -o $publishDir --self-contained true
Pop-Location

# 5. Generate run.bat for Debug
if ($Config -eq "Debug") {
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
"@ | Out-File -FilePath (Join-Path $publishDir "run.bat") -Encoding ASCII
}

Write-Host "✅ GUI x64 build: $publishDir" -ForegroundColor Green
