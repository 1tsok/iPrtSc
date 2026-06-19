# Builds the iPrtSc installer end-to-end.
#   1. Publishes a self-contained win-x64 build (bundles the .NET 8 runtime).
#   2. Compiles installer\iPrtSc.iss with Inno Setup into a Setup .exe.
#
# Usage (from repo root):  .\installer\build-installer.ps1
# Requires: .NET 8 SDK, and Inno Setup 6 (ISCC.exe on PATH or default install location).

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Single source of truth for the version: <Version> in the .csproj.
$csproj = "src\iPrtSc\iPrtSc.csproj"
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Could not read <Version> from $csproj." }
Write-Host "==> Building iPrtSc v$version" -ForegroundColor Cyan

Write-Host "==> Publishing self-contained win-x64 build..." -ForegroundColor Cyan
dotnet publish src/iPrtSc/iPrtSc.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true `
    -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Locate the Inno Setup compiler.
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "ISCC.exe (Inno Setup) not found. Install it: winget install -e --id JRSoftware.InnoSetup"
}

Write-Host "==> Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$version" "installer\iPrtSc.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

Write-Host "==> Done. Installer is in installer\Output\" -ForegroundColor Green
Get-ChildItem installer\Output\*.exe | Select-Object Name, @{N='SizeMB';E={[math]::Round($_.Length/1MB,1)}}
