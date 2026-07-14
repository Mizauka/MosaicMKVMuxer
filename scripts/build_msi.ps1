# 3m_tool MSI 打包脚本
# 需要: .NET 8 SDK, WiX Toolset (winget install WiXToolset.WiXToolset)
# 首次使用请运行: winget install WiXToolset.WiXToolset
param(
    [ValidateSet("x64", "arm64")]
    [string[]]$Archs = @("x64", "arm64"),

    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$guiDir = Join-Path $root "3m_gui"
$coreDir = Join-Path $root "3m_core"
$distDir = Join-Path $root "dist" "msi"
$wixDir = Join-Path $PSScriptRoot "wix"
$wixFile = Join-Path $wixDir "3m_tool.wxs"
Remove-Item -Recurse -Force $distDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  3m_tool MSI Build" -ForegroundColor Cyan
Write-Host "  Architectures: $($Archs -join ', ')" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ── Check WiX ────────────────────────────────────────────────────
$wixFound = $false
$candle = $null; $light = $null
foreach ($c in @(
    "${env:ProgramFiles}\WiX Toolset v*\bin\candle.exe",
    "${env:ProgramFiles(x86)}\WiX Toolset v*\bin\candle.exe",
    "${env:LOCALAPPDATA}\Microsoft\WinGet\Packages\*\WiX Toolset v*\bin\candle.exe"
)) {
    $found = Get-ChildItem $c -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $wixBin = Split-Path -Parent $found.FullName
        $candle = Join-Path $wixBin "candle.exe"
        $light = Join-Path $wixBin "light.exe"
        $wixFound = $true
        Write-Host "WiX found: $wixBin" -ForegroundColor Gray
        break
    }
}
if (-not $wixFound) {
    Write-Host "ERROR: WiX Toolset not found." -ForegroundColor Red
    Write-Host "Install via: winget install WiXToolset.WiXToolset" -ForegroundColor Yellow
    Write-Host "Or download from: https://wixtoolset.org/" -ForegroundColor Yellow
    exit 1
}

foreach ($arch in $Archs) {
    $rid = "win-$arch"
    $archDir = Join-Path $distDir $arch
    $publishDir = Join-Path $archDir "publish"
    $msiDir = Join-Path $archDir "msi"
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    New-Item -ItemType Directory -Path $msiDir -Force | Out-Null

    Write-Host ""
    Write-Host "── [$arch] Building MSI ... ──" -ForegroundColor Yellow

    # 1. Build Rust
    Write-Host "  [1] Rust backend ($rid) ..." -ForegroundColor Gray
    $coreBuildScript = Join-Path $coreDir "scripts" "build.ps1"
    if (Test-Path $coreBuildScript) {
        & pwsh -File $coreBuildScript -Targets $rid -Config $Config
        if ($LASTEXITCODE -ne 0) { throw "Rust build failed" }
    }
    else {
        Push-Location $coreDir
        cargo build --release --target $rid
        Pop-Location
    }
    $coreExe = Join-Path $coreDir "target" $rid "release" "m3_core.exe"
    if (-not (Test-Path $coreExe)) { $coreExe = Join-Path $coreDir "build" $rid "m3_core.exe" }
    if (Test-Path $coreExe) { Copy-Item $coreExe $publishDir; Write-Host "    -> m3_core.exe" -ForegroundColor Green }

    # 2. Publish C# GUI
    Write-Host "  [2] C# GUI ($rid) ..." -ForegroundColor Gray
    Push-Location $guiDir
    dotnet publish -p:Platform=$arch -p:RuntimeIdentifier=$rid -c $Config -o $publishDir --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
    Pop-Location
    Write-Host "    -> ThreeMGui.exe" -ForegroundColor Green

    # 3. Harvest & build MSI
    Write-Host "  [3] Building MSI ..." -ForegroundColor Gray

    # Harvest all files from publish dir (heat)
    $harvestWxs = Join-Path $msiDir "harvest.wxs"
    & "$wixBin\heat.exe" dir $publishDir -dr INSTALLFOLDER -cg HarvestedFiles -gg -g1 -sfrag -srd -out $harvestWxs -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    WARN: heat.exe failed, using manual fallback" -ForegroundColor Yellow
        # Fallback: create minimal harvest file
        @" 
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
      <Component Id="MainApp" Guid="*" Win64="yes">
        <File Id="GuiExe" Source="$publishDir\ThreeMGui.exe" KeyPath="yes"/>
        <File Id="CoreExe" Source="$publishDir\m3_core.exe"/>
        $(Get-ChildItem $publishDir -File | Where-Object { $_.Name -notmatch '\.(exe|pdb)$' } | ForEach-Object { "<File Id=`"f_$($_.Name -replace '[^a-zA-Z0-9]','_')`" Source=`"$($_.FullName)`"/>" })
      </Component>
    </DirectoryRef>
  </Fragment>
</Wix>
"@ | Out-File $harvestWxs -Encoding UTF8
    }

    # Compile
    $wixObj = Join-Path $msiDir "3m_tool.wixobj"
    & $candle -arch $arch -dPlatform=$arch -dPublishDir=$publishDir -out $msiDir\ $wixFile $harvestWxs
    if ($LASTEXITCODE -ne 0) { throw "WiX compile failed" }

    # Link
    $msiFile = Join-Path $msiDir "3m_tool_$arch.msi"
    & $light -out $msiFile "$msiDir\3m_tool.wixobj" "$msiDir\harvest.wixobj" -ext WixUIExtension -cultures:zh-CN -nologo
    if ($LASTEXITCODE -ne 0) { throw "WiX link failed" }

    Write-Host "    -> $msiFile" -ForegroundColor Green
    Write-Host "  Done: $archDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  MSI builds complete!" -ForegroundColor Green
Write-Host "  Output: $distDir" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
